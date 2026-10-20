"""Stand-in for the Unity app: UDP in on 5005, UDP out to 5006, like UdpReceiver.cs."""

from __future__ import annotations

import json
import socket
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

UNITY_PORT = 5005
BRIDGE_PORT = 5006
HOST = "127.0.0.1"


@dataclass(frozen=True)
class Datagram:
    t: float  # time.monotonic() at reception
    raw: bytes

    @property
    def msg(self) -> Any:
        try:
            return json.loads(self.raw)
        except ValueError:
            return None

    @property
    def action(self) -> str | None:
        msg = self.msg
        return msg.get("action") if isinstance(msg, dict) else None


class FakeUnity:
    def __init__(self) -> None:
        self._rx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._rx.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._rx.bind((HOST, UNITY_PORT))
        self._rx.settimeout(0.1)
        self._log: list[Datagram] = []
        self._cond = threading.Condition()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                raw, _ = self._rx.recvfrom(65535)
            except TimeoutError:
                continue
            except OSError:
                return
            datagram = Datagram(time.monotonic(), raw)
            # Only the Rust bridge reports playback progress; leaving it out keeps the traffic
            # comparable with the original bridge's.
            if datagram.action == "song_progress":
                continue
            with self._cond:
                self._log.append(datagram)
                self._cond.notify_all()

    def close(self) -> None:
        self._stop.set()
        self._thread.join(timeout=2)
        self._rx.close()

    # -- sending ---------------------------------------------------------------------------

    def send(self, command: dict[str, Any] | bytes) -> None:
        raw = command if isinstance(command, bytes) else json.dumps(command).encode()
        # A new socket per datagram, like UdpReceiver.InviaJsonAPython. A reused socket can raise
        # ConnectionRefusedError for a ping that reached no listener before the bridge started.
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as tx:
            tx.sendto(raw, (HOST, BRIDGE_PORT))

    # -- observing -------------------------------------------------------------------------

    def mark(self) -> int:
        with self._cond:
            return len(self._log)

    def log(self, since: int = 0) -> list[Datagram]:
        with self._cond:
            return list(self._log[since:])

    def actions(self, since: int = 0) -> list[str | None]:
        return [d.action for d in self.log(since)]

    def wait_for(
        self,
        predicate: Callable[[Datagram], bool],
        timeout: float = 5.0,
        since: int = 0,
    ) -> Datagram:
        deadline = time.monotonic() + timeout
        with self._cond:
            seen = since
            while True:
                while seen < len(self._log):
                    if predicate(self._log[seen]):
                        return self._log[seen]
                    seen += 1
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise AssertionError(
                        f"timed out after {timeout}s; got {[d.raw for d in self._log[since:]]}"
                    )
                self._cond.wait(remaining)

    def wait_action(self, action: str, timeout: float = 5.0, since: int = 0) -> Datagram:
        return self.wait_for(lambda d: d.action == action, timeout, since)

    def wait_count(
        self, predicate: Callable[[Datagram], bool], count: int, timeout: float, since: int = 0
    ) -> list[Datagram]:
        deadline = time.monotonic() + timeout
        with self._cond:
            while True:
                hits = [d for d in self._log[since:] if predicate(d)]
                if len(hits) >= count:
                    return hits
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise AssertionError(
                        f"wanted {count} matches, got {len(hits)}: "
                        f"{[d.raw for d in self._log[since:]]}"
                    )
                self._cond.wait(remaining)

    def request(self, command: dict[str, Any], reply: str, timeout: float = 5.0) -> dict[str, Any]:
        """Sends a command and returns the first `reply` action received after it."""
        start = self.mark()
        self.send(command)
        return self.wait_action(reply, timeout, since=start).msg

    def quiet(self, duration: float) -> list[Datagram]:
        """Waits `duration` seconds and returns everything that arrived meanwhile."""
        start = self.mark()
        time.sleep(duration)
        return self.log(start)

    def wait_ready(self, timeout: float = 30.0) -> None:
        """Pings like Unity's heartbeat until the bridge answers `bridge_ready`."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            start = self.mark()
            self.send({"action": "ping"})
            try:
                self.wait_action("bridge_ready", timeout=0.5, since=start)
                return
            except AssertionError:
                continue
        raise AssertionError("bridge never became ready")


def note_msgs(datagrams: list[Datagram]) -> list[Datagram]:
    """Datagrams that are note events (press/release), as Unity classifies them."""
    return [d for d in datagrams if d.action in ("press", "release")]
