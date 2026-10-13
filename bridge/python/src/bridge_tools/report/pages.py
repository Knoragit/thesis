"""The pages of the report, one matplotlib figure each (US Letter portrait)."""

from __future__ import annotations

import matplotlib.pyplot as plt
import numpy as np
from matplotlib import patches
from matplotlib.figure import Figure

from .analysis import note_color, radar_metrics
from .model import FollowSession, PlayedNote
from .text import (
    FINGERPRINT_CATEGORIES,
    FINGERPRINT_GUIDE,
    follow_evaluation,
    free_play_feedback,
)

PAGE_SIZE = (8.5, 11)
PITCH_RANGE = (20, 109)
_TITLE_BOX = {
    "boxstyle": "round,pad=0.45",
    "facecolor": "#f8f9fa",
    "edgecolor": "#0288d1",
    "linewidth": 1.4,
}
_TEXT_BOX = {"boxstyle": "round", "facecolor": "#eef2f5", "edgecolor": "#c5d0d8", "alpha": 0.9}


def _new_page(title: str, fontsize: float) -> Figure:
    fig = plt.figure(figsize=PAGE_SIZE)
    fig.text(
        0.5,
        0.958,
        title,
        ha="center",
        va="center",
        fontsize=fontsize,
        fontweight="bold",
        bbox=_TITLE_BOX,
    )
    return fig


def _style_roll(ax, x_limit: float, *, ylabel: str, xlabel: str | None = None) -> None:
    ax.set_xlim(0, x_limit)
    ax.set_ylim(*PITCH_RANGE)
    ax.set_ylabel(ylabel, fontsize=8)
    if xlabel:
        ax.set_xlabel(xlabel, fontsize=8)
    ax.tick_params(axis="both", labelsize=8)
    ax.grid(True, linestyle="--", alpha=0.3)


def free_play_page(notes: list[PlayedNote]) -> Figure:
    """Page 1: the free-play take as a piano roll with the written feedback."""
    fig = _new_page("REPORT · MODALITÀ FAI TU", 15)
    grid = fig.add_gridspec(
        3,
        1,
        height_ratios=[1.3, 0.38, 1.0],
        left=0.12,
        right=0.88,
        top=0.90,
        bottom=0.05,
        hspace=0.45,
    )

    if not notes:
        ax = fig.add_subplot(1, 1, 1)
        ax.axis("off")
        ax.text(
            0.5,
            0.5,
            "Nessuna nota registrata nella modalità Fai Tu.",
            horizontalalignment="center",
            fontsize=12,
        )
        return fig

    t0 = min(n.start for n in notes)
    shifted = [PlayedNote(n.note, n.vel, n.start - t0, n.end - t0, n.hand) for n in notes]

    ax = fig.add_subplot(grid[0])
    ax.set_title("Cromagramma dell'Esecuzione (Piano Roll)", fontsize=10, pad=8)
    max_end = 1.0
    for n in shifted:
        dur = max(0.1, n.end - n.start)
        max_end = max(max_end, n.start + dur)
        ax.add_patch(
            patches.Rectangle(
                (n.start, n.note - 0.4), dur, 0.8, linewidth=0.4, edgecolor="black",
                facecolor=note_color(n.note, n.vel, n.hand),
            )
        )  # fmt: skip
    _style_roll(ax, max_end + 0.5, ylabel="Pitch MIDI", xlabel="Tempo (secondi)")

    _hand_legend(fig.add_subplot(grid[1]))

    ax_text = fig.add_subplot(grid[2])
    ax_text.axis("off")
    ax_text.text(
        0.0, 0.95, free_play_feedback(shifted), transform=ax_text.transAxes, fontsize=8,
        verticalalignment="top", bbox=_TEXT_BOX,
    )  # fmt: skip
    return fig


def _hand_legend(ax) -> None:
    ax.axis("off")
    gradient = np.linspace(0, 1, 150)
    left = np.stack([np.zeros(150), 1.0 - gradient, np.ones(150)], axis=1)[None, :, :]
    right = np.stack([np.ones(150), 0.92 * (1.0 - gradient), np.zeros(150)], axis=1)[None, :, :]
    ax.imshow(left, extent=[0.20, 1.55, 0.80, 1.25], aspect="auto", interpolation="nearest")
    ax.imshow(right, extent=[1.95, 3.30, 0.80, 1.25], aspect="auto", interpolation="nearest")
    ax.set_xlim(0.0, 3.6)
    ax.set_ylim(0.3, 1.7)
    ax.text(0.88, 1.45, "MANO SINISTRA", fontsize=7.5, ha="center", va="center", color="white",
            bbox={"facecolor": "black", "alpha": 0.5, "boxstyle": "round,pad=0.18"})  # fmt: skip
    ax.text(2.63, 1.45, "MANO DESTRA", fontsize=7.5, ha="center", va="center", color="white",
            bbox={"facecolor": "black", "alpha": 0.4, "boxstyle": "round,pad=0.18"})  # fmt: skip
    for x, label in ((0.20, "piano"), (1.55, "forte"), (1.95, "piano"), (3.30, "forte")):
        ax.text(x, 0.55, label, fontsize=7, color="dimgray", ha="center")
    ax.set_title(
        "Intensità per mano (hand tracking)", fontsize=8.5, pad=6, style="italic", color="dimgray"
    )


