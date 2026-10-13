"""Tutorial examples: scripted demonstrations played on the piano and streamed to Unity."""

import time

import mido
import pytest
from harness import midi_fixtures as fx
from harness.expect import CLEAR_SCENE, EXAMPLE_DONE, press, release
from harness.runs import assert_times

# (note, unity velocity, duration, pause) per challenge, from the tutorial design.
SEQUENCES = {
    1: [(60, 0.18, 1.20, 0.10)],
    2: [(60, 0.25, 0.35, 0.40), (62, 0.35, 0.35, 0.40), (64, 0.45, 0.35, 0.40), (65, 0.55, 0.35, 0.10)],
    3: [(60, 0.50, 0.18, 0.40), (62, 0.50, 0.15, 0.10)],
}  # fmt: skip


def run_example(bridge, sfida, timeout=8.0):
    u_mark, s_mark = bridge.unity.mark(), bridge.synth.mark()
    bridge.unity.send({"action": "play_example", "sfida": sfida})
    done = bridge.unity.wait_action("example_done", timeout, since=u_mark)
    time.sleep(0.2)
    return bridge.unity.log(u_mark), bridge.synth.received(s_mark), done


@pytest.mark.parametrize("sfida", [1, 2, 3])
def test_simple_examples(make_bridge, sfida):
    bridge = make_bridge()
    udp, midi, _ = run_example(bridge, sfida)

    expected, midi_expected, onsets, offsets = [], [], [], []
    clock = 0.0
    for note, vel, duration, pause in SEQUENCES[sfida]:
        expected += [press(note, vel), release(note, vel)]
        midi_expected += [
            mido.Message("note_on", note=note, velocity=int(vel * 127)),
            mido.Message("note_off", note=note, velocity=64),
        ]
        onsets.append(clock)
        offsets.append(clock + duration)
        clock += duration + pause
    assert [d.raw for d in udp] == [*expected, CLEAR_SCENE, EXAMPLE_DONE]
    assert [s.msg.bytes() for s in midi] == [m.bytes() for m in midi_expected]

    t0 = udp[0].t
    # The original sleeps between notes, so its timings drift by a few ms per note.
    assert_times([d.t - t0 for d in udp if d.action == "press"], onsets, tol=0.1)
    assert_times([d.t - t0 for d in udp if d.action == "release"], offsets, tol=0.1)
    assert abs((udp[-1].t - t0) - clock) <= 0.1


def test_legato_example_overlaps_two_notes(make_bridge):
    bridge = make_bridge()
    udp, midi, _ = run_example(bridge, 4)
    assert [d.raw for d in udp] == [
        press(60, 0.5),
        press(64, 0.5),
        release(64, 0.5),
        release(60, 0.5),
        CLEAR_SCENE,
        EXAMPLE_DONE,
    ]
    t0 = udp[0].t
    assert_times([d.t - t0 for d in udp[:4]], [0.0, 0.60, 1.45, 1.55])
    assert [s.msg.bytes() for s in midi] == [
        mido.Message("note_on", note=60, velocity=64).bytes(),
        mido.Message("note_on", note=64, velocity=64).bytes(),
        mido.Message("note_off", note=64, velocity=64).bytes(),
        mido.Message("note_off", note=60, velocity=64).bytes(),
    ]


def test_missing_challenge_defaults_to_the_first(make_bridge):
    bridge = make_bridge()
    u_mark = bridge.unity.mark()
    bridge.unity.send({"action": "play_example"})
    bridge.unity.wait_action("example_done", 5.0, since=u_mark)
    assert bridge.unity.log(u_mark)[0].raw == press(60, 0.18)


@pytest.mark.parametrize("sfida", [0, 5, 99, -1])
def test_unknown_challenge_is_silently_ignored(make_bridge, sfida):
    bridge = make_bridge()
    bridge.unity.send({"action": "play_example", "sfida": sfida})
    assert bridge.unity.quiet(0.6) == []
    assert bridge.midi_out() == []


def test_stop_example_ends_it_after_the_current_note(make_bridge):
    bridge = make_bridge()
    u_mark = bridge.unity.mark()
    bridge.unity.send({"action": "play_example", "sfida": 2})
    bridge.unity.wait_action("press", since=u_mark)
    time.sleep(0.2)
    bridge.unity.send({"action": "stop_example"})
    bridge.unity.wait_action("example_done", 5.0, since=u_mark)
    log = bridge.unity.log(u_mark)
    assert [d.raw for d in log] == [press(60, 0.25), release(60, 0.25), CLEAR_SCENE, EXAMPLE_DONE]
    assert (log[-1].t - log[0].t) < 1.0  # well before the 3 s the full sequence takes


def test_stop_example_does_not_interrupt_the_legato_demo(make_bridge):
    bridge = make_bridge()
    u_mark = bridge.unity.mark()
    bridge.unity.send({"action": "play_example", "sfida": 4})
    bridge.unity.wait_action("press", since=u_mark)
    bridge.unity.send({"action": "stop_example"})
    bridge.unity.wait_action("example_done", 5.0, since=u_mark)
    assert [d.action for d in bridge.unity.log(u_mark)] == [
        "press", "press", "release", "release", "clear_scene", "example_done",
    ]  # fmt: skip


@pytest.mark.parametrize("mode", ["play_song", "play_song_follow"])
def test_example_is_skipped_while_a_song_is_active(make_bridge, mode):
    bridge = make_bridge({"long.mid": fx.long_song()})
    bridge.unity.send({"action": mode, "filename": "long.mid"})
    time.sleep(0.8)
    u_mark, s_mark = bridge.unity.mark(), bridge.synth.mark()
    bridge.unity.send({"action": "play_example", "sfida": 1})
    bridge.unity.wait_action("example_done", 3.0, since=u_mark)
    # Only the acknowledgement: no example notes (the song itself may still be streaming).
    ours = [d for d in bridge.unity.log(u_mark) if d.raw == press(60, 0.18)]
    assert ours == []
    assert all(
        s.msg.type != "note_on" or s.msg.velocity != 22 for s in bridge.synth.received(s_mark)
    )
    bridge.unity.send({"action": "stop_song"})
