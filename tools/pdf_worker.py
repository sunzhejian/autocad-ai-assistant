"""Local PDF inspection and preview; no document data is sent over the network.

Requires a separately installed PyMuPDF. Third-party binaries are not bundled.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import uuid


def inspect_pdf(source, cache, page_number):
    import fitz

    source = Path(source).resolve(strict=True)
    if source.stat().st_size > 128 * 1024 * 1024:
        raise ValueError("PDF exceeds the 128 MB limit")
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    folder = Path(cache).resolve() / digest
    folder.mkdir(parents=True, exist_ok=True)
    # Analyze a content-addressed snapshot, so edits to the original file cannot
    # change the geometry between preview and native import.
    snapshot = folder / "source.pdf"
    if not snapshot.exists() or hashlib.sha256(snapshot.read_bytes()).hexdigest() != digest:
        with tempfile.NamedTemporaryFile(dir=folder, delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(source.read_bytes())
        if hashlib.sha256(temporary.read_bytes()).hexdigest() != digest:
            temporary.unlink()
            raise ValueError("PDF changed while being copied; attach it again")
        os.replace(temporary, snapshot)
    with fitz.open(snapshot) as document:
        if not document.is_pdf:
            raise ValueError("Input is not a PDF document")
        if document.needs_pass:
            raise ValueError("Password-protected PDF is not supported")
        if not 1 <= page_number <= document.page_count:
            raise ValueError(f"Page must be between 1 and {document.page_count}")
        page = document[page_number - 1]
        prefix = folder / f"page-{page_number}"
        metadata = prefix.with_suffix(".json")
        if metadata.exists():
            try:
                saved = json.loads(metadata.read_text(encoding="utf-8"))
                if (saved.get("schema") == 2 and saved.get("source_hash") == digest
                        and saved.get("page") == page_number
                        and hashlib.sha256(Path(saved["pdf"]).read_bytes()).hexdigest() == saved["pdf_hash"]
                        and hashlib.sha256(Path(saved["preview"]).read_bytes()).hexdigest() == saved["preview_hash"]):
                    return saved
            except (OSError, ValueError, KeyError):
                pass
        width, height = page.rect.width, page.rect.height
        if min(width, height) <= 0 or max(width, height) > 100000:
            raise ValueError("Invalid or oversized PDF page")
        paths = len(page.get_drawings())
        images = len(page.get_images(full=True))
        text = page.get_text("text")
        kind = ("mixed" if images else "vector") if paths or text.strip() else ("raster" if images else "blank")
        preview = prefix.with_suffix(".png")
        single_page = prefix.with_suffix(".pdf")
        scale = min(2400 / max(width, height), 3)
        temporary_preview = folder / (prefix.name + "-" + uuid.uuid4().hex + ".png")
        page.get_pixmap(matrix=fitz.Matrix(scale, scale), alpha=False).save(temporary_preview)
        os.replace(temporary_preview, preview)
        with fitz.open() as selected:
            selected.insert_pdf(document, from_page=page_number - 1, to_page=page_number - 1)
            temporary = folder / (prefix.name + "-" + uuid.uuid4().hex + ".tmp.pdf")
            selected.save(temporary, garbage=4, deflate=True)
        os.replace(temporary, single_page)
        result = dict(schema=2, source_hash=digest, page=page_number,
                      pages=document.page_count, kind=kind, paths=paths, images=images,
                      text_chars=len(text), text=text[:12000], width_mm=width * 25.4 / 72,
                      height_mm=height * 25.4 / 72, preview=str(preview), pdf=str(single_page),
                      pdf_hash=hashlib.sha256(single_page.read_bytes()).hexdigest(),
                      preview_hash=hashlib.sha256(preview.read_bytes()).hexdigest())
        temporary_metadata = folder / (prefix.name + "-" + uuid.uuid4().hex + ".json")
        temporary_metadata.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
        os.replace(temporary_metadata, metadata)
        return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("cache")
    parser.add_argument("page", type=int)
    args = parser.parse_args()
    try:
        result = inspect_pdf(args.source, args.cache, args.page)
    except ImportError:
        result = dict(error="PDF support requires Python and PyMuPDF: python -m pip install pymupdf")
    except Exception as error:
        result = dict(error=str(error))
    sys.stdout.buffer.write(json.dumps(result, ensure_ascii=False).encode("utf-8"))
    sys.exit(1 if "error" in result else 0)
