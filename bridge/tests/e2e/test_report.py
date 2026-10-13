"""Fai Tu recording, report status and report generation."""

import os
import stat
import time
import wave

import pytest
from harness import midi_fixtures as fx
from harness.expect import wire
from PIL import Image
from pypdf import PdfReader

SONGS = {"chords.mid": fx.chords()}
NO_MATERIAL = wire({"action": "report_status", "has_material": False})
HAS_MATERIAL = wire({"action": "report_status", "has_material": True})


def status(bridge) -> bytes:
    start = bridge.unity.mark()
    bridge.unity.send({"action": "get_report_status"})
    return bridge.unity.wait_action("report_status", since=start).raw


def tap(bridge, note=60, hold=0.06):
    # Commands (UDP) and notes (MIDI) travel independently: let the last command take effect.
    time.sleep(0.1)
    bridge.keyboard.tap(note, 100, hold)
    time.sleep(0.1)


def generate(bridge, timeout=60.0):
    start = bridge.unity.mark()
    bridge.unity.send({"action": "generate_report"})
    return bridge.unity.wait_action("report_ready", timeout, since=start).msg


# -- report_status ----------------------------------------------------------------------------


def test_status_is_empty_at_first(make_bridge):
    assert status(make_bridge(SONGS)) == NO_MATERIAL


