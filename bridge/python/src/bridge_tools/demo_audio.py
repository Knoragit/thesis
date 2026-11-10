"""Turns a bridge MIDI log (`midi-bridge --midi-log-dir DIR`, one `Note_*.txt` per run) into a
WAV for a demo video.

The log has every note the keyboard sent and the piano received, timed from when the bridge
started, which is when Play is pressed in Unity. The notes are written to a MIDI file at those
exact times (on one piano channel, pedals included) and played with FluidSynth through the piano
of MuseScore's sound font, then normalized. Usage:

    uv run --project python python -m bridge_tools.demo_audio LOG OUT.wav

FluidSynth and the sound font are found through `--fluidsynth` / `--soundfont`, the
`DEMO_AUDIO_FLUIDSYNTH` / `DEMO_AUDIO_SOUNDFONT` variables, or the usual places.
"""

from __future__ import annotations

import argparse
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

import numpy as np

TICKS_PER_BEAT = 960
TEMPO_MICROS = 500_000  # 120 BPM: 1920 ticks a second
_TICKS_PER_SECOND = TICKS_PER_BEAT * 1_000_000 / TEMPO_MICROS
# A note-on repeated on the same key this soon is the piano echoing what it received.
ECHO_SECONDS = 0.005
PEAK = 0.9  # normalized peak, about -1 dBFS
SAMPLE_RATE = 44_100

_FLUIDSYNTH_PATHS = (
    Path.home() / "Desktop/Nora/strumenti/fluidsynth/fluidsynth-v2.6.1-win10-x64-cpp11/bin/fluidsynth.exe",
)
_SOUNDFONT_PATHS = (Path(r"C:\Program Files\MuseScore 4\sound\MS Basic.sf3"),)


def read_log(path: Path) -> list[tuple[float, bytes]]:
    """The log's notes and controllers as `(seconds, message)`, moved to channel 1; program
    changes are dropped so everything sounds as a piano, and echoed note-ons are left out."""
    messages: list[tuple[float, bytes]] = []
    last_on: dict[int, float] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        parts = line.split()
        if len(parts) < 3:
            continue
        try:
            seconds = float(parts[0])
            data = bytes(int(b, 16) for b in parts[2:])
        except ValueError:
            continue
        kind = data[0] & 0xF0
        if kind not in (0x80, 0x90, 0xB0) or len(data) != 3:
            continue
        if kind == 0x90 and data[2] > 0:
            if seconds - last_on.get(data[1], -1.0) < ECHO_SECONDS:
                continue
            last_on[data[1]] = seconds
        messages.append((seconds, bytes([kind, data[1], data[2]])))
    messages.sort(key=lambda m: m[0])
    return messages


def _variable_length(value: int) -> bytes:
    out = [value & 0x7F]
    value >>= 7
    while value:
        out.append(0x80 | (value & 0x7F))
        value >>= 7
    return bytes(reversed(out))


def write_midi(path: Path, messages: list[tuple[float, bytes]]) -> None:
    """One track, the times kept from the log (time 0 is when the bridge started)."""
    track = bytearray(b"\x00\xff\x51\x03" + TEMPO_MICROS.to_bytes(3, "big"))
    track += b"\x00\xc0\x00"
    previous = 0
    for seconds, message in messages:
        tick = max(previous, round(seconds * _TICKS_PER_SECOND))
        track += _variable_length(tick - previous) + message
        previous = tick
    track += b"\x00\xff\x2f\x00"
    header = struct.pack(">4sIHHH", b"MThd", 6, 0, 1, TICKS_PER_BEAT)
    path.write_bytes(header + struct.pack(">4sI", b"MTrk", len(track)) + bytes(track))


def _find(option: str | None, variable: str, name: str, paths: tuple[Path, ...]) -> Path:
    for candidate in (option, os.environ.get(variable), shutil.which(name), *paths):
        if candidate and Path(candidate).is_file():
            return Path(candidate)
    raise SystemExit(f"{name} not found: pass its path or set {variable}")


def normalize(path: Path) -> None:
    """Scales the WAV so its loudest sample is at `PEAK`."""
    with wave.open(str(path), "rb") as source:
        params = source.getparams()
        samples = np.frombuffer(source.readframes(params.nframes), dtype=np.int16)
    peak = int(np.abs(samples.astype(np.int32)).max()) if samples.size else 0
    if peak == 0:
        return
    scaled = np.clip(samples.astype(np.float64) * (PEAK * 32767 / peak), -32768, 32767)
    with wave.open(str(path), "wb") as target:
        target.setparams(params)
        target.writeframes(scaled.astype(np.int16).tobytes())


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Render the bridge's MIDI log as a WAV.")
    parser.add_argument("log", type=Path)
    parser.add_argument("out", type=Path)
    parser.add_argument("--fluidsynth")
    parser.add_argument("--soundfont")
    args = parser.parse_args(argv)

    messages = read_log(args.log)
    if not messages:
        print("the log has no notes", file=sys.stderr)
        return 1
    fluidsynth = _find(args.fluidsynth, "DEMO_AUDIO_FLUIDSYNTH", "fluidsynth", _FLUIDSYNTH_PATHS)
    soundfont = _find(args.soundfont, "DEMO_AUDIO_SOUNDFONT", "soundfont", _SOUNDFONT_PATHS)
    with tempfile.TemporaryDirectory() as scratch:
        midi = Path(scratch) / "demo.mid"
        write_midi(midi, messages)
        shutil.copyfile(midi, args.out.with_suffix(".mid"))
        subprocess.run(
            [str(fluidsynth), "-ni", "-q", "-g", "1.0", "-r", str(SAMPLE_RATE), "-O", "s16",
             "-F", str(args.out), str(soundfont), str(midi)],
            check=True,
        )
    normalize(args.out)
    notes = sum(1 for _, m in messages if m[0] == 0x90 and m[2] > 0)
    print(f"{args.out}: {notes} notes, {messages[-1][0]:.1f} s from Play")
    return 0


if __name__ == "__main__":
    sys.exit(main())
