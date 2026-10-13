"""Helpers shared by scenarios that observe a whole playback."""

from __future__ import annotations

import time
from dataclasses import dataclass

import mido

from .fake_unity import Datagram, note_msgs
from .midi_io import Sent


@dataclass
class Run:
    udp: list[Datagram]
    midi: list[Sent]
    t0: float  # time of the first note event, the reference for relative timings

    @property
    def notes(self) -> list[Datagram]:
        return note_msgs(self.udp)

    def times(self, action: str) -> list[float]:
        return [d.t - self.t0 for d in self.notes if d.action == action]

    def song_midi(self) -> list[mido.Message]:
        """MIDI out without the leading and trailing all-notes-off bursts."""
        msgs = [s.msg for s in self.midi]
        assert len(msgs) >= 32, f"expected two all-notes-off bursts, got {len(msgs)} messages"
        return msgs[16:-16]


def play(bridge, command: dict, *, until_clear_scenes: int = 2, timeout: float = 15.0) -> Run:
    """Sends a play command and waits for the playback to finish (second clear_scene)."""
    u_mark, s_mark = bridge.unity.mark(), bridge.synth.mark()
    bridge.unity.send(command)
    bridge.unity.wait_count(
        lambda d: d.action == "clear_scene", until_clear_scenes, timeout, since=u_mark
    )
    time.sleep(0.25)  # MIDI out is asynchronous: let the trailing messages arrive
    udp = bridge.unity.log(u_mark)
    notes = note_msgs(udp)
    t0 = notes[0].t if notes else udp[0].t
    return Run(udp, bridge.synth.received(s_mark), t0)


def assert_times(actual: list[float], expected: tuple[float, ...] | list[float], tol: float = 0.05):
    assert len(actual) == len(expected), f"{actual} vs {expected}"
    for got, want in zip(actual, expected, strict=True):
        assert abs(got - want) <= tol, f"{got:.3f}s != {want:.3f}s (+-{tol}): {actual}"
