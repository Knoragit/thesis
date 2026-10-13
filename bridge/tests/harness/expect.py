"""Expected wire bytes, produced with Python's own json.dumps as the oracle."""

from __future__ import annotations

import json
from typing import Any


def wire(obj: dict[str, Any]) -> bytes:
    return json.dumps(obj).encode()


def press(note: int, velocity: float) -> bytes:
    return wire({"note": note, "velocity": velocity, "action": "press"})


def release(note: int, velocity: float) -> bytes:
    return wire({"note": note, "velocity": velocity, "action": "release"})


CLEAR_SCENE = wire({"action": "clear_scene"})
BRIDGE_READY = wire({"action": "bridge_ready"})
EXAMPLE_DONE = wire({"action": "example_done"})


def midi_velocity(value: int) -> float:
    """How the bridge turns a MIDI velocity into the float Unity receives."""
    return value / 127.0