def _follow_page(index: int, session: FollowSession) -> Figure:
    fig = _new_page(f"ANALISI COMPARATIVA #{index} · MODALITÀ SEGUIMI", 13)
    fig.text(
        0.5,
        0.932,
        f"{session.title} — {session.artist}",
        ha="center",
        va="center",
        fontsize=10,
        color="#555",
    )
    return fig


def _played_span(session: FollowSession) -> float:
    """Seconds between the first and the last note the user started."""
    first = session.user_notes[0].start
    return max(n.start - first for n in session.user_notes)


def chromagram_page(index: int, session: FollowSession) -> Figure:
    """Follow-me page 1: the song's notes above the user's, on the same time scale."""
    fig = _follow_page(index, session)
    grid = fig.add_gridspec(
        2, 1, height_ratios=[1.0, 1.0], left=0.12, right=0.88, top=0.90, bottom=0.06, hspace=0.32
    )
    first = session.user_notes[0].start
    time_limit = max(_played_span(session) + 1.5, 5.0)

    ax_target = fig.add_subplot(grid[0])
    ax_target.set_title("1. Target (Brano Originale - Osservatore)", fontsize=10, pad=6)
    for n in session.reference_notes:
        if n.start <= time_limit:
            color = note_color(n.note, n.vel)
            ax_target.add_patch(
                patches.Rectangle(
                    (n.start, n.note - 0.4),
                    n.dur,
                    0.8,
                    facecolor=color,
                    edgecolor=color,
                    linewidth=0.1,
                )
            )
    _style_roll(ax_target, time_limit, ylabel="Pitch MIDI")

    ax_user = fig.add_subplot(grid[1])
    ax_user.set_title("2. Tua Esecuzione (Modalità Seguimi)", fontsize=10, pad=6)
    for n in session.user_notes:
        dur = max(0.05, n.end - n.start)
        ax_user.add_patch(
            patches.Rectangle((n.start - first, n.note - 0.4), dur, 0.8,
                              facecolor=note_color(n.note, n.vel), edgecolor="black", linewidth=0.3)
        )  # fmt: skip
    _style_roll(ax_user, time_limit, ylabel="Pitch MIDI", xlabel="Tempo (secondi)")
    return fig


def analysis_page(index: int, session: FollowSession) -> Figure:
    """Follow-me page 2: fingerprint radar of song vs user, written evaluation, and a guide."""
    fig = _follow_page(index, session)
    grid = fig.add_gridspec(
        4,
        1,
        height_ratios=[0.25, 1.1, 1.8, 0.85],
        left=0.12,
        right=0.88,
        top=0.90,
        bottom=0.05,
        hspace=0.42,
    )

    radar_row = grid[1].subgridspec(1, 3, width_ratios=[0.3, 1, 0.3], wspace=0.05)
    ax_radar = fig.add_subplot(radar_row[1], polar=True)
    angles = [
        i / float(len(FINGERPRINT_CATEGORIES)) * 2 * np.pi
        for i in range(len(FINGERPRINT_CATEGORIES))
    ]
    angles += angles[:1]

    def draw(values: list[float], color: str, label: str) -> None:
        closed = list(values) + list(values[:1])
        ax_radar.plot(angles, closed, linewidth=1.8, color=color, label=label)
        ax_radar.fill(angles, closed, color=color, alpha=0.22)

    draw(radar_metrics(session.reference_notes), "#4a90e2", "Target (Osservatore)")
    draw(radar_metrics(session.user_notes), "#9b59b6", "Tua Esecuzione (Seguimi)")
    ax_radar.set_xticks(angles[:-1])
    ax_radar.set_xticklabels(FINGERPRINT_CATEGORIES, fontsize=7.5)
    ax_radar.set_ylim(0, 1)
    ax_radar.set_title(
        "Impronta Digitale dell'Espressività: Target vs Tua Esecuzione", fontsize=9.5, pad=14
    )
    ax_radar.legend(loc="lower center", bbox_to_anchor=(0.5, -0.36), ncol=1, fontsize=6.5)

    ax_text = fig.add_subplot(grid[2])
    ax_text.axis("off")
    ax_text.text(0.0, 0.98, follow_evaluation(session, _played_span(session)), transform=ax_text.transAxes,
                 fontsize=7.5, verticalalignment="top", bbox=_TEXT_BOX)  # fmt: skip

    ax_guide = fig.add_subplot(grid[3])
    ax_guide.axis("off")
    ax_guide.text(0.02, 0.95, FINGERPRINT_GUIDE, transform=ax_guide.transAxes, fontsize=7.5,
                  verticalalignment="top", bbox=_TEXT_BOX)  # fmt: skip
    return fig
