"""Running MuseScore from the command line to engrave a MIDI file into PNG pages.

MuseScore is found through `MIDI_BRIDGE_MUSESCORE` (an empty value turns engraving off), then
on the `PATH`, then in the usual install folders.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

ENV_VAR = "MIDI_BRIDGE_MUSESCORE"
PNG_DPI = 150
TIMEOUT_SECONDS = 300

_NAMES = ("MuseScore4", "mscore4portable", "MuseScore3", "mscore", "musescore")
_INSTALL_PATHS = (
    r"C:\Program Files\MuseScore 4\bin\MuseScore4.exe",
    r"C:\Program Files\MuseScore 3\bin\MuseScore3.exe",
    r"C:\Program Files (x86)\MuseScore 3\bin\MuseScore3.exe",
    "/Applications/MuseScore 4.app/Contents/MacOS/mscore",
    "/Applications/MuseScore 3.app/Contents/MacOS/mscore",
)


# MIDI import options. MuseScore sometimes doubles the tempo and halves the bars of a song it
# reads as a human performance; these keep the bars of the file (and no pickup bar), so the
# score's bars are the bars the bridge counts. The rest keep a played (rather than written) file
# readable: a sixteenth-note grid at most (QuantValue 2), one voice per staff (VoiceCount 0) and
# no quintuplets, septuplets or nonuplets, which timing jitter would otherwise produce. The clefs
# never change: the treble staff is always the right hand and the bass staff the left.
_IMPORT_OPTIONS = """<?xml version="1.0" encoding="UTF-8"?>
<MidiOptions>
  <MeasureCount2xLess>true</MeasureCount2xLess>
  <RecognizePickupBar>false</RecognizePickupBar>
  <QuantValue>2</QuantValue>
  <VoiceCount>0</VoiceCount>
  <Quintuplets>false</Quintuplets>
  <Septuplets>false</Septuplets>
  <Nonuplets>false</Nonuplets>
  <ClefChanges>false</ClefChanges>
</MidiOptions>
"""


class EngravingError(RuntimeError):
    pass


@dataclass(frozen=True)
class Engraving:
    pages: list[Path]
    # When each page starts in the song, in seconds; one entry per page.
    page_starts: list[float]


def find_musescore() -> Path | None:
    configured = os.environ.get(ENV_VAR)
    if configured is not None:
        return Path(configured) if configured.strip() else None
    for name in _NAMES:
        found = shutil.which(name)
        if found:
            return Path(found)
    return next((Path(p) for p in _INSTALL_PATHS if Path(p).is_file()), None)


def engrave(
    midi: Path, out_dir: Path, prefix: str = "pagina", bars: list[float] | None = None
) -> Engraving:
    """Engraves `midi` into `out_dir` as `<prefix>_1.png`, `<prefix>_2.png`… (older pages with
    the same prefix are removed).

    `bars` (when each bar starts, in seconds) times the pages by bar number; otherwise they are
    timed by MuseScore's own clock, which can drift slightly from the file's."""
    musescore = find_musescore()
    if musescore is None:
        raise EngravingError("MuseScore not found (install it or set MIDI_BRIDGE_MUSESCORE)")
    # MuseScore works in a scratch folder with plain names: song names can be anything.
    with tempfile.TemporaryDirectory() as scratch_name:
        scratch = Path(scratch_name)
        shutil.copyfile(midi, scratch / "in.mid")
        job = [{"in": "in.mid", "out": ["out.png", "out.mpos"]}]
        (scratch / "job.json").write_text(json.dumps(job), encoding="utf-8")
        (scratch / "import.xml").write_text(_IMPORT_OPTIONS, encoding="utf-8")
        try:
            result = subprocess.run(
                [str(musescore), "-r", str(PNG_DPI), "-M", "import.xml", "-j", "job.json"],
                cwd=scratch,
                capture_output=True,
                timeout=TIMEOUT_SECONDS,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise EngravingError(f"cannot run MuseScore: {error}") from error
        rendered = sorted(scratch.glob("out-*.png"), key=lambda p: int(p.stem.rsplit("-", 1)[1]))
        if not rendered:
            stderr = result.stderr.decode(errors="replace").strip()[-500:]
            raise EngravingError(
                f"MuseScore produced no pages (exit {result.returncode}): {stderr}"
            )
        page_starts = _page_starts(scratch / "out.mpos", len(rendered), bars)

        for old in out_dir.glob(f"{prefix}_*.png"):
            old.unlink()
        pages = []
        for number, png in enumerate(rendered, start=1):
            page = out_dir / f"{prefix}_{number}.png"
            shutil.move(png, page)
            pages.append(page)
    return Engraving(pages=pages, page_starts=page_starts)


def _page_starts(mpos: Path, page_count: int, bars: list[float] | None) -> list[float]:
    """When the first bar of each page starts, from MuseScore's measure positions (`.mpos`: one
    element per bar, in order, with its page; one event per bar with its time in ms). A page
    without bars (or no positions at all) starts where the page before it does."""
    starts: dict[int, float] = {}
    try:
        root = ET.parse(mpos).getroot()
        measures = sorted(
            (int(e.get("id", "")), int(e.get("page", "0"))) for e in root.iter("element")
        )
        number_of = {str(id_): n for n, (id_, _) in enumerate(measures)}
        page_of = {str(id_): page for id_, page in measures}
        for event in root.iter("event"):
            element = event.get("elid", "")
            if element not in page_of:
                continue
            number = number_of[element]
            if bars:
                seconds = bars[min(number, len(bars) - 1)]
            else:
                seconds = int(event.get("position", "0")) / 1000.0
            page = page_of[element]
            starts[page] = min(seconds, starts.get(page, seconds))
    except (OSError, ET.ParseError, ValueError):
        pass
    result = []
    previous = 0.0
    for page in range(page_count):
        previous = max(previous, starts.get(page, previous)) if page else 0.0
        result.append(previous)
    return result
