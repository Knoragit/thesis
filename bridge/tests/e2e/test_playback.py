"""Observer mode: play_song streams a MIDI file to the piano and to Unity."""

import time

import mido
import pytest
from harness import midi_fixtures as fx
from harness.expect import CLEAR_SCENE, press, release, wire
from harness.fake_unity import note_msgs
from harness.midi_io import is_all_notes_off
from harness.runs import assert_times, play

SONGS = {
    "scale.mid": fx.scale(),
    "tempo.mid": fx.tempo_change(),
    "controls.mid": fx.controls(),
    "long.mid": fx.long_song(),
}


def test_play_song_streams_notes_to_unity_with_correct_timing(make_bridge):
    bridge = make_bridge(SONGS)
    run = play(bridge, {"action": "play_song", "filename": "scale.mid"})

    expected = [CLEAR_SCENE]
    for note, vel in zip(fx.SCALE_NOTES, fx.SCALE_VELOCITIES, strict=True):
        expected += [press(note, vel / 127.0), release(note, 0.0)]
    expected.append(CLEAR_SCENE)
    assert [d.raw for d in run.udp] == expected

    assert_times(run.times("press"), fx.SCALE_ON_TIMES)
    assert_times(run.times("release"), [t + fx.BEAT_S for t in fx.SCALE_ON_TIMES])
    # clear_scene only comes after the end_of_track delay, not right after the last note.
    assert abs((run.udp[-1].t - run.t0) - fx.SCALE_LAST_EVENT_S) <= 0.06


def test_play_song_sends_the_song_to_the_piano_between_all_notes_off_bursts(make_bridge):
    bridge = make_bridge(SONGS)
    run = play(bridge, {"action": "play_song", "filename": "scale.mid"})

    msgs = [s.msg for s in run.midi]
    assert is_all_notes_off(msgs[:16])
    assert is_all_notes_off(msgs[-16:])
    expected = []
    for note, vel in zip(fx.SCALE_NOTES, fx.SCALE_VELOCITIES, strict=True):
        expected += [
            mido.Message("note_on", note=note, velocity=vel),
            mido.Message("note_off", note=note, velocity=0),
        ]
    assert [m.bytes() for m in run.song_midi()] == [m.bytes() for m in expected]


def test_play_song_by_id_matches_play_by_filename(make_bridge):
    bridge = make_bridge(SONGS)
    songs = bridge.unity.request({"action": "list_songs"}, "song_list")["songs"]
    song_id = next(s["id"] for s in songs if s["filename"] == "scale.mid")
    run = play(bridge, {"action": "play_song", "id": song_id})
    assert [d.action for d in note_msgs(run.udp)] == ["press", "release"] * 4


def test_id_takes_precedence_over_filename(make_bridge):
    bridge = make_bridge(SONGS)
    songs = bridge.unity.request({"action": "list_songs"}, "song_list")["songs"]
    song_id = next(s["id"] for s in songs if s["filename"] == "scale.mid")
    run = play(bridge, {"action": "play_song", "id": song_id, "filename": "long.mid"})
    assert len(run.times("press")) == 4


def test_unknown_id_or_no_song_gives_play_result_error(make_bridge):
    bridge = make_bridge(SONGS)
    expected = wire(
        {"action": "play_result", "status": "error", "message": "Brano non trovato nel database."}
    )
    for command in (
        {"action": "play_song", "id": 9999},
        {"action": "play_song"},
        {"action": "play_song_follow", "id": 9999},
        {"action": "play_song_follow"},
    ):
        start = bridge.unity.mark()
        bridge.unity.send(command)
        assert bridge.unity.wait_action("play_result", since=start).raw == expected
    assert bridge.unity.quiet(0.3) == []
    assert bridge.midi_out() == []


def test_tempo_changes_on_another_track_apply_to_the_whole_song(make_bridge):
    bridge = make_bridge(SONGS)
    run = play(bridge, {"action": "play_song", "filename": "tempo.mid"})
    assert_times(run.times("press"), fx.TEMPO_CHANGE_ON_TIMES)
    assert_times(run.times("release"), (0.25, 0.625, 0.875))


def test_every_non_meta_message_reaches_the_piano_unchanged(make_bridge):
    bridge = make_bridge(SONGS)
    run = play(bridge, {"action": "play_song", "filename": "controls.mid"})

    source = mido.MidiFile(file=__import__("io").BytesIO(fx.controls()))
    expected = [m for m in source if not m.is_meta]
    assert [m.bytes() for m in run.song_midi()] == [m.bytes() for m in expected]
    assert any(m.type == "sysex" for m in expected)
    assert {m.type for m in expected} >= {
        "program_change", "control_change", "pitchwheel", "polytouch", "aftertouch", "sysex",
    }  # fmt: skip
    # Unity only hears about notes, on every channel.
    assert [d.raw for d in note_msgs(run.udp)] == [
        press(60, 90 / 127.0),
        release(60, 0.0),
        press(72, 64 / 127.0),
        release(72, 40 / 127.0),
    ]