def test_a_recorded_fai_tu_note_counts_as_material(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu", "filename": "Improvvisazione_Libera"})
    tap(bridge)
    assert status(bridge) == HAS_MATERIAL


def test_notes_are_only_recorded_while_fai_tu_is_running(make_bridge):
    bridge = make_bridge(SONGS)
    tap(bridge)  # not recording
    assert status(bridge) == NO_MATERIAL
    bridge.unity.send({"action": "start_fai_tu"})
    tap(bridge)
    bridge.unity.send({"action": "stop_fai_tu"})
    assert status(bridge) == HAS_MATERIAL  # stopping keeps what was recorded


def test_a_note_still_held_is_not_material_until_released(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    time.sleep(0.1)
    bridge.keyboard.press(60)
    time.sleep(0.1)
    assert status(bridge) == NO_MATERIAL
    bridge.keyboard.release(60)
    time.sleep(0.1)
    assert status(bridge) == HAS_MATERIAL


def test_release_without_press_is_ignored(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    time.sleep(0.1)
    bridge.keyboard.release(60)
    time.sleep(0.1)
    assert status(bridge) == NO_MATERIAL


def test_starting_fai_tu_again_discards_the_previous_take(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    tap(bridge)
    bridge.unity.send({"action": "start_fai_tu"})
    assert status(bridge) == NO_MATERIAL


def test_follow_me_notes_count_once_released_and_survive_stop_song(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.request({"action": "play_song_follow", "filename": "chords.mid"}, "expect_notes")
    assert status(bridge) == NO_MATERIAL
    tap(bridge)
    assert status(bridge) == HAS_MATERIAL
    bridge.unity.send({"action": "stop_song"})
    assert status(bridge) == HAS_MATERIAL
    bridge.unity.send({"action": "start_fai_tu"})  # only clears the free-play take
    assert status(bridge) == HAS_MATERIAL


def test_follow_me_notes_are_not_recorded_after_the_song_ends(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "play_song_follow", "filename": "chords.mid"})
    bridge.unity.wait_action("expect_notes")
    bridge.unity.send({"action": "stop_song"})
    time.sleep(0.1)
    tap(bridge)
    assert status(bridge) == NO_MATERIAL


def test_note_hand_assignments_are_accepted_silently(make_bridge):
    bridge = make_bridge(SONGS)
    start = bridge.unity.mark()
    for command in (
        {"action": "note_hand", "note": 60, "hand": "left"},
        {"action": "note_hand", "note": 61, "hand": "right"},
        {"action": "note_hand", "note": 62, "hand": "both"},
        {"action": "note_hand", "hand": "left"},
    ):
        bridge.unity.send(command)
    assert bridge.unity.quiet(0.4) == []
    assert status(bridge) == NO_MATERIAL
    assert len(bridge.unity.log(start)) == 1


# -- generate_report --------------------------------------------------------------------------


def assert_session_folder(bridge, report, pages):
    layout = bridge.layout
    folder = report["folder"]
    assert list(report) == ["action", "file", "folder", "pages"]
    assert report["pages"] == pages
    assert os.path.dirname(folder) == str(layout.sessions_dir)
    assert os.path.basename(folder).startswith(layout.names.session_prefix)
    assert report["file"] == os.path.join(folder, layout.names.report_name)
    names = sorted(os.listdir(folder))
    assert names == sorted(
        [layout.names.report_name, "Registrazione_Audio.wav"]
        + [f"pagina_{i}.png" for i in range(1, pages + 1)]
    )
    assert len(PdfReader(report["file"]).pages) == pages
    for i in range(1, pages + 1):
        with Image.open(os.path.join(folder, f"pagina_{i}.png")) as image:
            assert image.size == (1020, 1320)  # 8.5 x 11 in at 120 dpi


def wav_frames(folder) -> tuple[int, int, int, int]:
    with wave.open(os.path.join(folder, "Registrazione_Audio.wav")) as wav:
        return wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getnframes()


def test_free_play_report_has_one_page_and_a_recording(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    tap(bridge, 60)
    tap(bridge, 72)
    report = generate(bridge)
    assert_session_folder(bridge, report, pages=1)
    channels, width, rate, frames = wav_frames(report["folder"])
    assert (channels, width, rate) == (1, 2, 44100)
    # Notes last well under a second: the recording is padded to the 2 s minimum.
    assert frames == 2 * 44100


def test_report_without_any_material_is_a_single_placeholder_page(make_bridge):
    bridge = make_bridge(SONGS)
    report = generate(bridge)
    assert_session_folder(bridge, report, pages=1)
    assert wav_frames(report["folder"])[1:] == (2, 44100, 2 * 44100)  # test-tone fallback


def test_each_follow_session_with_notes_adds_two_pages(make_bridge):
    bridge = make_bridge({**SONGS, "scale.mid": fx.scale()})
    bridge.unity.request({"action": "play_song_follow", "filename": "chords.mid"}, "expect_notes")
    tap(bridge, 60)
    tap(bridge, 64)
    bridge.unity.request({"action": "play_song_follow", "filename": "scale.mid"}, "expect_notes")
    tap(bridge, 62)
    bridge.unity.request({"action": "play_song_follow", "filename": "chords.mid"}, "expect_notes")
    # (this third session never gets a note, so it is left out)
    report = generate(bridge)
    assert_session_folder(bridge, report, pages=1 + 2 + 2)


def test_free_play_plus_follow_me_report(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    tap(bridge, 60)
    bridge.unity.send({"action": "stop_fai_tu"})
    bridge.unity.request({"action": "play_song_follow", "filename": "chords.mid"}, "expect_notes")
    for note in (60, 64, 67, 72, 76, 48, 52):
        tap(bridge, note)
    assert_session_folder(bridge, generate(bridge), pages=3)


def test_duplicate_report_requests_are_dropped_for_five_seconds(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    tap(bridge)
    start = bridge.unity.mark()
    for _ in range(3):
        bridge.unity.send({"action": "generate_report"})
    bridge.unity.wait_action("report_ready", 60, since=start)
    time.sleep(1.0)
    assert bridge.unity.actions(start).count("report_ready") == 1

    # After the window a new report is produced, in a new session folder.
    time.sleep(5.0)
    first = next(d.msg for d in bridge.unity.log(start) if d.action == "report_ready")
    second = generate(bridge)
    assert second["folder"] != first["folder"]
    assert len(os.listdir(bridge.layout.sessions_dir)) == 2


def test_generating_a_report_stops_free_play_recording(make_bridge):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    generate(bridge)
    tap(bridge)
    assert status(bridge) == NO_MATERIAL


def test_pings_are_answered_while_a_report_is_rendered(make_bridge, profile):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    tap(bridge)
    start = bridge.unity.mark()
    bridge.unity.send({"action": "generate_report"})
    bridge.unity.send({"action": "ping"})
    bridge.unity.wait_action("report_ready", 60, since=start)
    time.sleep(0.2)
    actions = bridge.unity.actions(start)
    if profile.fixes("D3"):
        assert actions.index("bridge_ready") < actions.index("report_ready")
    else:
        # D3: the original renders on its command thread, so the ping waits for the report.
        assert actions.index("report_ready") < actions.index("bridge_ready")


def test_report_failure_is_reported_to_unity(make_bridge, profile):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    tap(bridge)
    sessions = bridge.layout.sessions_dir
    sessions.chmod(stat.S_IRUSR | stat.S_IXUSR)
    try:
        start = bridge.unity.mark()
        bridge.unity.send({"action": "generate_report"})
        if profile.fixes("D4"):
            report = bridge.unity.wait_action("report_ready", 10, since=start).msg
            assert report["pages"] == 0  # Unity shows "report not found" straight away
        else:
            # D4: the original says nothing and Unity has to wait for its own timeout.
            assert "report_ready" not in [d.action for d in bridge.unity.quiet(3.0)]
    finally:
        sessions.chmod(stat.S_IRWXU)
    assert bridge.unity.request({"action": "ping"}, "bridge_ready")


@pytest.mark.parametrize("hand", ["left", "right"])
def test_hand_hints_do_not_break_report_generation(make_bridge, hand):
    bridge = make_bridge(SONGS)
    bridge.unity.send({"action": "start_fai_tu"})
    bridge.unity.send({"action": "note_hand", "note": 60, "hand": hand})
    tap(bridge, 60)
    assert_session_folder(bridge, generate(bridge), pages=1)
