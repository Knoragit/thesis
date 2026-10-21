"""The score helpers that do not need MuseScore: the MIDI writer and reading page times."""

from __future__ import annotations

import mido
import pytest
from bridge_tools.score import musescore, smf

MPOS = """<?xml version="1.0" encoding="UTF-8"?>
<score>
  <elements>
    <element id="0" x="1" y="1" sx="1" sy="1" page="0"/>
    <element id="1" x="1" y="1" sx="1" sy="1" page="0"/>
    <element id="2" x="1" y="1" sx="1" sy="1" page="1"/>
    <element id="3" x="1" y="1" sx="1" sy="1" page="1"/>
  </elements>
  <events>
    <event elid="0" position="0"/>
    <event elid="1" position="2010"/>
    <event elid="2" position="4020"/>
    <event elid="3" position="6030"/>
  </events>
</score>
"""


def test_played_notes_become_a_midi_file_from_the_first_note(tmp_path):
    path = tmp_path / "take.mid"
    smf.write_notes(path, [(64, 0.5, 10.5, 11.0), (60, 1.0, 10.0, 10.5)])
    midi = mido.MidiFile(path)
    notes = []
    clock = 0.0
    for message in midi:
        clock += message.time
        if message.type in ("note_on", "note_off"):
            notes.append((message.type, message.note, round(clock, 6)))
    assert notes == [
        ("note_on", 60, 0.0),
        ("note_off", 60, 0.5),
        ("note_on", 64, 0.5),
        ("note_off", 64, 1.0),
    ]


def test_each_hand_gets_its_own_track_right_hand_first(tmp_path):
    path = tmp_path / "take.mid"
    smf.write_notes(
        path,
        [(72, 0.5, 0.0, 0.5, "right"), (64, 0.5, 0.0, 0.5, "left"), (55, 0.5, 0.5, 1.0, "right"),
         (40, 0.5, 0.5, 1.0, None), (80, 0.5, 0.5, 1.0, None)],
    )
    tracks = [
        [m.note for m in track if m.type == "note_on"] for track in mido.MidiFile(path).tracks
    ]
    # Hand tracking wins over pitch; without it, middle C splits the hands.
    assert tracks == [[72, 55, 80], [64, 40]]


def test_pages_are_timed_by_bar_number_when_the_bars_are_known(tmp_path):
    mpos = tmp_path / "out.mpos"
    mpos.write_text(MPOS, encoding="utf-8")
    assert musescore._page_starts(mpos, 2, None) == [0.0, 4.02]
    assert musescore._page_starts(mpos, 2, [0.0, 2.0, 4.0, 6.0]) == [0.0, 4.0]
    # A page without bars starts with the one before it; unreadable positions mean page 1.
    assert musescore._page_starts(mpos, 3, None) == [0.0, 4.02, 4.02]
    assert musescore._page_starts(tmp_path / "missing.mpos", 2, None) == [0.0, 0.0]


def test_an_empty_setting_turns_engraving_off(tmp_path, monkeypatch):
    monkeypatch.setenv(musescore.ENV_VAR, "")
    assert musescore.find_musescore() is None
    with pytest.raises(musescore.EngravingError):
        musescore.engrave(tmp_path / "a.mid", tmp_path)
