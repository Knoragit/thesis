"""Names the original bridge uses on disk, read from its source.

The new bridge takes these as launch arguments (Unity passes them in production), so the
harness extracts them from the original script instead of spelling them out.
"""

from __future__ import annotations

import os
import re
import shutil
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
BUNDLED_FONTS = REPO_ROOT / "python" / "src" / "bridge_tools" / "report" / "assets" / "fonts"


def reference_script() -> Path:
    override = os.environ.get("BRIDGE_REFERENCE_SCRIPT")
    if override:
        return Path(override)
    return REPO_ROOT.parent / "reference" / "midi_bridge.py"


def install_reference_fonts(fonts_dir: Path) -> None:
    """Puts the report font where the original looks for it (the new bridge bundles it)."""
    fonts_dir.mkdir(parents=True, exist_ok=True)
    for font in BUNDLED_FONTS.glob("*.ttf"):
        shutil.copy2(font, fonts_dir / font.name)


@dataclass(frozen=True)
class LegacyNames:
    data_dir: str
    songs_subdir: str
    sessions_subdir: str
    db_file: str
    report_name: str
    session_prefix: str
    font_dir: tuple[str, ...]


def _find(source: str, pattern: str) -> re.Match[str]:
    match = re.search(pattern, source)
    if match is None:
        raise RuntimeError(f"reference script no longer matches {pattern!r}")
    return match


def read_legacy_names(script: Path | None = None) -> LegacyNames:
    source = (script or reference_script()).read_text(encoding="utf-8")
    desktop = _find(source, r'os\.path\.join\(os\.path\.expanduser\("~"\), "Desktop", "([^"]+)"\)')
    songs = _find(source, r'CANZONI_PATH = os\.path\.join\(DESKTOP_PATH, "([^"]+)"\)')
    sessions = _find(source, r'SESSIONI_PATH = os\.path\.join\(DESKTOP_PATH, "([^"]+)"\)')
    db = _find(source, r'DB_PATH = os\.path\.join\(DESKTOP_PATH, "([^"]+)"\)')
    report = _find(source, r'pdf_filepath = os\.path\.join\(session_dir, "([^"]+)"\)')
    prefix = _find(source, r'f"([A-Za-z_]+)\{timestamp_str\}"')
    font = _find(source, r"RALWAY_DIR = os\.path\.join\(DESKTOP_PATH, ([^)]+)\)")
    font_dir = tuple(part.strip().strip('"') for part in font.group(1).split(","))
    return LegacyNames(
        data_dir=desktop.group(1),
        songs_subdir=songs.group(1),
        sessions_subdir=sessions.group(1),
        db_file=db.group(1),
        report_name=report.group(1),
        session_prefix=prefix.group(1),
        font_dir=font_dir,
    )


@dataclass(frozen=True)
class Layout:
    """Where one bridge instance keeps its files, under a throw-away home directory."""

    home: Path
    names: LegacyNames

    @property
    def data_dir(self) -> Path:
        return self.home / "Desktop" / self.names.data_dir

    @property
    def songs_dir(self) -> Path:
        return self.data_dir / self.names.songs_subdir

    @property
    def sessions_dir(self) -> Path:
        return self.data_dir / self.names.sessions_subdir

    @property
    def db_path(self) -> Path:
        return self.data_dir / self.names.db_file

    @property
    def fonts_dir(self) -> Path:
        return self.data_dir.joinpath(*self.names.font_dir)
