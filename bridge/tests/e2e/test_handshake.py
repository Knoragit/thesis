"""Startup handshake and tolerance to junk input."""

from harness.expect import BRIDGE_READY


def test_announces_itself_when_the_listener_is_up(make_bridge):
    bridge = make_bridge(wait_ready=False)
    first = bridge.unity.wait_action("bridge_ready", timeout=30)
    assert first.raw == BRIDGE_READY


def test_ping_is_answered_every_time(make_bridge):
    bridge = make_bridge()
    for _ in range(3):
        start = bridge.unity.mark()
        bridge.unity.send({"action": "ping"})
        reply = bridge.unity.wait_action("bridge_ready", since=start)
        assert reply.raw == BRIDGE_READY


def test_junk_input_is_ignored_and_does_not_break_the_bridge(make_bridge):
    bridge = make_bridge()
    start = bridge.unity.mark()
    for junk in (
        b"",
        b"not json at all",
        b"\xff\xfe\x00",
        b"[1, 2, 3]",
        b'"just a string"',
        b"{}",
        b'{"action": "no_such_action"}',
        b'{"action": null}',
        b'{"action": "note_hand", "note": "x"}',
    ):
        bridge.unity.send(junk)
    assert bridge.unity.quiet(0.5) == bridge.unity.log(start)
    assert bridge.unity.log(start) == []
    reply = bridge.unity.request({"action": "ping"}, "bridge_ready")
    assert reply == {"action": "bridge_ready"}
