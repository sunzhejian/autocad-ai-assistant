import importlib.util
import io
from pathlib import Path
import tempfile
import unittest

import fitz

spec = importlib.util.spec_from_file_location("pdf_worker", Path(__file__).parents[1] / "tools/pdf_worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class PdfWorkerTests(unittest.TestCase):
    def test_routing_pages_and_snapshot(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path = root / "测试 drawings with spaces.pdf"
            with fitz.open() as document:
                vector = document.new_page(width=144, height=144)
                vector.draw_rect(fitz.Rect(0, 0, 72, 36), color=(0, 0, 0), fill=(.8, .8, .8))
                vector.insert_text((10, 100), "unit 25.4 mm")
                vector.set_rotation(90)
                pixmap = fitz.Pixmap(fitz.csRGB, fitz.IRect(0, 0, 40, 40), False)
                pixmap.clear_with(200)
                png = pixmap.tobytes("png")
                document.new_page().insert_image(fitz.Rect(0, 0, 40, 40), stream=png)
                mixed = document.new_page()
                mixed.insert_image(fitz.Rect(0, 0, 40, 40), stream=png)
                mixed.draw_line((50, 50), (80, 80))
                document.new_page()
                document.save(path)
            results = [worker.inspect_pdf(path, root / "cache", page) for page in range(1, 5)]
            self.assertEqual([r["kind"] for r in results], ["vector", "raster", "mixed", "blank"])
            self.assertEqual(results[0]["pages"], 4)
            self.assertAlmostEqual(results[0]["width_mm"], 50.8)
            self.assertIn("25.4", results[0]["text"])
            repeated = worker.inspect_pdf(path, root / "cache", 1)
            self.assertEqual(repeated["pdf_hash"], results[0]["pdf_hash"])
            self.assertEqual(repeated["preview_hash"], results[0]["preview_hash"])
            with fitz.open(results[0]["pdf"]) as selected:
                self.assertEqual(selected.page_count, 1)
                self.assertEqual(selected[0].rotation, 90)
            snapshot = Path(results[0]["pdf"]).read_bytes()
            path.write_bytes(b"changed original")
            self.assertEqual(Path(results[0]["pdf"]).read_bytes(), snapshot)

    def test_invalid_page(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "one.pdf"
            with fitz.open() as document:
                document.new_page()
                document.save(path)
            for page in [0, -1, 2]:
                with self.assertRaisesRegex(ValueError, "Page must"):
                    worker.inspect_pdf(path, Path(folder) / "cache", page)


if __name__ == "__main__":
    unittest.main()
