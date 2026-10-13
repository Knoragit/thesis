"""A simple synthesized recording of the free-play take."""

from __future__ import annotations

import wave
from pathlib import Path

import numpy as np

from .model import PlayedNote

SAMPLE_RATE = 44_100


def synthesize(notes: list[PlayedNote], sample_rate: int = SAMPLE_RATE) -> np.ndarray:
    """Mixes the notes into mono 16-bit samples: a sine with a quiet octave, a short attack and
    an exponential decay. With no notes, a one-second middle C stands in for the recording."""
    if not notes:
        notes = [PlayedNote(note=60, vel=0.8, start=0.0, end=1.0)]

    t0 = min(n.start for n in notes)
    t_end = max(n.end for n in notes)
    total_samples = int(max(2.0, (t_end - t0) + 1.0) * sample_rate)
    mix = np.zeros(total_samples, dtype=np.float32)

    for n in notes:
        duration = max(0.3, n.end - n.start)
        frequency = 440.0 * (2.0 ** ((n.note - 69) / 12.0))
        velocity = max(0.2, min(1.0, n.vel))

        length = int(duration * sample_rate)
        t = np.linspace(0, duration, length, endpoint=False)
        wave_ = (
            np.sin(2 * np.pi * frequency * t) + 0.3 * np.sin(2 * np.pi * 2 * frequency * t)
        ) * velocity

        attack = int(min(0.01 * sample_rate, length / 2))
        envelope = np.ones(length, dtype=np.float32)
        if attack > 0:
            envelope[:attack] = np.linspace(0, 1, attack)
        note_audio = wave_ * envelope * np.exp(-3.0 * t / duration)

        start = int((n.start - t0) * sample_rate)
        end = start + len(note_audio)
        if end <= total_samples:
            mix[start:end] += note_audio

    peak = np.max(np.abs(mix))
    if peak > 0:
        mix = (mix / peak) * 0.85
    else:
        t = np.linspace(0, 1.0, sample_rate, endpoint=False)
        mix[:sample_rate] = 0.5 * np.sin(2 * np.pi * 440.0 * t)
    return (mix * 32767).astype(np.int16)


def write_wav(path: Path, samples: np.ndarray, sample_rate: int = SAMPLE_RATE) -> None:
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(samples.tobytes())
