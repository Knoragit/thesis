"""The session data the bridge hands to the renderer (JSON, schema version 1)."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

SCHEMA_VERSION = 1


@dataclass(frozen=True)
class ReferenceNote:
    """A note of the song as written in the MIDI file."""

    note: int
    vel: float
    start: float
    dur: float

    @property
    def duration(self) -> float:
        return self.dur


@dataclass(frozen=True)
class PlayedNote:
    """A note the user played: a key press and its release."""

    note: int
    vel: float
    start: float
    end: float
    hand: str | None = None  # "left" / "right" when hand tracking said so

    @property
    def duration(self) -> float:
        return self.end - self.start


@dataclass(frozen=True)
class FollowSession:
    filename: str
    title: str
    artist: str
    reference_notes: list[ReferenceNote]
    user_notes: list[PlayedNote]


@dataclass(frozen=True)
class SessionData:
    fai_tu_notes: list[PlayedNote]
    follow_sessions: list[FollowSession]

    @property
    def played_sessions(self) -> list[FollowSession]:
        """Only the songs in which the user played something get report pages."""
        return [s for s in self.follow_sessions if s.user_notes]


def parse_session(raw: dict[str, Any]) -> SessionData:
    version = raw.get("version")
    if version != SCHEMA_VERSION:
        raise ValueError(f"unsupported session data version {version!r}")
    return SessionData(
        fai_tu_notes=[PlayedNote(**n) for n in raw["fai_tu_notes"]],
        follow_sessions=[
            FollowSession(
                filename=s["filename"],
                title=s["title"],
                artist=s["artist"],
                reference_notes=[ReferenceNote(**n) for n in s["reference_notes"]],
                user_notes=[PlayedNote(**n) for n in s["user_notes"]],
            )
            for s in raw["follow_sessions"]
        ],
    )
