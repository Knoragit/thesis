"""The implementations under test, behind one interface."""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

from .legacy_layout import REPO_ROOT, Layout, reference_script
from .midi_io import KEYBOARD_NAME, SYNTH_NAME


@dataclass(frozen=True)
class LaunchContext:
    layout: Layout
    log_path: Path
    http_base: str | None


class Profile:
    name: str
    #: Divergence ids (D1..D7) for which this implementation has the fixed behaviour.
    fixed: frozenset[str]

    def start(self, ctx: LaunchContext) -> subprocess.Popen[bytes]:
        raise NotImplementedError

    def fixes(self, divergence: str) -> bool:
        return divergence in self.fixed


def _spawn(argv: list[str], env: dict[str, str], log_path: Path) -> subprocess.Popen[bytes]:
    log = log_path.open("wb")
    return subprocess.Popen(
        argv,
        env=env,
        stdout=log,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )


class ReferenceProfile(Profile):
    """The original Python bridge, run through the thin environment shim."""

    name = "reference"
    fixed = frozenset()

    def start(self, ctx: LaunchContext) -> subprocess.Popen[bytes]:
        env = {
            **os.environ,
            "HOME": str(ctx.layout.home),
            "USERPROFILE": str(ctx.layout.home),
            "PYTHONUNBUFFERED": "1",
            "PYTHONIOENCODING": "utf-8",
            "MPLBACKEND": "Agg",
            # Shared across runs: each run has a fresh HOME, and rebuilding the font cache is slow.
            "MPLCONFIGDIR": str(Path(tempfile.gettempdir()) / "midi-bridge-test-mplcache"),
            "BRIDGE_TEST_MIDI_IN": KEYBOARD_NAME,
            "BRIDGE_TEST_MIDI_OUT": SYNTH_NAME,
        }
        if ctx.http_base:
            env["BRIDGE_HTTP_FIXTURE_BASE"] = ctx.http_base
        shim = REPO_ROOT / "tests" / "reference" / "run_reference.py"
        argv = [sys.executable, str(shim), str(reference_script())]
        return _spawn(argv, env, ctx.log_path)


class RustProfile(Profile):
    name = "rust"
    fixed = frozenset({"D1", "D2", "D3", "D4", "D5", "D6", "D7"})

    def start(self, ctx: LaunchContext) -> subprocess.Popen[bytes]:
        names = ctx.layout.names
        binary = os.environ.get(
            "BRIDGE_RUST_BIN", str(REPO_ROOT / "target" / "debug" / "midi-bridge")
        )
        argv = [
            binary,
            "--data-dir", str(ctx.layout.data_dir),
            "--db-file", names.db_file,
            "--report-name", names.report_name,
            "--songs-subdir", names.songs_subdir,
            "--sessions-subdir", names.sessions_subdir,
            "--midi-in", KEYBOARD_NAME,
            "--midi-out", SYNTH_NAME,
        ]  # fmt: skip
        env = {
            **os.environ,
            "HOME": str(ctx.layout.home),
            "RUST_LOG": os.environ.get("RUST_LOG", "debug"),
        }
        if ctx.http_base:
            env["MIDI_BRIDGE_FREEMIDI_URL"] = f"{ctx.http_base}/freemidi.org"
            env["MIDI_BRIDGE_BITMIDI_URL"] = f"{ctx.http_base}/bitmidi.com"
        return _spawn(argv, env, ctx.log_path)


def selected_profile() -> Profile:
    choice = os.environ.get("BRIDGE_IMPL", "reference")
    if choice == "reference":
        return ReferenceProfile()
    if choice == "rust":
        return RustProfile()
    raise RuntimeError(f"BRIDGE_IMPL must be 'reference' or 'rust', got {choice!r}")
