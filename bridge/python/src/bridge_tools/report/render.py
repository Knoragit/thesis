"""Turns session data into the report files."""

from __future__ import annotations

import sys
from pathlib import Path

import matplotlib

matplotlib.use("Agg")

import matplotlib.pyplot as plt
from matplotlib.backends.backend_pdf import PdfPages

from ..score import musescore, smf
from . import audio, fonts, pages
from .model import PlayedNote, SessionData

PNG_DPI = 120
WAV_NAME = "Registrazione_Audio.wav"
MIDI_NAME = "Esecuzione_Fai_Tu.mid"


def render_report(data: SessionData, out_dir: Path, pdf_name: str) -> int:
    """Writes the PDF, one `pagina_N.png` per page and the WAV of the free-play take into
    `out_dir`. The free-play page is followed by the take as sheet music when MuseScore is
    available. Returns the number of pages."""
    fonts.use_raleway()
    audio.write_wav(out_dir / WAV_NAME, audio.synthesize(data.fai_tu_notes))

    page_count = 0
    with PdfPages(out_dir / pdf_name) as pdf:

        def add(figure) -> None:
            nonlocal page_count
            pdf.savefig(figure)
            page_count += 1
            figure.savefig(out_dir / f"pagina_{page_count}.png", dpi=PNG_DPI)
            plt.close(figure)

        add(pages.free_play_page(data.fai_tu_notes))
        sheets = _free_play_score(data.fai_tu_notes, out_dir)
        for number, sheet in enumerate(sheets, start=1):
            add(pages.score_page(sheet, number, len(sheets)))
        for index, session in enumerate(data.played_sessions, start=1):
            add(pages.chromagram_page(index, session))
            add(pages.analysis_page(index, session))
    return page_count


def _free_play_score(notes: list[PlayedNote], out_dir: Path) -> list[Path]:
    """The free-play take engraved by MuseScore, one image per page, kept in `out_dir` with
    the take as a MIDI file. Without MuseScore, or if engraving fails, the report simply has no
    score pages."""
    if not notes or musescore.find_musescore() is None:
        return []
    midi = out_dir / MIDI_NAME
    smf.write_notes(midi, ((n.note, n.vel, n.start, n.end, n.hand) for n in notes))
    try:
        return musescore.engrave(midi, out_dir, prefix="spartito_fai_tu").pages
    except musescore.EngravingError as error:
        print(f"free-play score skipped: {error}", file=sys.stderr)
        return []
