# midi-bridge

Bridge between a Unity app and a MIDI keyboard: relays notes over UDP, plays and
follows songs, records free play, and produces PDF reports.

- Rust (`src/`): MIDI, state machine, UDP protocol, song library.
- Python (`python/`): report rendering and online song search, run as subprocesses via `uv`.
- `tests/`: Rust integration tests and a black-box pytest suite that can run against
  either this bridge or the original Python implementation.

See `docs/design-decisions.md`. Common tasks: `just lint`, `just test`, `just e2e`.
