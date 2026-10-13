"""The Rust MIDI reader must see every file exactly as mido and the original bridge do."""

from __future__ import annotations

import io
import json
import random
import subprocess
import tempfile
from pathlib import Path

import mido
import pytest
from golden.generate_golden import load_original
from harness.legacy_layout import REPO_ROOT

DUMP = REPO_ROOT / "target" / "debug" / "dump_timeline"
FILES_PER_SEED = 6


def random_midi(rng: random.Random) -> bytes:
    """A valid file with random tempo maps, tracks, channels and message types."""
    tpb = rng.choice([96, 120, 192, 240, 480, 960])
    midi_type = rng.choice([0, 1, 1])
    mid = mido.MidiFile(type=midi_type, ticks_per_beat=tpb)
    track_count = 1 if midi_type == 0 else rng.randint(2, 4)
    for index in range(track_count):
        events: list[tuple[int, mido.Message | mido.MetaMessage]] = []
        if index == 0:
            for _ in range(rng.randint(0, 4)):
                events.append(
                    (
                        rng.randrange(0, tpb * 12),
                        mido.MetaMessage("set_tempo", tempo=rng.randint(150_000, 1_000_000)),
                    )
                )
        if midi_type == 0 or index > 0:
            held: dict[tuple[int, int], int] = {}
            tick = 0
            for _ in range(rng.randint(0, 40)):
                tick += rng.choice([0, 0, 1, 7, tpb // 8, tpb // 4, tpb // 2, tpb])
                channel = rng.randrange(0, 3)
                roll = rng.random()
                if roll < 0.45:
                    note = rng.randint(21, 108)
                    events.append(
                        (
                            tick,
                            mido.Message(
                                "note_on", note=note, velocity=rng.randint(1, 127), channel=channel
                            ),
                        )
                    )
                    held[(channel, note)] = tick
                elif roll < 0.8 and held:
                    (ch, note), _ = held.popitem()
                    if rng.random() < 0.5:
                        events.append(
                            (tick, mido.Message("note_on", note=note, velocity=0, channel=ch))
                        )
                    else:
                        events.append(
                            (
                                tick,
                                mido.Message(
                                    "note_off", note=note, velocity=rng.randint(0, 127), channel=ch
                                ),
                            )
                        )
                elif roll < 0.88:
                    events.append(
                        (
                            tick,
                            mido.Message(
                                "control_change",
                                control=rng.randint(0, 127),
                                value=rng.randint(0, 127),
                                channel=channel,
                            ),
                        )
                    )
                elif roll < 0.92:
                    events.append(
                        (
                            tick,
                            mido.Message(
                                "program_change",
                                program=rng.choice([0, 3, 7, 8, 40, 100]),
                                channel=channel,
                            ),
                        )
                    )
                elif roll < 0.95:
                    events.append(
                        (
                            tick,
                            mido.Message(
                                "pitchwheel", pitch=rng.randint(-8192, 8191), channel=channel
                            ),
                        )
                    )
                elif roll < 0.97:
                    events.append(
                        (
                            tick,
                            mido.Message(
                                "sysex",
                                data=[rng.randint(0, 127) for _ in range(rng.randint(1, 6))],
                            ),
                        )
                    )
                else:
                    events.append((tick, mido.MetaMessage("text", text="x")))
            for (ch, note), _ in held.items():
                events.append((tick + tpb, mido.Message("note_off", note=note, channel=ch)))
        track = mido.MidiTrack()
        last = 0
        for tick, msg in sorted(events, key=lambda e: e[0]):
            track.append(msg.copy(time=tick - last))
            last = tick
        if rng.random() < 0.8:
            track.append(mido.MetaMessage("end_of_track", time=rng.choice([0, 0, tpb, tpb * 3])))
        mid.tracks.append(track)
    out = io.BytesIO()
    mid.save(file=out)
    return out.getvalue()


@pytest.fixture(scope="module")
def original():
    with tempfile.TemporaryDirectory() as home:
        module = load_original(Path(home))
        yield module


def run_rust(paths: list[Path]) -> dict:
    result = subprocess.run([DUMP, *map(str, paths)], capture_output=True, check=True, text=True)
    return json.loads(result.stdout)


@pytest.mark.parametrize("seed", range(25))
def test_rust_matches_mido_and_the_original_bridge(seed, original, tmp_path):
    rng = random.Random(seed)
    paths = []
    for index in range(FILES_PER_SEED):
        path = tmp_path / f"song{index}.mid"
        path.write_bytes(random_midi(rng))
        paths.append(path)
    rust = run_rust(paths)

    original.CANZONI_PATH = str(tmp_path)
    for path in paths:
        got = rust[str(path)]
        assert "error" not in got, got
        expected = list(mido.MidiFile(path))

        # The timeline: delay and bytes of every step, bit-exact.
        assert [s["delay"] for s in got["steps"]] == [m.time for m in expected]
        assert [s["bytes"] for s in got["steps"]] == [
            None if m.is_meta else m.bytes() for m in expected
        ]
        # Only compared approximately: Python 3.12 sums floats with compensation.
        assert got["length"] == pytest.approx(mido.MidiFile(path).length, rel=1e-12)

        # Everything derived from it, computed by the original functions.
        original.load_follow_sequence(path.name)
        assert got["reference_notes"] == original.current_song_reference_notes
        assert got["chords"] == [list(dict.fromkeys(c)) for c in original.follow_notes_sequence]
        assert got["has_piano_part"] == original.has_piano_track(str(path))
        safe, reason = original.is_midi_safe_for_visualizer(str(path))
        assert (got["unsafe"] is None) == safe, (got["unsafe"], reason)


def test_unreadable_files_are_reported_as_errors(tmp_path):
    broken = tmp_path / "broken.mid"
    broken.write_bytes(b"MThd garbage")
    assert "error" in run_rust([broken])[str(broken)]
