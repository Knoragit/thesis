"""The refactored analysis and audio code must agree with the original bridge's functions."""

from __future__ import annotations

import random
import tempfile
import wave
from pathlib import Path

import numpy as np
import pytest
from bridge_tools.report import analysis, audio
from bridge_tools.report.model import PlayedNote, ReferenceNote
from golden.generate_golden import load_original


@pytest.fixture(scope="module")
def original():
    with tempfile.TemporaryDirectory() as home:
        yield load_original(Path(home))


def user_notes(rng: random.Random, count: int) -> list[PlayedNote]:
    t = rng.uniform(0, 5000)
    notes = []
    for _ in range(count):
        t += rng.choice([0.0, 0.05, 0.2, 0.5, 1.0])
        notes.append(
            PlayedNote(
                rng.randint(30, 90),
                round(rng.uniform(0.05, 1), 4),
                t,
                t + rng.choice([0.01, 0.1, 0.4, 1.5]),
            )
        )
    return notes


def reference_notes(rng: random.Random, count: int) -> list[ReferenceNote]:
    t = 0.0
    notes = []
    for _ in range(count):
        t += rng.choice([0.0, 0.1, 0.4, 0.7])
        notes.append(
            ReferenceNote(
                rng.randint(30, 90), round(rng.uniform(0.05, 1), 4), t, rng.choice([0.05, 0.3, 0.9])
            )
        )
    return notes


def as_user_dicts(notes):
    return [{"note": n.note, "vel": n.vel, "start": n.start, "end": n.end} for n in notes]


def as_ref_dicts(notes):
    return [{"note": n.note, "vel": n.vel, "start": n.start, "dur": n.dur} for n in notes]


@pytest.mark.parametrize("seed", range(40))
def test_radar_metrics(seed, original):
    rng = random.Random(seed)
    user, ref = user_notes(rng, rng.randint(0, 25)), reference_notes(rng, rng.randint(0, 25))
    assert analysis.radar_metrics(user) == original.compute_radar_metrics(as_user_dicts(user))
    assert analysis.radar_metrics(ref) == original.compute_radar_metrics(
        as_ref_dicts(ref), is_ref=True
    )


@pytest.mark.parametrize("seed", range(40))
def test_articulation(seed, original):
    rng = random.Random(seed)
    user = user_notes(rng, rng.randint(0, 30))
    assert (
        analysis.classify_articulation(user)
        == original.classifica_articolazione(as_user_dicts(user))[0]
    )


@pytest.mark.parametrize("seed", range(60))
def test_alignment(seed, original):
    rng = random.Random(seed)
    ref = reference_notes(rng, rng.randint(0, 30))
    if rng.random() < 0.7:  # a performance that roughly follows the song
        origin = rng.uniform(0, 5000)
        user = [
            PlayedNote(
                r.note, r.vel, origin + r.start + rng.uniform(-0.5, 0.5), origin + r.start + r.dur
            )
            for r in ref
            if rng.random() < 0.8
        ]
    else:
        user = user_notes(rng, rng.randint(0, 20))
    window = rng.choice([None, 2.0, 10.0])
    got = analysis.align_notes(user, ref, ref_window=window)
    want = original.allinea_note(as_user_dicts(user), as_ref_dicts(ref), ref_window=window)
    assert got.matched == want["matched"]
    assert got.missed == want["missed"]
    assert got.extra == want["extra"]
    assert got.vel_ratios == want["vel_ratios"]
    assert got.dur_ratios == want["dur_ratios"]
    assert got.onset_errors == want["onset_errors"]


@pytest.mark.parametrize("seed", range(20))
def test_note_colors(seed, original):
    rng = random.Random(seed)
    for _ in range(20):
        note, vel = rng.randint(0, 127), rng.uniform(-0.2, 1.3)
        hand = rng.choice([None, "left", "right"])
        assert analysis.note_color(note, vel, hand) == original.get_note_color_rgb(note, vel, hand)


@pytest.mark.parametrize("seed", range(8))
def test_audio_is_identical(seed, original, tmp_path):
    rng = random.Random(seed)
    notes = user_notes(rng, rng.randint(0, 12))
    original.generate_wav_from_midi_notes(as_user_dicts(notes), str(tmp_path / "o.wav"))
    with wave.open(str(tmp_path / "o.wav")) as wav:
        want = np.frombuffer(wav.readframes(wav.getnframes()), dtype=np.int16)
    assert np.array_equal(audio.synthesize(notes), want)
