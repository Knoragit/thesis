"""Fixture pages shaped like the ones freemidi.org and bitmidi.com serve."""

from __future__ import annotations

import hashlib
import urllib.parse

from .http_fixtures import FixtureSite


def freemidi_search_url(query: str) -> str:
    return f"/freemidi.org/search?q={urllib.parse.quote(query)}"


def serve_freemidi(
    site: FixtureSite,
    query: str,
    tracks: dict[str, tuple[bytes, str | None, str | None]],
) -> None:
    """tracks maps a slug like `download3-101-moonlight` to (midi bytes, title, artist)."""
    links = "".join(f'<a href="/{slug}">x</a>' for slug in tracks)
    site.serve(freemidi_search_url(query), f"<html>{links}</html>")
    for slug, (data, title, artist) in tracks.items():
        track_id = slug.split("-")[1]
        page = ""
        if title:
            page += f"<div class=download-title-container style=display:none>{title} Midi</div>"
        if artist:
            page += f'<a itemprop=item href="/artist-7-someone"> <span itemprop=name>{artist}</span></a>'
        site.serve(f"/freemidi.org/{slug}", f"<html>{page}</html>")
        site.serve(f"/freemidi.org/getter-{track_id}", data)


def bitmidi_page_url(slug: str) -> str:
    return f"https://www.bitmidi.com/{slug}"


def bitmidi_filename(slug: str) -> str:
    return hashlib.md5(bitmidi_page_url(slug).encode()).hexdigest()[:10] + ".mid"


def serve_bitmidi(site: FixtureSite, query: str, tracks: dict[str, tuple[bytes, str]]) -> None:
    """tracks maps a page slug like `moonlight-mid` to (midi bytes, page title)."""
    links = "".join(f'<a href="/{slug}">x</a>' for slug in tracks)
    site.serve(f"/bitmidi.com/search?q={urllib.parse.quote(query)}", f"<html>{links}</html>")
    for slug, (data, title) in tracks.items():
        upload = f"/uploads/{slug}.mid"
        site.serve(
            f"/bitmidi.com/{slug}",
            f'<html><title>{title}.mid | BitMIDI</title><a href="{upload}">dl</a></html>',
        )
        site.serve(f"/bitmidi.com{upload}", data)
