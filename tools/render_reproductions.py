"""Render DXFs exported by the actual AutoCAD executor, for local visual review."""
import argparse
from pathlib import Path

import ezdxf
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from ezdxf.addons.drawing import Frontend, RenderContext
from ezdxf.addons.drawing.config import BackgroundPolicy, ColorPolicy, Configuration
from ezdxf.addons.drawing.matplotlib import MatplotlibBackend

parser = argparse.ArgumentParser()
parser.add_argument("folder", type=Path, nargs="?", default=Path("artifacts/reproductions"))
args = parser.parse_args()
for path in args.folder.glob("*.dxf"):
    doc = ezdxf.readfile(path)
    # Preview-only Unicode font mapping; source DWG/DXF files keep their original CAD fonts.
    font = Path("C:/Windows/Fonts/simhei.ttf")
    if font.exists():
        for style in doc.styles:
            style.dxf.font = str(font)
            style.dxf.bigfont = ""
    fig = plt.figure(figsize=(14, 10), facecolor="white")
    ax = fig.add_axes([0.02, 0.02, 0.96, 0.96])
    config = Configuration(background_policy=BackgroundPolicy.WHITE, color_policy=ColorPolicy.BLACK)
    Frontend(RenderContext(doc), MatplotlibBackend(ax), config=config).draw_layout(doc.modelspace(), finalize=True)
    fig.savefig(path.with_suffix(".png"), dpi=150)
    plt.close(fig)
    print(f"Rendered {path.name}")
