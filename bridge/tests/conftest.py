from __future__ import annotations

import signal
import subprocess
import time
from collections.abc import Callable, Iterator
from dataclasses import dataclass, field
from pathlib import Path

import pytest
from harness.fake_unity import FakeUnity
from harness.http_fixtures import FixtureSite, start_fixture_server
from harness.legacy_layout import Layout, install_reference_fonts, read_legacy_names
from harness.midi_io import VirtualKeyboard, VirtualSynth
from harness.profiles import LaunchContext, Profile, selected_profile

REPO_TESTS = Path(__file__).parent


def pytest_collection_modifyitems(items: list[pytest.Item]) -> None:
    for item in items:
        if "e2e" in Path(str(item.fspath)).parts:
            item.add_marker(pytest.mark.e2e)


@pytest.hookimpl(wrapper=True)
def pytest_runtest_makereport(item: pytest.Item, call: pytest.CallInfo[None]):
    report = yield
    setattr(item, f"rep_{report.when}", report)
    return report


@pytest.fixture(scope="session")
def profile() -> Profile:
    return selected_profile()


@pytest.fixture(scope="session")
def keyboard() -> Iterator[VirtualKeyboard]:
    kb = VirtualKeyboard()
    yield kb
    kb.close()


@pytest.fixture(scope="session")
def synth() -> Iterator[VirtualSynth]:
    s = VirtualSynth()
    yield s
    s.close()


@pytest.fixture(scope="session")
def http_site() -> Iterator[FixtureSite]:
    site, server = start_fixture_server()
    yield site
    server.shutdown()


@dataclass
class RunningBridge:
    profile: Profile
    layout: Layout
    unity: FakeUnity
    keyboard: VirtualKeyboard
    synth: VirtualSynth
    http: FixtureSite
    log_path: Path
    process: subprocess.Popen[bytes] | None = None
    _starts: int = field(default=0)
    _synth_base: int = field(default=0)

    def start(self, wait_ready: bool = True) -> None:
        self._starts += 1
        self._synth_base = self.synth.mark()
        log = self.log_path.with_suffix(f".{self._starts}.log")
        ctx = LaunchContext(self.layout, log, self.http.base_url)
        self.log_file = log
        self.process = self.profile.start(ctx)
        if wait_ready:
            self.unity.wait_ready()
            time.sleep(0.2)  # let the MIDI input loop settle

    def stop(self) -> None:
        proc, self.process = self.process, None
        if proc is None:
            return
        try:
            proc.send_signal(signal.SIGTERM)
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()

    def restart(self) -> None:
        self.stop()
        self.start()

    def midi_out(self) -> list:
        """MIDI the bridge sent to the piano since it was (re)started."""
        return self.synth.received(self._synth_base)

    def log_text(self) -> str:
        try:
            return self.log_file.read_text(errors="replace")
        except (AttributeError, OSError):
            return ""


@pytest.fixture
def make_bridge(
    request: pytest.FixtureRequest,
    profile: Profile,
    keyboard: VirtualKeyboard,
    synth: VirtualSynth,
    http_site: FixtureSite,
    tmp_path: Path,
) -> Iterator[Callable[..., RunningBridge]]:
    created: list[RunningBridge] = []
    http_site.reset()

    def factory(
        songs: dict[str, bytes] | None = None,
        *,
        wait_ready: bool = True,
        fonts: bool = True,
    ) -> RunningBridge:
        layout = Layout(tmp_path / "home", read_legacy_names())
        layout.songs_dir.mkdir(parents=True)
        layout.sessions_dir.mkdir(parents=True)
        for name, data in (songs or {}).items():
            (layout.songs_dir / name).write_bytes(data)
        if fonts:
            install_reference_fonts(layout.fonts_dir)
        bridge = RunningBridge(
            profile, layout, FakeUnity(), keyboard, synth, http_site, tmp_path / "bridge"
        )
        created.append(bridge)
        bridge.start(wait_ready=wait_ready)
        return bridge

    yield factory

    failed = getattr(request.node, "rep_call", None) is not None and request.node.rep_call.failed
    for bridge in created:
        bridge.stop()
        if failed:
            print(f"\n--- {profile.name} bridge log ---\n{bridge.log_text()}")
        bridge.unity.close()
