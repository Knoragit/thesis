"""The new report renderer must reproduce what the original bridge produced for the same data."""

from __future__ import annotations

import json
import subprocess
import sys
import wave
from pathlib import Path

import numpy as np
import pytest
from PIL import Image, ImageChops
from pypdf import PdfReader

DATA = Path(__file__).parent / "data"
CASES = sorted(p.name for p in DATA.iterdir() if p.is_dir())


def render(case: str, out_dir: Path) -> dict:
    result = subprocess.run(
        [
            sys.executable,
            "-m",
            "bridge_tools.report.cli",
            "--out-dir",
            str(out_dir),
            "--pdf-name",
            "r.pdf",
        ],
        input=(DATA / case / "input.json").read_text(),
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stderr
    return json.loads(result.stdout)


@pytest.fixture(scope="module", params=CASES)
def rendered(request, tmp_path_factory):
    out = tmp_path_factory.mktemp(request.param)
    return request.param, out, render(request.param, out)


def test_pages_are_pixel_identical_to_the_original(rendered):
    case, out, result = rendered
    expected = sorted((DATA / case).glob("pagina_*.png"), key=lambda p: int(p.stem.split("_")[1]))
    assert result == {"pages": len(expected)}
    for golden in expected:
        with Image.open(golden) as want, Image.open(out / golden.name) as got:
            assert want.size == got.size
            assert (
                ImageChops.difference(want.convert("RGB"), got.convert("RGB")).getbbox() is None
            ), golden.name


def test_pdf_text_matches_the_original(rendered):
    case, out, _ = rendered
    expected = json.loads((DATA / case / "pdf_text.json").read_text())
    got = [page.extract_text() for page in PdfReader(out / "r.pdf").pages]
    assert got == expected


def test_audio_matches_the_original(rendered):
    case, out, _ = rendered
    expected = json.loads((DATA / case / "audio.json").read_text())
    with wave.open(str(out / "Registrazione_Audio.wav")) as wav:
        samples = np.frombuffer(wav.readframes(wav.getnframes()), dtype=np.int16)
        assert wav.getnchannels() == expected["channels"]
        assert wav.getsampwidth() == expected["sample_width"]
        assert wav.getframerate() == expected["rate"]
        assert wav.getnframes() == expected["frames"]
    assert int(np.abs(samples).max()) == expected["peak"]
    assert float(np.sqrt(np.mean(samples.astype(np.float64) ** 2))) == pytest.approx(
        expected["rms"], rel=1e-9
    )


def test_output_folder_contains_only_the_report_files(rendered):
    _case, out, result = rendered
    assert sorted(p.name for p in out.iterdir()) == sorted(
        [
            "r.pdf",
            "Registrazione_Audio.wav",
            *(f"pagina_{i}.png" for i in range(1, result["pages"] + 1)),
        ]
    )


def test_bad_input_fails_with_a_message(tmp_path):
    result = subprocess.run(
        [sys.executable, "-m", "bridge_tools.report.cli", "--out-dir", str(tmp_path)],
        input='{"version": 99}',
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode != 0
    assert "unsupported session data version" in result.stderr
