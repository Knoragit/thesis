"""The song-search tool against local stand-ins for the websites (and, opt-in, the real ones)."""

from __future__ import annotations

import json
import os
import subprocess
import sys

import pytest
from harness import midi_fixtures as fx
from harness.http_fixtures import FixtureSite, start_fixture_server
from harness.sites import (
    bitmidi_filename,
    freemidi_search_url,
    serve_bitmidi,
    serve_freemidi,
)


@pytest.fixture(scope="module")
def site():
    site, server = start_fixture_server()
    yield site
    server.shutdown()


@pytest.fixture(autouse=True)
def _reset(site: FixtureSite):
    site.reset()


def run(site: FixtureSite, source: str, query: str, dest, live: bool = False):
    env = dict(os.environ)
    if not live:
        env["MIDI_BRIDGE_FREEMIDI_URL"] = f"{site.base_url}/freemidi.org"
        env["MIDI_BRIDGE_BITMIDI_URL"] = f"{site.base_url}/bitmidi.com"
    result = subprocess.run(
        [
            sys.executable,
            "-m",
            "bridge_tools.search.cli",
            "--source",
            source,
            "--query",
            query,
            "--dest",
            str(dest),
        ],
        capture_output=True,
        text=True,
        env=env,
        check=False,
    )
    assert result.returncode == 0, result.stderr
    return json.loads(result.stdout)


def test_freemidi_results_keep_the_site_order_and_are_saved(site, tmp_path):
    serve_freemidi(
        site,
        "moonlight",
        {
            "download3-5-b-song": (fx.piano(60), "B Song", "Artist B"),
            "download3-3-a-song": (fx.piano(62), "A Song", None),
        },
    )
    assert run(site, "freemidi", "moonlight", tmp_path) == [
        {"filename": "5-b-song.mid", "title": "B Song", "artist": "Artist B"},
        {"filename": "3-a-song.mid", "title": "A Song", "artist": None},
    ]
    assert (tmp_path / "5-b-song.mid").read_bytes() == fx.piano(60)
    assert (tmp_path / "3-a-song.mid").read_bytes() == fx.piano(62)


def test_freemidi_sends_the_real_site_as_referer(site, tmp_path):
    serve_freemidi(site, "q", {"download3-1-x": (fx.piano(), "X", None)})
    run(site, "freemidi", "q", tmp_path)
    (getter,) = site.requested("/freemidi.org/getter-")
    assert getter.headers["Referer"] == "https://freemidi.org/download3-1-x"
    assert "Mozilla" in getter.headers["User-Agent"]


def test_freemidi_skips_failed_and_non_midi_downloads(site, tmp_path):
    serve_freemidi(
        site,
        "q",
        {
            "download3-1-html": (b"<html>captcha</html>", "Broken", None),
            "download3-2-ok": (fx.piano(), "Fine", None),
        },
    )
    site.serve("/freemidi.org/getter-3", b"nope", status=500)
    site.serve(
        freemidi_search_url("q"),
        '<a href="download3-1-html">a</a><a href="download3-3-gone">b</a><a href="download3-2-ok">c</a>',
    )
    assert [d["filename"] for d in run(site, "freemidi", "q", tmp_path)] == ["2-ok.mid"]
    assert sorted(p.name for p in tmp_path.iterdir()) == ["2-ok.mid"]


def test_freemidi_downloads_at_most_four(site, tmp_path):
    serve_freemidi(
        site, "many", {f"download3-{i}-s{i}": (fx.piano(), f"S{i}", None) for i in range(1, 8)}
    )
    assert len(run(site, "freemidi", "many", tmp_path)) == 4


def test_no_results_or_a_broken_site_gives_an_empty_list(site, tmp_path):
    assert run(site, "freemidi", "nothing", tmp_path) == []  # 404 from the stand-in
    site.serve(freemidi_search_url("empty"), "<html>no links</html>")
    assert run(site, "freemidi", "empty", tmp_path) == []
    assert run(site, "bitmidi", "nothing", tmp_path) == []


def test_unreachable_site_gives_an_empty_list(tmp_path):
    env = {**os.environ, "MIDI_BRIDGE_FREEMIDI_URL": "http://127.0.0.1:9"}
    result = subprocess.run(
        [sys.executable, "-m", "bridge_tools.search.cli", "--source", "freemidi", "--query", "q", "--dest", str(tmp_path)],
        capture_output=True, text=True, env=env, check=False,
    )  # fmt: skip
    assert (result.returncode, json.loads(result.stdout)) == (0, [])


def test_bitmidi_names_files_after_the_public_page_address(site, tmp_path):
    serve_bitmidi(site, "nocturne", {"chopin-nocturne-mid": (fx.piano(), "Chopin Nocturne")})
    assert run(site, "bitmidi", "nocturne", tmp_path) == [
        {
            "filename": bitmidi_filename("chopin-nocturne-mid"),
            "title": "Chopin Nocturne",
            "artist": None,
        }
    ]


@pytest.mark.live
@pytest.mark.parametrize("source", ["freemidi", "bitmidi"])
def test_real_site_still_works(source, site, tmp_path):
    """Opt in with `pytest -m live`. Fails when a site changed its pages."""
    found = run(site, source, "moonlight sonata", tmp_path, live=True)
    assert found, f"{source} returned nothing: the scraping may need updating"
    for song in found:
        assert (tmp_path / song["filename"]).read_bytes()[:4] == b"MThd"
