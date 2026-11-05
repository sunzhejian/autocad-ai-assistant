import argparse
import importlib.util
import json
from pathlib import Path

import fitz

spec = importlib.util.spec_from_file_location("pdf_worker", Path(__file__).with_name("pdf_worker.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
parser = argparse.ArgumentParser()
parser.add_argument("folder")
parser.add_argument("--source")
parser.add_argument("--page", type=int, default=3)
args = parser.parse_args()
folder = Path(args.folder)
with fitz.open() as doc:
    page = doc.new_page(width=144, height=144)
    page.draw_rect(fitz.Rect(10, 10, 82, 46), color=(0, 0, 0), fill=(.7, .7, .7))
    doc.save(folder / "inch-rectangle.pdf")
with fitz.open() as doc:
    page = doc.new_page(width=144, height=144)
    page.draw_line((10, 10), (80, 10))
    pixmap = fitz.Pixmap(fitz.csRGB, fitz.IRect(0, 0, 20, 20), False)
    pixmap.clear_with(100)
    page.insert_image(fitz.Rect(20, 30, 60, 70), stream=pixmap.tobytes("png"))
    doc.save(folder / "mixed.pdf")
for name, source, page in [("small", folder / "inch-rectangle.pdf", 1),
                           ("mixed", folder / "mixed.pdf", 1),
                           ("real", args.source or folder / "inch-rectangle.pdf", args.page if args.source else 1)]:
    result = worker.inspect_pdf(source, folder / "sources", page)
    (folder / (name + ".json")).write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
