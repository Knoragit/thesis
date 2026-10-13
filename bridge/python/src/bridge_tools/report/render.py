"""Turns session data into the report files."""

from __future__ import annotations

from pathlib import Path

import matplotlib

matplotlib.use("Agg")

import matplotlib.pyplot as plt
from matplotlib.backends.backend_pdf import PdfPages

from . import audio, fonts, pages
from .model import SessionData

PNG_DPI = 120
WAV_NAME = "Registrazione_Audio.wav"


def render_report(data: SessionData, out_dir: Path, pdf_name: str) -> int:
    """Writes the PDF, one `pagina_N.png` per page and the WAV of the free-play take into
    `out_dir`. Returns the number of pages."""
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
        for index, session in enumerate(data.played_sessions, start=1):
            add(pages.chromagram_page(index, session))
            add(pages.analysis_page(index, session))
    return page_count
