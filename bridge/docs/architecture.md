# midi-bridge: how it works

This document explains how the bridge is put together and what happens, step by step, in each of
its jobs. The reasons behind the design are in [`design-decisions.md`](design-decisions.md).

## 1. The big picture

The bridge is a single process running next to the Unity app, on the same machine. It has four
neighbours:

```
┌─────────────┐   commands · UDP :5006   ┌───────────────┐   notes · MIDI in    ┌───────────────┐
│    Unity    │ ───────────────────────▶ │               │ ◀─────────────────── │               │
│  (headset)  │                          │  midi-bridge  │                      │ MIDI keyboard │
│             │ ◀─────────────────────── │    (Rust)     │ ───────────────────▶ │   and piano   │
└─────────────┘   events · UDP :5005     └───────┬───────┘   songs · MIDI out   └───────────────┘
                                                 │
                                                 │ `uv run`: JSON in, JSON out
                                                 ▼
                                    ┌─────────────────────────┐
                                    │  report-renderer        │
                                    │  song-search   (Python) │
                                    └─────────────────────────┘
```

- **Unity** sends commands as small JSON datagrams to port 5006 and listens for events on 5005.
- **The keyboard** sends note-ons and note-offs. **The piano** (often the same instrument) receives
  the songs and demonstrations the bridge plays.
- **Two Python programs** are started on demand, one per job: `report-renderer` draws the report,
  and `song-search` downloads songs from the web. Each one reads its input, writes its output and exits.

The bridge also owns a **data folder** on disk:

```
<data dir>/
├── <db file>                     SQLite: the song library (table midi_files)
├── <songs subdir>/               the .mid files
│   └── ...
└── <sessions subdir>/
    └── Sessione_YYYYMMDD_HHMMSS/ one folder per report
        ├── <report name>.pdf
        ├── pagina_1.png ...      one image per page, which Unity shows
        └── Registrazione_Audio.wav
```

The names in angle brackets are command-line options. Unity passes the real ones when it launches
the bridge (see `midi-bridge --help`).

## 2. Inside the bridge: one loop, one brain

Everything that happens reaches a single **event loop**, which asks the **core** what to do and then
does it:

```
  UDP receiver ──────┐
  keyboard callback ─┼──▶ channel ──▶ event loop ──▶ core.handle(input) ──▶ effects ──▶ executor
  worker threads ────┘                                                                      │
        ▲                                                                                   │
        └──────────────────────────────── starts playback / demo / report / search workers ◀┘
```

| Piece | Where | What it does |
|---|---|---|
| **UDP receiver** | `runtime/udp.rs`, own thread | Reads datagrams, parses them into `Command`s, ignores anything malformed |
| **Keyboard callback** | `midi/ports.rs`, thread owned by the MIDI library | Turns each incoming note-on/off into a `NoteMessage` |
| **Channel** | `std::sync::mpsc` | A queue of `Wake { input, at }`, where `at` is the moment it happened (monotonic seconds) |
| **Event loop** | `runtime/mod.rs` (`run`), the main thread | Takes one input at a time, calls the core, executes the effects |
| **Core** | `core/` | Pure logic: no sockets, files or threads. Holds the only mutable state |
| **Executor** | `runtime/mod.rs` | Turns each `Effect` into action: send a datagram, start or stop a worker |
| **Workers** | `runtime/playback.rs`, `examples.rs`, `report.rs`, `search.rs` | Background threads for anything that takes time; they report back through the channel |

The core is a function of its inputs:

```rust
core.handle(input: Input, now: f64, library: &dyn SongLibrary) -> Vec<Effect>
```

| `Input` | comes from |
|---|---|
| `Command(..)` | Unity |
| `Note(..)` | the keyboard |
| `PlaybackFinished(id)` | a playback worker that ended |
| `ReportFinished(outcome)` | the report worker |
| `SearchFinished { query, found }` | the search worker |

| `Effect` | the executor… |
|---|---|
| `Send(Event)` | sends a datagram to Unity |
| `StartPlayback { id, path }` / `StopPlayback` | starts or cancels the song player |
| `StartExample(challenge)` / `StopExample` | starts or cancels a tutorial demonstration |
| `RenderReport(SessionData)` | starts the report worker |
| `Search { query }` | starts the search worker |

Workers that only *stream* (song playback, demonstrations) send their notes straight to Unity and
the piano through shared handles (`Services`: the Unity sender, the MIDI output, the channel and the
clock). They use the channel only to say they have finished. Only the core changes state, and only
on the event loop's thread.

## 3. The state the core keeps

`core::Core` holds four things:

- **`playback`**: which song (by playback id) is playing, if any.
- **`follow`** (`core/follow.rs`): the chords of the song being followed, the cursor into them, the
  keys currently held, and every follow-me session played so far with the user's notes.
