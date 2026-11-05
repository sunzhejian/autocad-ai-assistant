"""Index local PDFs without copying source material to Git; render reproducible regression samples."""
import argparse
import hashlib
import json
from pathlib import Path

import fitz


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path, default=Path("artifacts/corpus"))
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    documents = []
    seen = {}
    for path in sorted(args.source.rglob("*.pdf")):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        with fitz.open(path) as pdf:
            documents.append(dict(path=str(path.relative_to(args.source)), sha256=digest,
                                  pages=len(pdf), scanned_pages=sum(not p.get_text().strip() for p in pdf),
                                  duplicate_of=seen.get(digest)))
        seen.setdefault(digest, str(path.relative_to(args.source)))
    samples = []
    for case_id, pattern, page_number in [
        ("fifth-channel", "第五届成图大赛水利类.pdf", 1),
        ("sixth-intake", "第六届先进成图大赛水利类试题.pdf", 1),
        ("seventh", "第七届成图大赛水利类.pdf", 1),
    ]:
        path = next(args.source.rglob(pattern))
        with fitz.open(path) as pdf:
            page = pdf[page_number - 1]
            image = args.output / (case_id + ".png")
            page.get_pixmap(matrix=fitz.Matrix(2, 2)).save(image)
            samples.append(dict(id=case_id, source=str(path.relative_to(args.source)), page=page_number,
                                source_sha256=hashlib.sha256(path.read_bytes()).hexdigest(), image=str(image)))
    cases_path = Path(__file__).resolve().parents[1] / "tests" / "corpus_cases.json"
    for case in json.loads(cases_path.read_text(encoding="utf-8"))["cases"]:
        if "crop" not in case:
            continue
        path = args.source / case["source"]
        with fitz.open(path) as pdf:
            page = pdf[case["page"] - 1]
            page.set_rotation(case["rotation"])
            w, h = page.rect.width, page.rect.height
            x0, y0, x1, y1 = case["crop"]
            image = args.output / (case["id"] + ".png")
            scale = 3 if case["id"] == "sixth-section33" else 2.5
            page.get_pixmap(matrix=fitz.Matrix(scale, scale), clip=fitz.Rect(x0*w, y0*h, x1*w, y1*h)).save(image)
            samples.append(dict(id=case["id"], source=case["source"], page=case["page"], crop=case["crop"],
                                rotation=case["rotation"], image=str(image), prompt=case["prompt"],
                                source_sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    result = dict(documents=documents, samples=samples,
                  note="Local regression corpus; source PDFs/answers are not uploaded and model weights are not trained.")
    (args.output / "manifest.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Indexed {len(documents)} PDFs, {sum(d['pages'] for d in documents)} pages; rendered {len(samples)} cases.")


if __name__ == "__main__":
    main()
