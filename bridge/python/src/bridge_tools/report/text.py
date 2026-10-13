"""The written feedback in the report (Italian, as shown to the user)."""

from __future__ import annotations

import numpy as np

from .analysis import align_notes, classify_articulation
from .model import FollowSession, PlayedNote

FINGERPRINT_CATEGORIES = [
    "Marcato\n(Peso)",
    "Varietà\nDinamica",
    "Articolazione\n(Legato)",
    "Rubato\n(Flessibilità)",
    "Bilanciamento\nMani",
]

FINGERPRINT_GUIDE = (
    "GUIDA ALL'IMPRONTA DIGITALE (scala 0-1):\n"
    "• Marcato = intensità media del tocco · 0 = tocco leggerissimo · 1 = tocco molto pesante\n"
    "• Varietà = escursione dinamica · 0 = dinamica piatta · 1 = massimo contrasto piano/forte\n"
    "• Articolazione = durata note · 0 = staccato brevissimo · 1 = note lunghe e legate (≥ 0.5s)\n"
    "• Rubato = flessibilità ritmica · 0 = tempo metronomico rigido · 1 = fraseggio molto libero\n"
    "• Bilanciamento = melodia vs accomp. · 0 = destra assente · 0.5 = mani bilanciate · 1 = dominante\n"
    "Blu = Target (Osservatore) · Viola = Tua Esecuzione (Seguimi)"
)

DYNAMIC_SPREAD_THRESHOLD = 0.12
RHYTHM_SPREAD_THRESHOLD = 0.15


def free_play_feedback(notes: list[PlayedNote]) -> str:
    """Comments on a free-play take. `notes` start at time 0."""
    std_vel = float(np.std([n.vel for n in notes])) if len(notes) > 1 else 0.0
    vel_left = [n.vel for n in notes if n.note < 60]
    vel_right = [n.vel for n in notes if n.note >= 60]
    mean_left = float(np.mean(vel_left)) if vel_left else 0.0
    mean_right = float(np.mean(vel_right)) if vel_right else 0.0
    starts = sorted(n.start for n in notes)
    iois = np.diff(starts) if len(starts) > 1 else [0.0]
    std_ioi = float(np.std(iois)) if len(iois) > 1 else 0.0

    counts = classify_articulation(notes)
    total = max(1, counts["legato"] + counts["staccato"] + counts["medio"])
    pct_legato = 100.0 * counts["legato"] / total
    pct_staccato = 100.0 * counts["staccato"] / total
    pct_medium = 100.0 * counts["medio"] / total

    if std_vel < DYNAMIC_SPREAD_THRESHOLD:
        dynamics = "La tua esecuzione è dinamicamente piatta. Prova ad accentuare le note chiave."
    else:
        dynamics = "Ottima escursione dinamica: tocco vivo e ricco di sfumature."

    if mean_left > mean_right and mean_left > 0.0:
        balance = "La mano sinistra copre la melodia. Riduci il volume dell'accompagnamento."
    else:
        balance = "Buon equilibrio: la melodia (destra) risalta sull'accompagnamento."

    if std_ioi < RHYTHM_SPREAD_THRESHOLD:
        phrasing = "Il fraseggio è meccanico. Prova a rallentare leggermente a fine frase."
    else:
        phrasing = "Ottima flessibilità ritmica e naturalezza nel fraseggio (rubato)."

    return (
        "FEEDBACK SULLA TUA ESECUZIONE:\n"
        f"• Articolazione: {pct_legato:.0f}% note legate · {pct_staccato:.0f}% staccate · {pct_medium:.0f}% mediane\n"
        f"• Dinamica: {dynamics}\n"
        f"• Bilanciamento: {balance}\n"
        f"• Fraseggio: {phrasing}"
    )