- **`recorder`** (`core/recorder.rs`): whether free play is being recorded, the keys currently held,
  the completed notes, and the latest left/right-hand hint for each key.
- **report bookkeeping**: when the last report was requested, and whether one is being rendered.

Notes are timestamped with the bridge's monotonic clock. Only differences between timestamps are
used later.

## 4. What happens, job by job

### Start-up (`runtime::run`)

1. Create the songs and sessions folders if needed.
2. Open the SQLite library (creating or migrating the table) and **scan** the songs folder. Every
   `.mid` file that is safe for the visualizer is registered.
3. Open the MIDI output (the piano) and bind UDP port 5006. If the port is taken, exit with an error.
4. Connect the MIDI input (the keyboard), start the UDP receiver, and send `bridge_ready` to Unity.
5. Start `uv sync` in the background so the Python tools are installed before they are needed.
6. Enter the event loop.

Unity also pings every second until it sees `bridge_ready`, then every 5 seconds. Each ping is
answered with `bridge_ready`.

### A key is played

```
keyboard ──▶ callback ──▶ Note(press 60, vel 100) ──▶ core
   core: send {"note": 60, "velocity": 0.787…, "action": "press"} to Unity
         if free play is recording: remember 60 as held since `at`
         if a follow-me song is active: same, for the follow session
```

On release, the same note is sent as `release`. If the key was being held in a recording, a complete
note `{note, vel, start, end}` is stored. For free play, the stored note also gets the hand hint Unity
sent earlier with `note_hand`, if there was one.

### Playing a song ("Osservatore", `play_song`)

1. The core looks the song up: by `id` in the database, or directly by `filename`. If there is no such
   song or file, it replies `play_result` with an error.
2. It assigns a new playback id and emits `StartPlayback`.
3. The executor cancels the current song and **waits for it to wind down**, then starts a new
   playback thread.
4. The playback thread (`runtime/playback.rs`):
   - reads the file into a **timeline** (`midi/timeline.rs`): all tracks merged into one list of
     messages, each with the delay before it, in seconds, tempo changes applied;
   - silences the piano (all-notes-off on the 16 channels) and sends `clear_scene`;
   - walks the timeline against a running target time, sleeping in steps of at most 1 ms so a cancel
     is noticed at once;
   - sends every message to the piano, and every note to Unity as `press`/`release`;
   - at the end (or when cancelled) silences the piano, sends `clear_scene`, and reports
     `PlaybackFinished(id)`.

`stop_song` makes the core mark nothing as playing, end follow-me, cancel the player, and send
`clear_scene` itself.

### Following a song ("Seguimi", `play_song_follow` / `next_step`)

1. The core finds the file (as above) and asks the library for its **reference notes**: every
   note-on paired with its note-off, with start time and duration, in time order (`midi/analysis.rs`).
2. The notes are grouped into **chords**: a note joins the current chord if it starts within 50 ms of
   the previous note.
3. A new follow session is recorded, and the first chord goes out as `expect_notes`.
4. Unity checks the user's keys itself. When the chord is complete it sends `next_step`, and the core
   sends the next chord. After the last chord it sends `clear_scene` and follow-me ends.

The user's notes are recorded into the session the whole time, for the report.

### Tutorial demonstrations (`play_example`)

If a song is playing or being followed, the core replies `example_done` straight away. Otherwise a
demonstration thread plays a fixed script (`core/examples.rs`): press, wait, release, wait. The script
goes to the piano and to Unity, and ends with `clear_scene` and `example_done`. `stop_example` makes it
stop at the next note boundary.

### The song library (`list_songs`, `search_suggest`)

Both are answered synchronously from SQLite (`library.rs`). `list_songs` returns every row, filling in
a title and artist from the file name when they are missing. `search_suggest` returns at most
20 rows whose title, artist, file name or query contains the typed text.

### Searching online (`search_song`)

```
core: query already searched and its file still there? ── yes ──▶ search_result (from the library)
        │ no
        ▼
search worker:
   song-search --source freemidi ──▶ up to 4 files downloaded into a scratch folder
   keep only files that parse, have a piano part, and are safe for the visualizer
   none left? try --source bitmidi the same way
   move the keepers into the songs folder
        ▼
core: register them under the query, send search_result for the first (or "not found")
```

The search tool only fetches. Deciding which songs are usable is done by the bridge with its own MIDI
code.

### Reports (`get_report_status`, `generate_report`)

`get_report_status` answers whether there is anything to report: recorded free-play notes, or a
follow-me session with notes.

`generate_report` stops free-play recording. If the last request was less than 5 seconds ago, or a
report is still rendering, nothing else happens. Otherwise:

