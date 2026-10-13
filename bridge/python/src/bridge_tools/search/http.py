"""One place for how this tool talks to the websites."""

from __future__ import annotations

import os

import requests

USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
SEARCH_TIMEOUT = 12
PAGE_TIMEOUT = 10
DOWNLOAD_TIMEOUT = 15

FREEMIDI_SITE = "https://freemidi.org"
BITMIDI_SITE = "https://www.bitmidi.com"


def site_url(canonical: str, env_var: str) -> str:
    """The URL to fetch from: the real site unless an environment variable points elsewhere
    (used by tests to talk to a local stand-in)."""
    return os.environ.get(env_var, canonical).rstrip("/")


def new_session() -> requests.Session:
    session = requests.Session()
    session.headers["User-Agent"] = USER_AGENT
    return session


def is_midi(data: bytes) -> bool:
    return data[:4] == b"MThd"
