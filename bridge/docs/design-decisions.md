# midi-bridge: design decisions

## 1. What the bridge is

A local process between the Unity app (headset) and a MIDI keyboard. Over loopback UDP it:

1. relays key presses and releases to Unity (live visual feedback);
2. plays songs on the piano and streams the same notes to Unity ("Osservatore");
3. proposes a song chord by chord ("Seguimi") and records what the user plays;
4. records free play ("Fai Tu");
5. plays the four scripted tutorial demonstrations;
6. keeps a song library (SQLite plus a folder of `.mid` files) and can fetch songs online;
7. renders a PDF/PNG/WAV report of what was played.

The reference behaviour is `PorgettoTesi/midi_bridge (1).py`.

## 2. Constraints

- **The wire interface with Unity is frozen.** Same ports, same JSON, same files on disk. Unity
  sends any *unknown* action to its note handler, so adding an event type is not safe either.
- The state machine of the app lives in Unity. The bridge keeps only what Unity cannot know.
- Everything is testable on one machine without the headset.
- Readability over micro-optimization; Python where it clearly fits better.

## 3. Architecture

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

Inside the bridge, everything funnels into one loop:

```
commands, keyboard notes, worker results ──▶ channel ──▶ event loop ──▶ Core::handle(input) ──▶ effects
                                                                                                  │
                          events to Unity · MIDI to the piano · start workers  ◀──────────────────┘
```

- **One Cargo package** (library + thin binary), not a workspace. There is a single deliverable and
  nothing is reused on its own. Separation comes from modules:

  | Module | Responsibility | I/O? |
  |---|---|---|
  | `protocol` | typed commands and events; parsing; Python-identical JSON encoding | no |
  | `midi` | file → timeline, song analysis, port selection | ports only |
  | `library` | SQLite song library, folder scan | yes |
  | `core` | the state machine | **no** (enforced by `just lint`) |
  | `runtime` | sockets, threads, subprocesses, the event loop | yes |
  | `config` | command-line options | no |

- **Sans-I/O core.** `Core::handle(input, now, library) -> Vec<Effect>` is a plain function of its
  arguments: no sockets, files, threads or clocks (time is passed in). The whole behaviour is
  unit-tested by calling it. The runtime turns datagrams, keyboard notes and worker results into
  `Input`s, and carries out the `Effect`s.
- **Threads and channels, not async.** The I/O surface is one UDP socket, one MIDI callback,
  a few short-lived workers and subprocesses. `std::thread` and an `mpsc` channel into a single
  owner loop are simpler than an async runtime, with no colouring and no shared mutable state.
- **Python for what Python is good at.** The report (matplotlib), the WAV synthesis (numpy), the
  analysis behind the report text, and web scraping. Rust owns MIDI, state, protocol, the
  library and the validation of downloaded songs. The two sides talk through subprocesses:
  JSON on stdin and stdout, files in a folder, no long-lived state, nothing blocking the
  event loop.
- **`uv`** manages the Python side. The bridge runs `uv sync --frozen --no-dev --inexact` in the
  background at start-up, so the first report does not wait for an install (`--inexact` leaves
  the development tools alone when the same environment is used for testing).

## 4. What state the bridge keeps

Only this, all inside `core::Core`:

| State | Why Unity cannot own it |
|---|---|
| follow-me chords and cursor (`next_step` carries no index) | the wire protocol sends no position |
| the free-play take and the follow-me notes | only the bridge hears the keyboard with timing |
| whether a song is playing | to refuse tutorial demos over a song |
| time of the last report (5 s debounce) | |

Statelessness is bounded by the frozen protocol: `next_step` has no argument, so the cursor has to live
here. Everything else (modes, screens, who is allowed to do what) stays in Unity.

## 5. Wire protocol (unchanged)

Inbound on 5006: `ping`, `list_songs`, `search_song`, `search_suggest`, `play_song`,
`play_song_follow`, `next_step`, `stop_song`, `play_example`, `stop_example`, `start_fai_tu`,
`stop_fai_tu`, `note_hand`, `generate_report`, `get_report_status`.

