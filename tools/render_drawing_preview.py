"""Render existing native AutoCAD PDFs; this does not redraw the AI's entities."""
from pathlib import Path
import fitz
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
CASES = [("concrete", "Concrete section: material, hollow opening and anchored annotation"),
         ("angular", "Native 45 and 90 degree dimensions"),
         ("rings", "Wide circular ring and filled disk"),
         ("hatch", "Diagonal and particle textures with an inner hole")]

def main():
    target = ROOT / "output/pdf"
    target.mkdir(parents=True, exist_ok=True)
    combined = fitz.open()
    for name, title in CASES:
        source = ROOT / "artifacts/fidelity" / ("suite-preview-" + name + ".pdf")
        with fitz.open(source) as drawing:
            if len(drawing) != 1:
                raise ValueError("Expected one native CAD plot: " + str(source))
            combined.insert_pdf(drawing)
        page = combined[-1]
        page.remove_rotation()
        page.insert_text((35, 24), title, fontsize=12, color=(.1, .2, .3))
        page.insert_text((35, page.rect.height-15), "auto original plan / native AutoCAD output / fixed test case", fontsize=8)
    pdf = target / "AutoCAD-AI-drawing-tests.pdf"
    combined.save(pdf)
    tiles = []
    for index, page in enumerate(combined):
        pix = page.get_pixmap(matrix=fitz.Matrix(1.2,1.2), alpha=False)
        img = Image.frombytes("RGB", [pix.width,pix.height], pix.samples)
        img.thumbnail((1000,750))
        tile = Image.new("RGB",(1024,774),"#dde4eb")
        tile.paste(img,((1024-img.width)//2,(774-img.height)//2))
        tiles.append(tile)
    sheet = Image.new("RGB",(2048,1548),"white")
    for index, tile in enumerate(tiles):
        sheet.paste(tile,((index%2)*1024,(index//2)*774))
    sheet.save(target/"AutoCAD-AI-drawing-tests.png")
    combined.close()
    print("NATIVE_PREVIEW="+str(pdf))
    print("PAGES=4")

if __name__ == "__main__":
    main()
