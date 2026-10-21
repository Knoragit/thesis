"""Writing played notes as a Standard MIDI File, so MuseScore can engrave them."""

from __future__ import annotations

import struct
from collections.abc import Iterable
from pathlib import Path

TICKS_PER_BEAT = 480
TEMPO_MICROS = 500_000  # 120 BPM: one second is two beats
_TICKS_PER_SECOND = TICKS_PER_BEAT * 1_000_000 / TEMPO_MICROS
MIDDLE_C = 60


def _variable_length(value: int) -> bytes:
    out = [value & 0x7F]
    value >>= 7
    while value:
        out.append(0x80 | (value & 0x7F))
        value >>= 7
    return bytes(reversed(out))


def write_notes(
    path: Path,
    notes: Iterable[tuple[int, float, float, float] | tuple[int, float, float, float, str | None]],
) -> None:
    """Writes `(note, velocity 0-1, start, end[, hand])` notes (seconds; only differences matter)
    at 120 BPM in 4/4, as two piano tracks: right hand, then left hand. MuseScore reads two
    piano tracks in a row as one piano, so the right hand goes on the treble staff and the left
    on the bass staff. A note without a hand counts as left hand below middle C."""
    notes = [(n[0], n[1], n[2], n[3], n[4] if len(n) > 4 else None) for n in notes]
    origin = min((n[2] for n in notes), default=0.0)
    hands: dict[str, list[tuple[int, int, bytes]]] = {"right": [], "left": []}
    for note, velocity, start, end, hand in notes:
        if hand not in hands:
            hand = "left" if note < MIDDLE_C else "right"
        on = round((start - origin) * _TICKS_PER_SECOND)
        off = max(on + 1, round((end - origin) * _TICKS_PER_SECOND))
        vel = max(1, min(127, round(velocity * 127)))
        hands[hand].append((on, 1, bytes([0x90, note, vel])))  # (tick, order: offs first, message)
        hands[hand].append((off, 0, bytes([0x80, note, 0])))

    tracks = []
    for name, events in ((b"Mano destra", hands["right"]), (b"Mano sinistra", hands["left"])):
        events.sort(key=lambda e: (e[0], e[1]))
        track = bytearray()
        track += b"\x00\xff\x03" + _variable_length(len(name)) + name
        if not tracks:
            track += b"\x00\xff\x51\x03" + TEMPO_MICROS.to_bytes(3, "big")
            track += b"\x00\xff\x58\x04\x04\x02\x18\x08"
        track += b"\x00\xc0\x00"
        previous = 0
        for tick, _, message in events:
            track += _variable_length(tick - previous) + message
            previous = tick
        track += b"\x00\xff\x2f\x00"
        tracks.append(struct.pack(">4sI", b"MTrk", len(track)) + bytes(track))
    header = struct.pack(">4sIHHH", b"MThd", 6, 1, len(tracks), TICKS_PER_BEAT)
    path.write_bytes(header + b"".join(tracks))
