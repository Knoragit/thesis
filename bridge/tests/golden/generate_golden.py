"""Regenerates tests/golden/data from the ORIGINAL bridge's report code.

    uv run --project python python tests/golden/generate_golden.py

Only needed when the cases change; the committed output is the specification the new
renderer is tested against.
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

import numpy as np
from pypdf import PdfReader

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from harness.legacy_layout import read_legacy_names, reference_script

from golden.cases import build_cases

DATA = Path(__file__).parent / "data"


def load_original(home: Path):
    """Imports the original script as a module, with no MIDI ports and a private HOME."""
    os.environ["HOME"] = str(home)
    os.environ["MPLBACKEND"] = "Agg"
    import mido

    mido.get_input_names = lambda *a, **k: []
    mido.get_output_names = lambda *a, **k: []
    spec = importlib.util.spec_from_file_location("original_bridge", reference_script())
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.send_to_unity = lambda message: None
    return module


def run_case(name: str, case: dict) -> None:
    names = read_legacy_names()
    with tempfile.TemporaryDirectory() as tmp:
        home = Path(tmp)
        data_dir = home / "Desktop" / names.data_dir
        fonts = data_dir.joinpath(*names.font_dir)
        shutil.copytree(reference_script().parent / "Raleway" / "static", fonts)
        bridge = load_original(home)

        bridge.recorded_notes_fai_tu = [dict(n) for n in case["fai_tu_notes"]]
        bridge.history_follow_sessions = [
            {
                "filename": s["filename"],
                "title_info": {"title": s["title"], "artist": s["artist"]},
                "reference_notes": [dict(n) for n in s["reference_notes"]],
                "user_notes": [dict(n) for n in s["user_notes"]],
            }
            for s in case["follow_sessions"]
        ]
        bridge.generate_unified_report()

        (session,) = (data_dir / names.sessions_subdir).iterdir()
        out = DATA / name
        shutil.rmtree(out, ignore_errors=True)
        out.mkdir(parents=True)
        pages = sorted(session.glob("pagina_*.png"), key=lambda p: int(p.stem.split("_")[1]))
        for page in pages:
            shutil.copy(page, out / page.name)

        reader = PdfReader(session / names.report_name)
        assert len(reader.pages) == len(pages)
        (out / "pdf_text.json").write_text(
            json.dumps([p.extract_text() for p in reader.pages], indent=1, ensure_ascii=False)
        )
        with wave.open(str(session / "Registrazione_Audio.wav")) as wav:
            samples = np.frombuffer(wav.readframes(wav.getnframes()), dtype=np.int16)
            audio = {
                "channels": wav.getnchannels(),
                "sample_width": wav.getsampwidth(),
                "rate": wav.getframerate(),
                "frames": wav.getnframes(),
                "peak": int(np.abs(samples).max()),
                "rms": float(np.sqrt(np.mean(samples.astype(np.float64) ** 2))),
            }
        (out / "audio.json").write_text(json.dumps(audio, indent=1))
        (out / "input.json").write_text(json.dumps(case, indent=1))
        print(f"{name}: {len(pages)} pages")


def main() -> None:
    cases = build_cases()
    if len(sys.argv) > 1:
        run_case(sys.argv[1], cases[sys.argv[1]])
        return
    # One process per case: matplotlib caches font lookups, so a second case in the same
    # process would not see its own copy of the font and would silently fall back to DejaVu.
    for name in cases:
        subprocess.run([sys.executable, __file__, name], check=True)


if __name__ == "__main__":
    main()
