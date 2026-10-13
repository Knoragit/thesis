"""Deterministic report inputs, in the session JSON format the Rust bridge hands to the renderer.

Times are seconds. Free-play and user notes use an arbitrary clock origin (the bridge records
wall-clock time); reference notes start at 0 like the MIDI file they come from.
"""

from __future__ import annotations

import random
from typing import Any

SCHEMA_VERSION = 1


def _free_play(rng: random.Random, count: int, origin: float) -> list[dict[str, Any]]:
    notes, t = [], origin
    for i in range(count):
        left = i % 3 == 0
        note = rng.choice([40, 43, 48, 52, 55] if left else [62, 64, 67, 69, 72, 76])
        hand = rng.choice(["left", "right", None]) if i % 5 else ("left" if left else "right")
        dur = rng.choice([0.08, 0.2, 0.35, 0.6, 1.1])
        notes.append(
            {
                "note": note,
                "vel": round(rng.uniform(0.2, 0.95), 4),
                "start": round(t, 4),
                "end": round(t + dur, 4),
                "hand": hand,
            }
        )
        t += rng.choice([0.15, 0.25, 0.4, 0.55, 0.9])
    return notes


def _reference(rng: random.Random, count: int) -> list[dict[str, Any]]:
    notes, t = [], 0.0
    for i in range(count):
        notes.append(
            {
                "note": rng.choice([55, 60, 62, 64, 65, 67, 69, 72]),
                "vel": round(rng.uniform(0.4, 0.8), 4),
                "start": round(t, 4),
                "dur": round(rng.choice([0.2, 0.3, 0.45, 0.8]), 4),
            }
        )
        t += 0.4 if i % 4 else 0.7
    return notes


def _played_from(
    rng: random.Random, reference: list[dict[str, Any]], origin: float, keep: float
) -> list[dict[str, Any]]:
    """A user performance that follows the reference with jitter, misses and extra notes."""
    notes = []
    for ref in reference:
        if rng.random() > keep:
            continue
        start = origin + ref["start"] + rng.uniform(-0.12, 0.2)
        dur = max(0.05, ref["dur"] * rng.uniform(0.6, 1.4))
        notes.append(
            {
                "note": ref["note"],
                "vel": round(min(1.0, max(0.05, ref["vel"] + rng.uniform(-0.25, 0.25))), 4),
                "start": round(start, 4),
                "end": round(start + dur, 4),
            }
        )
    notes.append({"note": 50, "vel": 0.5, "start": origin + 1.3, "end": origin + 1.5})  # extra
    return sorted(notes, key=lambda n: n["start"])


def _session(name: str, title: str, artist: str, reference: list, user: list) -> dict[str, Any]:
    return {
        "filename": name,
        "title": title,
        "artist": artist,
        "reference_notes": reference,
        "user_notes": user,
    }


def build_cases() -> dict[str, dict[str, Any]]:
    rng = random.Random(20240607)
    ref_a = _reference(rng, 26)
    ref_b = _reference(rng, 18)
    ref_c = _reference(rng, 12)
    cases = {
        "empty": {"fai_tu_notes": [], "follow_sessions": []},
        "free_play": {
            "fai_tu_notes": _free_play(rng, 34, 5000.0),
            "follow_sessions": [],
        },
        "follow_one": {
            "fai_tu_notes": [],
            "follow_sessions": [
                _session(
                    "Gymnopedie No 1 - Satie.mid",
                    "Gymnopedie No 1",
                    "Satie",
                    ref_a,
                    _played_from(rng, ref_a, 9000.0, 0.8),
                )
            ],
        },
        "combined": {
            "fai_tu_notes": _free_play(rng, 12, 100.0),
            "follow_sessions": [
                _session(
                    "plain.mid",
                    "plain",
                    "Artista Non Specificato",
                    ref_b,
                    _played_from(rng, ref_b, 400.0, 0.9),
                ),
                # Too few notes ("partial performance" comment) and nothing matches the guide.
                _session(
                    "Short Piece - Someone.mid",
                    "Short Piece",
                    "Someone",
                    ref_c,
                    [
                        {"note": 30, "vel": 0.9, "start": 800.0 + i, "end": 800.2 + i}
                        for i in range(4)
                    ],
                ),
            ],
        },
    }
    for case in cases.values():
        case["version"] = SCHEMA_VERSION
    return cases
