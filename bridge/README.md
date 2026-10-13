# midi-bridge

Bridge between a Unity app and a MIDI keyboard: relays notes over UDP, plays and
follows songs, records free play, fetches songs online and produces PDF reports.

- **Rust** (`src/`): MIDI, the state machine, the UDP protocol, the song library.
- **Python** (`python/`): report rendering (`report-renderer`) and online song search
  (`song-search`), run as subprocesses through `uv`.
- **Tests** (`tests/`): a black-box suite that runs unchanged against this bridge and against
  the original Python implementation, plus golden and differential tests.

Design and the list of deliberate differences from the original: [`docs/design-decisions.md`](docs/design-decisions.md).

## Requirements

Rust (pinned in `rust-toolchain.toml`), [`uv`](https://docs.astral.sh/uv/), [`just`](https://just.systems).
The end-to-end tests also need macOS (CoreMIDI virtual ports). The bridge starts the Python tools
with `uv`, so `uv` must be on the `PATH` of whoever launches it.

## Everyday commands

| | |
|---|---|
| `just lint` | rustfmt, clippy, ruff, and the repository rules |
| `just test` | unit, golden and differential tests (no MIDI hardware needed) |
| `just e2e` | the black-box suite against this bridge |
| `just e2e-reference` | the same suite against the original bridge (expects it at `../PorgettoTesi/midi_bridge (1).py`, or set `BRIDGE_REFERENCE_SCRIPT`) |
| `just build-windows` | a Windows executable (cross-compiled) |
| `just live` | checks the real freemidi.org / bitmidi.com pages still match the scraper (needs internet) |
| `just fmt` | formats the Rust and Python code |

The end-to-end suites bind UDP ports 5005 and 5006: run one at a time, and not while Unity or
another bridge is running.

## Running it

```
cargo run --release -- --help
cargo run --release -- --data-dir ~/Desktop/midi-bridge
```

Unity normally starts the bridge itself (`UdpReceiver.bridgeExecutable` and `bridgeArguments`).