```
core ──RenderReport(SessionData)──▶ report worker
   create Sessione_<date>_<time>/
   uv run report-renderer --out-dir <that folder> --pdf-name <name>   (session JSON on stdin)
   read {"pages": N} from its output
core ◀──ReportFinished──
   send report_ready {file, folder, pages}   (pages 0 if anything failed)
```

Unity then loads `pagina_1.png … pagina_N.png` from the folder.

## 5. The Rust–Python contract

Both tools are console scripts of the `bridge-tools` package (`python/`), run as
`uv run --project python --frozen --no-dev <tool> …`.

**`report-renderer --out-dir DIR --pdf-name NAME`**, with stdin set to the session as UTF-8 JSON:

```json
{
  "version": 1,
  "fai_tu_notes":   [{"note": 60, "vel": 0.79, "start": 12.3, "end": 12.8, "hand": "left"}],
  "follow_sessions": [{
    "filename": "Song - Artist.mid", "title": "Song", "artist": "Artist",
    "reference_notes": [{"note": 60, "vel": 0.5, "start": 0.0, "dur": 0.4}],
    "user_notes":      [{"note": 60, "vel": 0.6, "start": 40.1, "end": 40.6}]
  }]
}
```

It writes the PDF, `pagina_N.png` (120 dpi) and `Registrazione_Audio.wav` into `DIR`, and prints
`{"pages": N}`. Inside, `model.py` parses the input, `analysis.py` and `text.py` compute the metrics and
the written feedback, `pages.py` draws one matplotlib figure per page, `audio.py` synthesizes the WAV,
and `render.py` puts it together.

**`song-search --source freemidi|bitmidi --query Q --dest DIR`** downloads the site's top results (at
most 4, in parallel) into `DIR` and prints `[{"filename", "title", "artist"}, …]` in the site's order.
The site addresses can be overridden with `MIDI_BRIDGE_FREEMIDI_URL` and `MIDI_BRIDGE_BITMIDI_URL`;
the tests use this.

## 6. Source map

```
src/
├── main.rs            parse options, set up logging, call runtime::run
├── config.rs          command-line options (also readable from MIDI_BRIDGE_* variables)
├── protocol.rs        Command / Event, datagram parsing, Python-identical JSON encoding
├── midi/
│   ├── message.rs     a raw MIDI message; is it a note? a program change?
│   ├── timeline.rs    .mid file → merged list of (delay, message)
│   ├── analysis.rs    reference notes, chords, piano detection, visualizer safety
│   └── ports.rs       choosing and opening MIDI ports; the shared MIDI output handle
├── library.rs         SQLite song library and folder scan (SongLibrary trait + Library)
├── core/
│   ├── mod.rs         Core, Input, Effect: the state machine
│   ├── follow.rs      follow-me chords and sessions
│   ├── recorder.rs    free-play recording
│   ├── report.rs      SessionData and report outcomes
│   └── examples.rs    the tutorial scripts, as data
└── runtime/
    ├── mod.rs         start-up, event loop, executor
    ├── services.rs    clock, Unity sender, handles shared with workers
    ├── udp.rs         the command receiver
    ├── playback.rs    song player
    ├── examples.rs    demonstration player
    ├── report.rs      report worker
    ├── search.rs      search worker and download validation
    └── python.rs      running the Python tools through uv
python/src/bridge_tools/
├── report/            report-renderer (model, analysis, text, pages, audio, render, cli)
└── search/            song-search (sources, http, cli)
examples/dump_timeline.rs   prints how a .mid file is read (used by the differential tests)
```

## 7. How it is tested

```
                  ┌──────────── tests/e2e (pytest) ───────────┐
fake Unity (UDP) ─┤                                           ├─▶ the Rust bridge   (just e2e)
virtual keyboard ─┤  same scenarios, same expectations        │   or
virtual piano    ─┤                                           ├─▶ the original      (just e2e-reference)
fake websites    ─┤                                           │
                  └───────────────────────────────────────────┘
```

- `tests/harness/`: the fake Unity, the virtual CoreMIDI ports, fixture MIDI files and websites, and
  the two "profiles" that know how to launch each implementation.
- `tests/reference/run_reference.py`: runs the original script unchanged, with a private home folder,
  only the test MIDI ports visible, and web requests redirected to the fake sites.
- `tests/differential/`: the Rust MIDI reader against mido, and the Python analysis against the
  original functions, on generated inputs.
- `tests/golden/`: reports rendered by the original from fixed data. The new renderer must match them
  pixel for pixel.
- `tests/search/`: the search tool against the fake sites, and (with `just live`) the real ones.
- Unit tests live next to the Rust code (`#[cfg(test)]`). The core's tests use an in-memory
  `SongLibrary`.
