"""`song-search`: downloads the top results of a search into a folder and lists them as JSON."""

from __future__ import annotations

import argparse
import json
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from .sources import SOURCES


def search(source: str, query: str, dest: Path) -> list[dict[str, str | None]]:
    """Downloads every candidate in parallel; results keep the order the site ranked them."""
    site = SOURCES[source]()
    candidates = site.candidates(query)
    if not candidates:
        return []
    with ThreadPoolExecutor(max_workers=len(candidates)) as pool:
        downloads = list(pool.map(lambda candidate: site.download(candidate, dest), candidates))
    return [
        {"filename": d.filename, "title": d.title, "artist": d.artist}
        for d in downloads
        if d is not None
    ]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Download MIDI files matching a search.")
    parser.add_argument("--source", choices=sorted(SOURCES), required=True)
    parser.add_argument("--query", required=True)
    parser.add_argument(
        "--dest", type=Path, required=True, help="existing folder for the downloads"
    )
    args = parser.parse_args(argv)
    print(json.dumps(search(args.source, args.query, args.dest)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
