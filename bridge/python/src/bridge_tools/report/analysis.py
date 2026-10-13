"""How a performance is measured: radar metrics, articulation, and note-by-note alignment."""

from __future__ import annotations

from dataclasses import dataclass
from itertools import pairwise
from typing import Protocol

import numpy as np

MIN_NOTE_DURATION = 0.05


class Timed(Protocol):
    note: int
    vel: float
    start: float

    @property
    def duration(self) -> float: ...


def note_color(note: int, velocity: float, hand: str | None = None) -> tuple[float, float, float]:
    """Left hand cyan to blue, right hand yellow to red, darker with velocity.

    Without hand tracking, notes below middle C count as left hand."""
    v = max(0.0, min(1.0, velocity))
    is_left = (hand == "left") if hand is not None else note < 60
    if is_left:
        return (0.0, 1.0 - v, 1.0)
    return (1.0, 0.92 * (1.0 - v), 0.0)


def radar_metrics(notes: list[Timed]) -> list[float]:
    """The five axes of the expressive "fingerprint", each from 0 to 1:
    weight, dynamic variety, legato, rubato and hand balance."""
    if not notes:
        return [0.5, 0.0, 0.5, 0.0, 0.5]

    t0 = min(n.start for n in notes)
    starts = sorted(n.start - t0 for n in notes)
    velocities = [n.vel for n in notes]
    durations = [max(MIN_NOTE_DURATION, n.duration) for n in notes]

    mean_vel = float(np.mean(velocities))
    std_vel = float(np.std(velocities)) if len(velocities) > 1 else 0.0
    mean_dur = float(np.mean(durations))
    iois = np.diff(starts) if len(starts) > 1 else [0.0]
    std_ioi = float(np.std(iois)) if len(iois) > 1 else 0.0
    vel_left = [n.vel for n in notes if n.note < 60]
    vel_right = [n.vel for n in notes if n.note >= 60]
    mean_left = float(np.mean(vel_left)) if vel_left else 0.0
    mean_right = float(np.mean(vel_right)) if vel_right else 0.0

    return [
        min(1.0, mean_vel * 1.2),
        min(1.0, std_vel * 4.0),
        min(1.0, mean_dur * 2.0),
        min(1.0, std_ioi * 3.0),
        min(1.0, (mean_right / (mean_left + 0.001)) * 0.5) if mean_left > 0 else 0.5,
    ]


def classify_articulation(notes: list[Timed]) -> dict[str, int]:
    """Counts legato / staccato / medium notes from how long each is held relative to the
    gap before the next onset."""
    counts = {"legato": 0, "staccato": 0, "medio": 0}
    if not notes:
        return counts

    t0 = min(n.start for n in notes)
    onsets = sorted(
        ((n.start - t0, max(MIN_NOTE_DURATION, n.duration)) for n in notes), key=lambda x: x[0]
    )
    gaps = [b[0] - a[0] for a, b in pairwise(onsets) if b[0] - a[0] > 0]
    median_gap = float(np.median(gaps)) if gaps else 0.2

    for i, (start, duration) in enumerate(onsets):
        gap = (
            max(MIN_NOTE_DURATION, onsets[i + 1][0] - start)
            if i + 1 < len(onsets)
            else max(MIN_NOTE_DURATION, median_gap)
        )
        ratio = duration / gap
        if ratio >= 0.85:
            counts["legato"] += 1
        elif ratio <= 0.55:
            counts["staccato"] += 1
        else:
            counts["medio"] += 1
    return counts


@dataclass
class Alignment:
    matched: int = 0
    missed: int = 0
    extra: int = 0
    vel_ratios: list[float] | None = None
    dur_ratios: list[float] | None = None
    onset_errors: list[float] | None = None

    def __post_init__(self) -> None:
        self.vel_ratios = self.vel_ratios or []
        self.dur_ratios = self.dur_ratios or []
        self.onset_errors = self.onset_errors or []


def align_notes(
    user: list, reference: list, tol: float = 0.35, ref_window: float | None = None
) -> Alignment:
    """Pairs the user's notes with the song's by pitch, in order, on relative time (each series
    measured from its own first note, since they come from different clocks).

    With `ref_window` (seconds from the reference start) only the part of the song the user got
    to is considered, so "missed" counts guide notes in the stretch actually played."""
    if not user or not reference:
        return Alignment()

    user_start = min(n.start for n in user)
    ref_start = min(n.start for n in reference)

    if ref_window is not None:
        reference = [r for r in reference if (r.start - ref_start) <= ref_window]
        if not reference:
            return Alignment()

    user_by_pitch: dict[int, list] = {}
    for u in user:
        user_by_pitch.setdefault(u.note, []).append((u.start - user_start, u))
    ref_by_pitch: dict[int, list] = {}
    for r in reference:
        ref_by_pitch.setdefault(r.note, []).append((r.start - ref_start, r))
    for entries in (*user_by_pitch.values(), *ref_by_pitch.values()):
        entries.sort(key=lambda x: x[0])

    result = Alignment()
    for note, user_entries in user_by_pitch.items():
        ref_entries = ref_by_pitch.get(note)
        if not ref_entries:
            result.extra += len(user_entries)
            continue
        for i, (user_time, u) in enumerate(user_entries):
            if i >= len(ref_entries):
                result.extra += 1
                continue
            ref_time, r = ref_entries[i]
            error = abs(user_time - ref_time)
            if error > tol:
                result.extra += 1
                continue
            result.matched += 1
            result.vel_ratios.append(min(2.0, u.vel / max(0.001, r.vel)))
            result.dur_ratios.append(
                min(2.0, max(MIN_NOTE_DURATION, u.duration) / max(MIN_NOTE_DURATION, r.duration))
            )
            result.onset_errors.append(error)

    result.missed = max(0, len(reference) - result.matched)
    return result
