# midi-bridge: design decisions

How the bridge works is described in [`architecture.md`](architecture.md). This document lists the
decisions behind it and the intended differences from the original implementation
(`PorgettoTesi/midi_bridge (1).py`).

## 1. Constraints

- The interface with Unity is fixed: ports, JSON messages, and files on disk. Unity routes unknown
  actions to its note handler, so no new event types are added.
- The application state machine stays in Unity.
- All tests run on one machine, without the headset.
- Readability takes priority over micro-optimization.

## 2. Decisions

| Decision | Reason |
|---|---|
| One Cargo package (library and binary), split into modules | A single deliverable with no reusable sub-parts |
| A core without I/O: `Core::handle(input, now, library) -> Vec<Effect>` | The behaviour is unit-testable with plain function calls |
| Threads and one `mpsc` channel instead of an async runtime | One socket, one MIDI callback and a few short-lived workers do not need async |
| One thread owns all mutable state | No locks around state |
| Python for the report, the WAV, the report analysis and web scraping | matplotlib, numpy and `requests` already cover these |
| Rust for MIDI, the protocol, state, the library and download validation | The real-time and stateful parts |
| Python tools run as subprocesses: JSON on stdin and stdout, files in a folder | No shared state; the event loop never waits on Python |
| `uv` for the Python environment; `uv sync --frozen --no-dev --inexact` at start-up | Reproducible installs; the first report does not wait for an install; development tools in the same environment are kept |
| Configuration through command-line options and `MIDI_BRIDGE_*` variables, no config file | Unity starts the bridge with a fixed command line |
| Legacy names (data folder, database file, PDF name) are options, not defaults | Unity passes them in `UdpReceiver.bridgeArguments`; the code stays name-neutral |
| JSON output identical to Python's `json.dumps` (key order, separators, ASCII escaping) | Unity receives exactly the same bytes as before |
| Malformed or unknown datagrams are ignored | Same behaviour as the original |

## 3. State kept by the bridge

| State | Why it is in the bridge |
|---|---|
| Follow-me chords and cursor | `next_step` carries no position |
| Free-play take and follow-me notes | Only the bridge receives the keyboard with timing |
| Whether a song is playing | Tutorial demonstrations are refused during a song |
| Time of the last report request | 5-second debounce |

## 4. Differences from the original

All other behaviour matches the original. Each difference is covered by an end-to-end test that
expects the original behaviour from the original and the new behaviour from this bridge.

| | Original | Now |
|---|---|---|
| **D1** | After "stop, then play" (sent by Unity when another song is picked), the previous song kept playing alongside the new one in about one case in four | Each playback has its own cancel token; the previous one ends before the next starts |
| **D2** | Missing song file: `play_song` sent nothing; `play_song_follow` followed the previous song. An unreadable file left the bridge in a "playing" state | `play_result` with `status: "error"`; state unchanged |
| **D3** | The report was rendered on the command thread; pings and commands waited | Rendered in a subprocess; one report at a time |
| **D4** | A failed report sent nothing; Unity waited for its 120 s timeout | `report_ready` with `pages: 0`, which Unity shows as "report not found" |
| **D5** | Port 5006 in use: the process kept running without listening | Exits with an error message |
| **D6** | Port selection skipped loopMIDI, virtual and through ports only; on macOS it picked the IAC bus | IAC ports are skipped too; `--midi-in` and `--midi-out` select a port by name |
| **D7** | Recorded notes used wall-clock time | Monotonic clock; only relative times are used |

Kept as in the original: `LIKE` wildcards in `search_suggest` are not escaped; `next_step` after
`stop_song` continues the loaded song; an unknown tutorial challenge is ignored; unreadable `.mid`
files are registered by the folder scan; tutorial timings drift by a few milliseconds per note.

Not ported (unused in the original): `recorded_follow_user_notes`, `t_first_note_fai_tu`,
`t_last_note_fai_tu`.

## 5. Testing

| Layer | What it checks | Command |
|---|---|---|
| Unit tests | Rust modules, including the core without I/O | `just test` |
| Differential tests | Rust MIDI reading against mido and the original's analysis functions; Python report analysis and audio against the original functions | `just test` |
| Golden tests | Report pages identical to the original's, pixel for pixel; same PDF text and audio | `just test` |
| End-to-end tests | The same scenarios against this bridge and against the original, through a fake Unity, virtual MIDI ports and local copies of the websites | `just e2e`, `just e2e-reference` |
| Live search test | The real freemidi.org and bitmidi.com pages still match the scraper | `just live` |

The end-to-end scenarios were written against the original first.

## 6. Open items

- The Windows build is compiled and linked, but has not been run on the lab PC. The first run should
  check the MIDI port names and that `uv` is on `PATH`.
- The Unity changes (`UdpReceiver.cs` and `SampleScene.unity`) are not committed in the Unity
  repository.