def test_stop_song_interrupts_playback(make_bridge):
    bridge = make_bridge(SONGS)
    u_mark, s_mark = bridge.unity.mark(), bridge.synth.mark()
    bridge.unity.send({"action": "play_song", "filename": "long.mid"})
    bridge.unity.wait_count(lambda d: d.action == "press", 2, 5.0, since=u_mark)

    stop_mark = bridge.unity.mark()
    sent_at = time.monotonic()
    bridge.unity.send({"action": "stop_song"})
    first_clear = bridge.unity.wait_action("clear_scene", since=stop_mark)
    assert first_clear.t - sent_at < 0.2

    time.sleep(0.5)
    after = bridge.unity.log(stop_mark)
    assert [d.action for d in after] == ["clear_scene", "clear_scene"]  # handler + playback end
    assert note_msgs(after) == []
    assert is_all_notes_off([s.msg for s in bridge.synth.received(s_mark)][-16:])


def test_stop_song_without_playback_just_clears_the_scene(make_bridge):
    bridge = make_bridge(SONGS)
    start = bridge.unity.mark()
    bridge.unity.send({"action": "stop_song"})
    assert [d.raw for d in bridge.unity.quiet(0.4)] == [CLEAR_SCENE]
    assert bridge.midi_out() == []
    assert len(bridge.unity.log(start)) == 1


def test_a_new_song_after_stop_plays_in_full(make_bridge):
    """The sequence Unity sends when the user picks another song."""
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "play_song", "filename": "long.mid"})
    time.sleep(1.1)
    run = play(bridge, {"action": "stop_song"}, until_clear_scenes=2)  # drain the old playback
    del run
    time.sleep(0.2)
    second = play(bridge, {"action": "play_song", "filename": "scale.mid"})
    assert_times(second.times("press"), fx.SCALE_ON_TIMES)
    assert len(second.times("press")) == 4


def test_playing_a_missing_file_leaves_unity_without_a_playback(make_bridge, profile):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "play_song", "filename": "ghost.mid"})
    traffic = bridge.unity.quiet(0.6)
    if profile.fixes("D2"):
        assert [d.action for d in traffic] == ["play_result"]
        assert traffic[0].msg["status"] == "error"
    else:
        assert traffic == []  # D2: the original fails silently
    assert note_msgs(traffic) == []
    assert bridge.midi_out() == []


def test_keyboard_notes_are_relayed_to_unity(make_bridge):
    bridge = make_bridge(SONGS)
    start = bridge.unity.mark()
    s_mark = bridge.synth.mark()

    bridge.keyboard.press(60, 100)
    bridge.keyboard.release(60)
    bridge.keyboard.press(61, 127)
    bridge.keyboard.release_as_note_on_zero(61)
    bridge.keyboard.press(62, 1)
    bridge.keyboard.release(62, 55)
    # Not notes: must be ignored.
    bridge.keyboard.send(mido.Message("control_change", control=64, value=127))
    bridge.keyboard.send(mido.Message("pitchwheel", pitch=100))
    bridge.keyboard.send(mido.Message("program_change", program=5))

    bridge.unity.wait_count(lambda d: d.action in ("press", "release"), 6, 3.0, since=start)
    time.sleep(0.3)
    assert [d.raw for d in bridge.unity.log(start)] == [
        press(60, 100 / 127.0),
        release(60, 0.0),
        press(61, 1.0),
        release(61, 0.0),
        press(62, 1 / 127.0),
        release(62, 55 / 127.0),
    ]
    assert bridge.synth.received(s_mark) == []  # the bridge does not echo to the piano


def test_picking_another_song_never_leaves_the_old_one_playing(make_bridge, profile):
    """Unity sends stop_song and the new play_song back to back (UdpReceiver.InviaComandoRiproduzione)."""
    if not profile.fixes("D1"):
        pytest.xfail("D1: in the original the stopped song survives about one time in four")
    bridge = make_bridge(SONGS)
    for attempt in range(10):
        bridge.unity.send({"action": "play_song", "filename": "long.mid"})
        time.sleep(0.3)
        bridge.unity.send({"action": "stop_song"})
        mark = bridge.unity.mark()
        bridge.unity.send({"action": "play_song", "filename": "scale.mid"})
        time.sleep(1.8)
        presses = [d.msg["note"] for d in bridge.unity.log(mark) if d.action == "press"]
        assert presses == list(fx.SCALE_NOTES), f"attempt {attempt}: {presses}"
        bridge.unity.send({"action": "stop_song"})
        time.sleep(0.3)
