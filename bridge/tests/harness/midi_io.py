"""Virtual MIDI ports: a keyboard that feeds the bridge and a piano that records its output."""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass

import mido

KEYBOARD_NAME = "Bridge Test Keyboard"
SYNTH_NAME = "Bridge Test Synth"


@dataclass(frozen=True)
class Sent:
    t: float
    msg: mido.Message


class VirtualKeyboard:
    """Appears to the bridge as a MIDI input device."""

    def __init__(self) -> None:
        self._port = mido.open_output(KEYBOARD_NAME, virtual=True)

    def press(self, note: int, velocity: int = 100) -> None:
        self._port.send(mido.Message("note_on", note=note, velocity=velocity))

    def release(self, note: int, velocity: int = 0) -> None:
        self._port.send(mido.Message("note_off", note=note, velocity=velocity))

    def release_as_note_on_zero(self, note: int) -> None:
        self._port.send(mido.Message("note_on", note=note, velocity=0))

    def send(self, msg: mido.Message) -> None:
        self._port.send(msg)

    def tap(self, note: int, velocity: int = 100, hold: float = 0.05) -> None:
        self.press(note, velocity)
        time.sleep(hold)
        self.release(note)

    def close(self) -> None:
        self._port.close()


class VirtualSynth:
    """Appears to the bridge as a MIDI output device and records what it receives."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._received: list[Sent] = []
        self._port = mido.open_input(SYNTH_NAME, virtual=True, callback=self._on_message)

    def _on_message(self, msg: mido.Message) -> None:
        with self._lock:
            self._received.append(Sent(time.monotonic(), msg))

    def mark(self) -> int:
        with self._lock:
            return len(self._received)

    def received(self, since: int = 0) -> list[Sent]:
        with self._lock:
            return list(self._received[since:])

    def close(self) -> None:
        self._port.close()


def is_all_notes_off(msgs: list[mido.Message]) -> bool:
    """True if `msgs` is exactly CC123=0 on channels 0..15, in order."""
    expected = [
        mido.Message("control_change", channel=ch, control=123, value=0) for ch in range(16)
    ]
    return [m.bytes() for m in msgs] == [m.bytes() for m in expected]
