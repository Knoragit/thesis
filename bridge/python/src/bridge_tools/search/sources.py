"""The two sites: how to search them and how to download one of their songs."""

from __future__ import annotations

import hashlib
import re
import urllib.parse
from dataclasses import dataclass
from pathlib import Path

from .http import (
    BITMIDI_SITE,
    DOWNLOAD_TIMEOUT,
    FREEMIDI_SITE,
    PAGE_TIMEOUT,
    SEARCH_TIMEOUT,
    is_midi,
    new_session,
    site_url,
)

MAX_RESULTS = 4


@dataclass(frozen=True)
class Download:
    """A song saved to disk, with whatever the site told us about it."""

    filename: str
    title: str | None
    artist: str | None


def extract_freemidi_metadata(html: str) -> tuple[str | None, str | None]:
    """(title, artist) from a freemidi.org download page."""
    title = None
    match = re.search(
        r'class=["\']?download-title-container["\']?[^>]*style=["\']?display:none["\']?>([^<]+)',
        html,
    ) or re.search(r'class=["\']?download-title-container["\']?[^>]*>\s*<h1>([^<]+)</h1>', html)
    if match:
        title = match.group(1).strip()
        if title.lower().endswith(" midi"):
            title = title[:-5].strip()

    artist = None
    match = re.search(
        r'href=["\']?/artist-\d+-[^"\']*["\']?[^>]*>\s*<span itemprop=["\']?name["\']?>([^<]+)',
        html,
    )
    if match:
        artist = match.group(1).strip()
    return title, artist


class FreeMidi:
    """freemidi.org: a results page of `download3-<id>-<slug>` links, each with a download page
    and a `getter-<id>` file that only answers when the download page is given as Referer."""

    def __init__(self) -> None:
        self.base = site_url(FREEMIDI_SITE, "MIDI_BRIDGE_FREEMIDI_URL")

    def candidates(self, query: str) -> list[str]:
        try:
            response = new_session().get(
                f"{self.base}/search?q={urllib.parse.quote(query)}", timeout=SEARCH_TIMEOUT
            )
        except OSError:
            return []
        if response.status_code != 200:
            return []
        links = list(dict.fromkeys(re.findall(r"download3-\d+-[\w-]+", response.text)))
        return links[:MAX_RESULTS]

    def download(self, slug: str, dest: Path) -> Download | None:
        track_id = re.search(r"download3-(\d+)", slug).group(1)
        session = new_session()
        try:
            page = session.get(f"{self.base}/{slug}", timeout=PAGE_TIMEOUT)
            title, artist = extract_freemidi_metadata(page.text)
            # The site checks the Referer against its own address, not wherever we fetch from.
            file = session.get(
                f"{self.base}/getter-{track_id}",
                headers={"Referer": f"{FREEMIDI_SITE}/{slug}"},
                timeout=DOWNLOAD_TIMEOUT,
            )
        except OSError:
            return None
        if file.status_code != 200 or not is_midi(file.content):
            return None
        filename = slug.replace("download3-", "") + ".mid"
        (dest / filename).write_bytes(file.content)
        return Download(filename, title, artist)


class BitMidi:
    """bitmidi.com: a results page of song pages (slugs ending in `-mid`), each linking to an
    upload."""

    def __init__(self) -> None:
        self.base = site_url(BITMIDI_SITE, "MIDI_BRIDGE_BITMIDI_URL")

    def candidates(self, query: str) -> list[str]:
        try:
            response = new_session().get(
                f"{self.base}/search?q={urllib.parse.quote(query)}", timeout=SEARCH_TIMEOUT
            )
        except OSError:
            return []
        if response.status_code != 200:
            return []
        return list(dict.fromkeys(re.findall(r'href="(/[^"]*-mid)"', response.text)))[:MAX_RESULTS]

    def download(self, path: str, dest: Path) -> Download | None:
        session = new_session()
        try:
            page = session.get(f"{self.base}{path}", timeout=SEARCH_TIMEOUT)
            if page.status_code != 200:
                return None
            links = re.findall(
                r'(?:href|src)="(?:[^"]*)(/uploads/[^"]+\.mid)"', page.text, re.IGNORECASE
            )
            if not links:
                links = re.findall(r'(?:href|src)="([^"]+\.mid[^"]*)"', page.text, re.IGNORECASE)
            for link in links:
                url = link if link.startswith("http") else f"{self.base}{link}"
                file = session.get(
                    url, headers={"Referer": f"{BITMIDI_SITE}{path}"}, timeout=DOWNLOAD_TIMEOUT
                )
                if file.status_code == 200 and is_midi(file.content):
                    # Named after the song page's public address, so names do not depend on `base`.
                    filename = (
                        hashlib.md5(f"{BITMIDI_SITE}{path}".encode()).hexdigest()[:10] + ".mid"
                    )
                    (dest / filename).write_bytes(file.content)
                    title_match = re.search(r"<title>([^<]+)</title>", page.text, re.IGNORECASE)
                    title = (
                        re.sub(
                            r"\s*\.mid\s*.*$", "", title_match.group(1), flags=re.IGNORECASE
                        ).strip()
                        if title_match
                        else None
                    )
                    return Download(filename, title, None)
        except OSError:
            return None
        return None


SOURCES = {"freemidi": FreeMidi, "bitmidi": BitMidi}
