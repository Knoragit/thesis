"""search_song: local cache, freemidi, bitmidi fallback, validation of downloads."""

import json

from harness import midi_fixtures as fx
from harness.expect import wire
from harness.sites import (
    bitmidi_filename,
    freemidi_search_url,
    serve_bitmidi,
    serve_freemidi,
)

NOT_FOUND = wire(
    {"action": "search_result", "status": "error", "message": "Canzone non trovata nel database online."}
)  # fmt: skip


def search(bridge, query, timeout=20.0):
    start = bridge.unity.mark()
    bridge.unity.send({"action": "search_song", "query": query})
    return bridge.unity.wait_action("search_result", timeout, since=start)


def songs_dir_names(bridge):
    return sorted(p.name for p in bridge.layout.songs_dir.iterdir())


def test_local_match_is_answered_from_the_library_without_network(make_bridge):
    bridge = make_bridge({"scale.mid": fx.scale()})
    reply = search(bridge, "  SCALE.mid  ")
    assert reply.raw == wire(
        {
            "action": "search_result", "status": "success", "filename": "scale.mid",
            "title": "scale", "artist": "Artista Non Specificato",
        }
    )  # fmt: skip
    assert bridge.http.requests == []


def test_blank_query_gets_no_answer(make_bridge):
    bridge = make_bridge()
    for query in ("", "   "):
        bridge.unity.send({"action": "search_song", "query": query})
    assert bridge.unity.quiet(0.5) == []
    assert bridge.http.requests == []


def test_freemidi_download_is_stored_validated_and_cached(make_bridge):
    bridge = make_bridge()
    serve_freemidi(
        bridge.http,
        "moonlight sonata",
        {
            "download3-101-moonlight-sonata": (fx.piano(60), "Moonlight Sonata", "Beethoven"),
            "download3-102-moonlight-easy": (fx.piano(62), "Moonlight Easy", None),
        },
    )
    reply = search(bridge, "Moonlight Sonata")
    assert reply.raw == wire(
        {
            "action": "search_result", "status": "success", "filename": "101-moonlight-sonata.mid",
            "title": "Moonlight Sonata", "artist": "Beethoven",
        }
    )  # fmt: skip
    assert songs_dir_names(bridge) == ["101-moonlight-sonata.mid", "102-moonlight-easy.mid"]
    getters = bridge.http.requested("/freemidi.org/getter-")
    assert len(getters) == 2
    assert all(
        r.headers["Referer"].startswith("https://freemidi.org/download3-") for r in getters
    )  # the site refuses downloads without it

    # Every downloaded song is now in the library, found by the query that fetched it.
    listing = bridge.unity.request({"action": "search_suggest", "query": "moonlight"}, "song_list")[
        "songs"
    ]
    by_name = {s["filename"]: s for s in listing}
    assert set(by_name) == {"101-moonlight-sonata.mid", "102-moonlight-easy.mid"}
    assert by_name["101-moonlight-sonata.mid"]["query"] == "moonlight sonata"
    assert by_name["102-moonlight-easy.mid"]["title"] == "Moonlight Easy"
    assert by_name["102-moonlight-easy.mid"]["artist"] is None

    # Asking again is a cache hit: no further downloads.
    before = len(bridge.http.requests)
    assert search(bridge, "moonlight sonata").raw == reply.raw
    assert len(bridge.http.requests) == before


def test_at_most_four_candidates_are_downloaded(make_bridge):
    bridge = make_bridge()
    serve_freemidi(
        bridge.http,
        "many",
        {f"download3-{i}-song-{i}": (fx.piano(40 + i), f"Song {i}", "A") for i in range(1, 7)},
    )
    search(bridge, "many")
    assert len(songs_dir_names(bridge)) == 4
    assert len(bridge.http.requested("/freemidi.org/getter-")) == 4


def test_downloads_without_piano_or_unsafe_for_the_visualizer_are_discarded(make_bridge):
    bridge = make_bridge()
    serve_freemidi(
        bridge.http,
        "mixed",
        {
            "download3-1-violin": (fx.violin(), "Violin", "A"),
            "download3-2-hammer": (fx.hammer(), "Hammer", "A"),
            "download3-3-notmidi": (b"<html>error</html>", "Broken", "A"),
            "download3-4-good": (fx.piano(), "Good", "A"),
        },
    )
    reply = search(bridge, "mixed")
    assert reply.msg["filename"] == "4-good.mid"
    assert songs_dir_names(bridge) == ["4-good.mid"]


def test_bitmidi_is_the_fallback_when_freemidi_has_nothing_valid(make_bridge):
    bridge = make_bridge()
    serve_freemidi(bridge.http, "nocturne", {"download3-1-violin": (fx.violin(), "V", "A")})
    serve_bitmidi(bridge.http, "nocturne", {"chopin-nocturne-mid": (fx.piano(), "Chopin Nocturne")})
    reply = search(bridge, "nocturne")
    expected_name = bitmidi_filename("chopin-nocturne-mid")
    assert json.loads(reply.raw) == {
        "action": "search_result",
        "status": "success",
        "filename": expected_name,
        "title": "Chopin Nocturne",
        "artist": None,
    }
    assert songs_dir_names(bridge) == [expected_name]


def test_nothing_found_anywhere(make_bridge):
    bridge = make_bridge()
    bridge.http.serve(freemidi_search_url("zzz"), "<html>no results</html>")
    assert search(bridge, "zzz").raw == NOT_FOUND
    assert songs_dir_names(bridge) == []


def test_unreachable_sites_count_as_not_found(make_bridge):
    bridge = make_bridge()  # the fixture server answers 404 to everything
    assert search(bridge, "anything").raw == NOT_FOUND


def test_the_bridge_keeps_answering_while_a_search_is_running(make_bridge):
    bridge = make_bridge()
    serve_freemidi(bridge.http, "q", {"download3-1-a": (fx.piano(), "A", "B")})
    bridge.unity.send({"action": "search_song", "query": "q"})
    assert bridge.unity.request({"action": "ping"}, "bridge_ready")
    bridge.unity.wait_action("search_result", 20.0)
