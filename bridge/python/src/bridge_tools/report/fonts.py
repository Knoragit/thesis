"""The report font: Raleway, the one the Unity app uses, shipped with this package."""

from __future__ import annotations

from importlib import resources

import matplotlib.pyplot as plt
from matplotlib import font_manager

# Raleway has no arrow glyph (used in the evaluation text), so DejaVu Sans stays as fallback.
_FALLBACK = "DejaVu Sans"


def use_raleway() -> None:
    fonts = resources.files("bridge_tools.report") / "assets" / "fonts"
    regular, bold = fonts / "Raleway-Regular.ttf", fonts / "Raleway-Bold.ttf"
    with resources.as_file(regular) as regular_path, resources.as_file(bold) as bold_path:
        font_manager.fontManager.addfont(str(regular_path))
        font_manager.fontManager.addfont(str(bold_path))
        family = font_manager.FontProperties(fname=str(regular_path)).get_name()
    plt.rcParams["font.family"] = [family, _FALLBACK]
