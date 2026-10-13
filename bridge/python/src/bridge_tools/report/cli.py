"""`report-renderer`: reads session data (JSON) from stdin and writes the report files."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .model import parse_session
from .render import render_report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Render the practice report from session JSON on stdin."
    )
    parser.add_argument("--out-dir", type=Path, required=True, help="existing folder to write into")
    parser.add_argument("--pdf-name", default="report.pdf")
    args = parser.parse_args(argv)

    # Bytes, not text: the bridge sends UTF-8, while text stdin uses the locale's codepage
    # (cp1252 on Windows) and would garble non-ASCII song titles.
    data = parse_session(json.loads(sys.stdin.buffer.read()))
    pages = render_report(data, args.out_dir, args.pdf_name)
    print(json.dumps({"pages": pages}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
