"""Seguimi (follow-me): the bridge proposes one chord at a time."""

from harness import midi_fixtures as fx
from harness.expect import CLEAR_SCENE, wire

SONGS = {"chords.mid": fx.chords(), "scale.mid": fx.scale()}


def expect_notes(notes):
    return wire({"action": "expect_notes", "notes": notes})


def next_step(bridge):
    start = bridge.unity.mark()
    bridge.unity.send({"action": "next_step"})
    return bridge.unity.wait_for(
        lambda d: d.action in ("expect_notes", "clear_scene"), since=start
    ).raw


def test_chords_are_proposed_in_order_and_the_song_ends_with_clear_scene(make_bridge):
    bridge = make_bridge(SONGS)
    start = bridge.unity.mark()
    bridge.unity.send({"action": "play_song_follow", "filename": "chords.mid"})
    first = bridge.unity.wait_action("expect_notes", since=start)
    assert first.raw == expect_notes(fx.CHORD_SEQUENCE[0])
    assert bridge.midi_out() == []  # following does not play the song on the piano

    seen = [first.raw]
    for _ in fx.CHORD_SEQUENCE[1:]:
        seen.append(next_step(bridge))
    assert seen == [expect_notes(c) for c in fx.CHORD_SEQUENCE]

    # Past the end every further step just clears the scene.
    assert next_step(bridge) == CLEAR_SCENE
    assert next_step(bridge) == CLEAR_SCENE


def test_follow_by_id_and_restart_resets_the_sequence(make_bridge):
    bridge = make_bridge(SONGS)
    songs = bridge.unity.request({"action": "list_songs"}, "song_list")["songs"]
    song_id = next(s["id"] for s in songs if s["filename"] == "chords.mid")

    reply = bridge.unity.request({"action": "play_song_follow", "id": song_id}, "expect_notes")
    assert reply["notes"] == fx.CHORD_SEQUENCE[0]
    assert next_step(bridge) == expect_notes(fx.CHORD_SEQUENCE[1])

    reply = bridge.unity.request(
        {"action": "play_song_follow", "filename": "chords.mid"}, "expect_notes"
    )
    assert reply["notes"] == fx.CHORD_SEQUENCE[0]


def test_next_step_without_a_song_clears_the_scene(make_bridge):
    bridge = make_bridge(SONGS)
    assert next_step(bridge) == CLEAR_SCENE


def test_stop_song_clears_the_scene_but_keeps_the_loaded_sequence(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.request({"action": "play_song_follow", "filename": "chords.mid"}, "expect_notes")
    start = bridge.unity.mark()
    bridge.unity.send({"action": "stop_song"})
    assert bridge.unity.wait_action("clear_scene", since=start).raw == CLEAR_SCENE
    assert next_step(bridge) == expect_notes(fx.CHORD_SEQUENCE[1])


def test_following_a_missing_file(make_bridge, profile):
    bridge = make_bridge(SONGS)
    bridge.unity.request({"action": "play_song_follow", "filename": "chords.mid"}, "expect_notes")
    start = bridge.unity.mark()
    bridge.unity.send({"action": "play_song_follow", "filename": "ghost.mid"})
    traffic = bridge.unity.quiet(0.6)
    assert bridge.unity.log(start) == traffic
    if profile.fixes("D2"):
        assert [d.action for d in traffic] == ["play_result"]
        assert traffic[0].msg["status"] == "error"
        assert next_step(bridge) == expect_notes(fx.CHORD_SEQUENCE[1])  # state untouched
    else:
        # D2: the original keeps the previous song's sequence and sends its next chord.
        assert [d.raw for d in traffic] == [expect_notes(fx.CHORD_SEQUENCE[1])]