Outbound on 5005: `bridge_ready`, `press`/`release`, `expect_notes`, `clear_scene`, `song_list`,
`search_result`, `play_result`, `example_done`, `report_ready`, `report_status`.

Bytes on the wire are identical to the original's `json.dumps` (key order, `", "` and `": "`
separators, ASCII escaping), checked in `protocol.rs` and by the e2e tests, which compare raw bytes.
Inbound parsing is as tolerant as before: malformed or unknown datagrams are ignored.

## 6. Deliberate differences from the original

Everything not listed here behaves identically; the e2e suite is the proof. Each difference has a
test that asserts the old behaviour for the original and the new behaviour for this bridge.

| | Original | Now |
|---|---|---|
| **D1** | A playback's end could cancel the next song: after "stop, then play" (what Unity sends when the user picks another song) the old song kept playing, mixed with the new one, in about one case in four | Each playback has its own cancel token; the old one is fully wound down before the next starts |
| **D2** | A missing song file: `play_song` was silent; `play_song_follow` began following the *previous* song. An unreadable file left the bridge believing a song was playing | `play_result` with `status: "error"` (Unity already handles it); state untouched |
| **D3** | The report was rendered on the command thread: pings and commands went unanswered for seconds | Rendered in a subprocess; the bridge stays responsive. Only one report renders at a time |
| **D4** | A failed report sent nothing, so Unity waited for its 120 s timeout | `report_ready` with `pages: 0`, which Unity already treats as "report not found", immediately |
| **D5** | Port 5006 busy: the process stayed alive doing nothing | Exits with an error message |
| **D6** | The MIDI port was the first one not named loopMIDI/virtual/through. On macOS that is the IAC bus, never the keyboard | `iac` joins the ignored names; `--midi-in` / `--midi-out` pick a port by name |
| **D7** | Wall-clock timestamps for recorded notes | Monotonic clock (only relative times are used, so nothing observable changes) |

Quirks kept on purpose: the `LIKE` wildcards in `search_suggest` are not escaped; `next_step` after
`stop_song` continues the loaded song; an unknown tutorial challenge is silently ignored; unreadable
`.mid` files are still registered by the folder scan; tutorial timings drift by a few ms per note.

Not ported, being dead code: `recorded_follow_user_notes`, `t_first_note_fai_tu`, `t_last_note_fai_tu`.

## 7. Configuration

Command-line options, each also settable as `MIDI_BRIDGE_<OPTION>`; no config file (the bridge is
started by Unity with a fixed command line). Run `midi-bridge --help`.

The names that must match existing user data (data folder, database file, PDF name, subfolders) are
options. The code carries neutral defaults, and Unity passes the real ones in
`UdpReceiver.bridgeArguments`.

## 8. Testing

Three layers, all runnable on one Mac (`just test`, `just e2e`, `just e2e-reference`):

1. **Unit tests** (Rust, `cargo test`; Python, pytest). The core is tested without any I/O.
2. **Differential and golden tests.**
   - The MIDI reader is compared with mido, bit for bit, over generated files, together with the
     original bridge's own song-analysis functions.
   - The report renderer is compared with the original: pixel-identical page images, equal PDF
     text, equal audio samples. The Python analysis is compared with the original functions on
     random inputs.
3. **Black-box end-to-end suite.** A fake Unity (UDP), a virtual keyboard and a virtual piano
   (CoreMIDI ports), local stand-ins for the two websites. The *same scenarios* run against the
   unmodified original (`just e2e-reference`, through a thin shim that only adjusts the
   environment) and against this bridge (`just e2e`). The suite was written and validated against
   the original first, so it is a specification, not a copy of the new code.

An opt-in test (`pytest -m live`) checks that the real freemidi.org and bitmidi.com pages still match
the scraping code.

## 9. Open items

- Windows is only compile-checked here. The first run on the lab PC should confirm port names and
  that `uv` is on `PATH`.
- Unity: `UdpReceiver` now starts `bridgeExecutable bridgeArguments`. The scene was edited to match;
  both changes are uncommitted in the Unity repository.
