"""`score-renderer`: engraves a MIDI file into `pagina_N.png` pages plus `score.json`."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .musescore import EngravingError, engrave

SCORE_INFO = "score.json"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Engrave a MIDI file with MuseScore.")
    parser.add_argument("--midi", type=Path, required=True)
    parser.add_argument("--out-dir", type=Path, required=True, help="existing folder to write into")
    parser.add_argument(
        "--bars", type=Path, help="JSON list of when each bar starts (seconds), to time the pages"
    )
    args = parser.parse_args(argv)

    bars = json.loads(args.bars.read_text(encoding="utf-8")) if args.bars else None
    try:
        engraving = engrave(args.midi, args.out_dir, bars=bars)
    except EngravingError as error:
        print(error, file=sys.stderr)
        return 1
    info = {"pages": len(engraving.pages), "page_starts": engraving.page_starts}
    # Written last: the bridge reuses a folder only once this file is there.
    (args.out_dir / SCORE_INFO).write_text(json.dumps(info), encoding="utf-8")
    print(json.dumps({"pages": len(engraving.pages)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