def follow_evaluation(session: FollowSession, played_span: float) -> str:
    """Compares a follow-me performance with the song. `played_span` is how many seconds
    after its first note the user kept playing."""
    reference, user = session.reference_notes, session.user_notes

    ref_vel_mean = float(np.mean([n.vel for n in reference])) if reference else 0.5
    usr_vel_mean = float(np.mean([n.vel for n in user])) if user else 0.5
    diff_vel = usr_vel_mean - ref_vel_mean
    if diff_vel > 0.1:
        dynamics = "Tocco più marcato e incisivo rispetto all'originale."
    elif diff_vel < -0.1:
        dynamics = "Tocco più leggero e delicato rispetto all'originale."
    else:
        dynamics = "Dinamica e peso del tocco perfettamente in linea con il brano originale."

    ref_dur_mean = float(np.mean([n.dur for n in reference])) if reference else 0.3
    usr_dur_mean = float(np.mean([n.end - n.start for n in user])) if user else 0.3
    diff_dur = usr_dur_mean - ref_dur_mean
    if diff_dur < -0.1:
        articulation = (
            "Esecuzione tendente allo staccato/sgranato (note più brevi del riferimento)."
        )
    elif diff_dur > 0.1:
        articulation = "Esecuzione molto legata e sostenuta (tasti tenuti a lungo)."
    else:
        articulation = (
            "Articolazione (durata e fraseggio delle note) bilanciata ed in linea con l'originale."
        )

    tempo = (
        "Ottima aderenza al ritmo e alla sequenza delle note guida."
        if len(user) > 5
        else "Esecuzione parziale o con pause rilevanti."
    )

    return (
        f"DINAMICA (VOLUME E TOCCO):\n"
        f"  Target: {ref_vel_mean:.2f} · Tuo: {usr_vel_mean:.2f}\n"
        f"  → {dynamics}\n\n"
        f"ARTICOLAZIONE:\n"
        f"  Target: {ref_dur_mean:.2f}s · Tua: {usr_dur_mean:.2f}s\n"
        f"  → {articulation}\n\n"
        f"TEMPO E RITMICA:\n"
        f"  → {tempo}\n\n"
        f"{_alignment_block(session, played_span)}"
    )


def _alignment_block(session: FollowSession, played_span: float) -> str:
    user = session.user_notes
    alignment = align_notes(user, session.reference_notes, ref_window=played_span)
    if alignment.matched == 0:
        return (
            "• ALLINEAMENTO NOTA-PER-NOTA (tratto eseguito):\n"
            "  Nessuna nota appaiabile al riferimento (esecuzione molto distante dalla guida)."
        )

    median_vel_ratio = float(np.median(alignment.vel_ratios))
    median_dur_ratio = float(np.median(alignment.dur_ratios))
    mean_onset_error = float(np.mean(alignment.onset_errors))
    pct_matched = 100.0 * alignment.matched / max(1, len(user))

    if median_dur_ratio < 0.9:
        duration_comment = "più staccato dell'originale"
    elif median_dur_ratio > 1.1:
        duration_comment = "più legato/sostenuto dell'originale"
    else:
        duration_comment = "in linea con l'articolazione del target"
    if median_vel_ratio < 0.9:
        velocity_comment = "tocco più leggero del riferimento"
    elif median_vel_ratio > 1.1:
        velocity_comment = "tocco più forte e incisivo del riferimento"
    else:
        velocity_comment = "intensità in linea con il riferimento"

    return (
        f"• ALLINEAMENTO NOTA-PER-NOTA (tratto eseguito):\n"
        f"  Riconosciute: {alignment.matched} su {len(user)} suonate ({pct_matched:.0f}%) → note che coincidono con la guida.\n"
        f"  Mancate: {alignment.missed} → note della guida nel tratto eseguito che non hai suonato.\n"
        f"  Extra: {alignment.extra} → note suonate in più rispetto alla guida proposta.\n"
        f"  Velocity relativa (mediana): {median_vel_ratio * 100:.0f}% del target → {velocity_comment}.\n"
        f"  Durata relativa (mediana): {median_dur_ratio * 100:.0f}% → {duration_comment}.\n"
        f"  Errore medio di attacco: {mean_onset_error * 1000:.0f} ms → precisione temporale rispetto alla guida."
    )
