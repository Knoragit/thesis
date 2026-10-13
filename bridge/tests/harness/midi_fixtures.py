"""Small MIDI files built on the fly, with timings that are easy to reason about.

Unless noted, files use 480 ticks per beat and 250000 us per beat, so one beat is 0.25 s.
"""

from __future__ import annotations

import io

import mido

TPB = 480
BEAT_US = 250_000
BEAT_S = 0.25


def _track(events: list[tuple[int, mido.Message | mido.MetaMessage]]) -> mido.MidiTrack:
    """Builds a track from (absolute tick, message) pairs; ties keep the given order."""
    track = mido.MidiTrack()
    last = 0
    for tick, msg in sorted(events, key=lambda e: e[0]):
        track.append(msg.copy(time=tick - last))
        last = tick
    return track


def _file(*tracks: mido.MidiTrack, midi_type: int = 1) -> bytes:
    mid = mido.MidiFile(type=midi_type, ticks_per_beat=TPB)
    mid.tracks.extend(tracks)
    out = io.BytesIO()
    mid.save(file=out)
    return out.getvalue()


def _on(note: int, vel: int = 100, ch: int = 0) -> mido.Message:
    return mido.Message("note_on", note=note, velocity=vel, channel=ch)


def _off(note: int, vel: int = 0, ch: int = 0) -> mido.Message:
    return mido.Message("note_off", note=note, velocity=vel, channel=ch)


def _tempo(us: int = BEAT_US) -> mido.MetaMessage:
    return mido.MetaMessage("set_tempo", tempo=us)


def _end(tick: int) -> mido.MetaMessage:
    return mido.MetaMessage("end_of_track")


def scale() -> bytes:
    """C D E F, one beat each, ending 1 beat after the last note-off (end_of_track at 1.25 s)."""
    events: list[tuple[int, mido.Message | mido.MetaMessage]] = [(0, _tempo())]
    for i, note in enumerate((60, 62, 64, 65)):
        events.append((i * TPB, _on(note, 80 + i * 10)))
        events.append(((i + 1) * TPB, _off(note)))
    events.append((5 * TPB, _end(5 * TPB)))
    return _file(_track(events), midi_type=0)


SCALE_NOTES = (60, 62, 64, 65)
SCALE_ON_TIMES = (0.0, 0.25, 0.5, 0.75)
SCALE_VELOCITIES = (80, 90, 100, 110)
SCALE_LAST_EVENT_S = 1.25


def tempo_change() -> bytes:
    """Conductor track with a tempo change at 0.5 s; the notes live on a second track."""
    conductor = _track([(0, _tempo(250_000)), (2 * TPB, _tempo(125_000)), (6 * TPB, _end(6 * TPB))])
    notes = _track(
        [
            (0, _on(60)),
            (TPB, _off(60)),
            (2 * TPB, _on(62)),  # 0.5 s
            (3 * TPB, _off(62)),  # 0.625 s (new tempo: a beat is 0.125 s)
            (4 * TPB, _on(64)),  # 0.75 s
            (5 * TPB, _off(64)),  # 0.875 s
            (6 * TPB, _end(6 * TPB)),
        ]
    )
    return _file(conductor, notes)


TEMPO_CHANGE_ON_TIMES = (0.0, 0.5, 0.75)


def controls() -> bytes:
    """Every non-note message type that has to reach the piano unchanged."""
    events: list[tuple[int, mido.Message | mido.MetaMessage]] = [
        (0, _tempo()),
        (0, mido.MetaMessage("track_name", name="controls")),
        (0, mido.Message("program_change", program=0, channel=0)),
        (0, mido.Message("control_change", control=64, value=127, channel=0)),
        (0, _on(60, 90, ch=0)),
        (TPB // 2, mido.Message("pitchwheel", pitch=1000, channel=0)),
        (TPB // 2, mido.Message("polytouch", note=60, value=40, channel=0)),
        (TPB, mido.Message("aftertouch", value=30, channel=0)),
        (TPB, mido.Message("sysex", data=(1, 2, 3))),
        (TPB, _off(60, ch=0)),
        (TPB, _on(72, 64, ch=2)),
        (2 * TPB, _off(72, 40, ch=2)),
        (2 * TPB, mido.Message("control_change", control=64, value=0, channel=0)),
    ]
    return _file(_track(events), midi_type=0)


def chords() -> bytes:
    """Follow-me fixture covering chord grouping.

    0.00 s  chord 67 (long), 60 (short), 64: notes are stored in note-off order -> [60, 64, 67]
    0.50 s  single note 72
    1.00 s  notes 48, 52, 55 each 0.04 s after the previous one: chained into one chord
    1.50 s  same pitch twice within 0.05 s: deduplicated when sent to Unity
    """
    sec = TPB * 4  # ticks per second at 250000 us/beat

    def t(seconds: float) -> int:
        return round(seconds * sec)

    events: list[tuple[int, mido.Message | mido.MetaMessage]] = [
        (0, _tempo()),
        (0, _on(67)),
        (0, _on(60)),
        (0, _on(64)),
        (t(0.25), _off(60)),
        (t(0.375), _off(64)),
        (t(0.5), _off(67)),
        (t(0.5), _on(72)),
        (t(0.75), _off(72)),
        (t(1.0), _on(48)),
        (t(1.04), _on(52)),
        (t(1.08), _on(55)),
        (t(1.25), _off(48)),
        (t(1.25), _off(52)),
        (t(1.25), _off(55)),
        (t(1.5), _on(76)),
        (t(1.51), _off(76)),
        (t(1.53), _on(76)),
        (t(1.6), _off(76)),
    ]
    return _file(_track(events), midi_type=0)


CHORD_SEQUENCE = ([60, 64, 67], [72], [48, 52, 55], [76])


def long_song() -> bytes:
    """About 8 s of quarter notes at 120 BPM, for stop/replace tests."""
    events: list[tuple[int, mido.Message | mido.MetaMessage]] = [(0, _tempo(500_000))]
    for i in range(16):
        events.append((i * TPB, _on(60 + (i % 8))))
        events.append(((i + 1) * TPB - 10, _off(60 + (i % 8))))
    return _file(_track(events), midi_type=0)


def hammer() -> bytes:
    """More than 60 simultaneous notes: rejected as unsafe for the visualizer."""
    events: list[tuple[int, mido.Message | mido.MetaMessage]] = [(0, _tempo())]
    for note in range(21, 21 + 70):
        events.append((0, _on(note)))
    for note in range(21, 21 + 70):
        events.append((TPB, _off(note)))
    return _file(_track(events), midi_type=0)


def out_of_range() -> bytes:
    """Contains a note below the 88-key range: rejected as unsafe."""
    return _file(
        _track([(0, _tempo()), (0, _on(10)), (TPB, _off(10))]),
        midi_type=0,
    )


def violin() -> bytes:
    """Program 40 only: no piano part."""
    return _file(
        _track(
            [
                (0, _tempo()),
                (0, mido.Message("program_change", program=40)),
                (0, _on(60)),
                (TPB, _off(60)),
            ]
        ),
        midi_type=0,
    )


def piano(note: int = 60) -> bytes:
    """Minimal valid piano file (explicit program 0)."""
    return _file(
        _track(
            [
                (0, _tempo()),
                (0, mido.Message("program_change", program=0)),
                (0, _on(note)),
                (TPB, _off(note)),
            ]
        ),
        midi_type=0,
    )
