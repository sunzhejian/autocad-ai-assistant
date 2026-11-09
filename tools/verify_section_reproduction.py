"""Check a declared subset of the sixth-contest 3-3 section against exported CAD.

This reads the actual DXF, never edits geometry or marks pixel fidelity verified.
Requires separately installed ezdxf. Units in this case: source cm, CAD mm.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path

import ezdxf


def near(a, b):
    return abs(a - b) <= 0.01


def verify(path):
    doc = ezdxf.readfile(path)
    model = doc.modelspace()
    checks = []

    def check(name, passed):
        checks.append({"criterion": name, "passed": bool(passed)})

    rectangles = []
    for entity in model.query("LWPOLYLINE"):
        points = list(entity.get_points("xy"))
        if entity.closed and len(points) in (4, 5):
            xs, ys = zip(*points)
            rectangles.append((min(xs), min(ys), max(xs), max(ys)))
    outer = next((r for r in rectangles if near(r[2] - r[0], 10000) and near(r[3] - r[1], 11000)), None)
    check("outer rectangle is 1000 x 1100 cm", outer is not None)
    inner = None
    if outer:
        expected = (outer[0] + 1500, outer[1] + 1500, outer[2] - 1500, outer[3] - 1500)
        inner = next((r for r in rectangles if all(near(a, b) for a, b in zip(r, expected))), None)
    check("700 x 800 cm hole is centered with 150 cm walls", inner is not None)
    check("CAD model units are millimeters", doc.header.get("$INSUNITS") == 4)
    dimensions = list(model.query("DIMENSION"))
    for label, bounds, axis, value in [("outer width", outer, 0, 1000), ("outer height", outer, 1, 1100),
                                       ("inner width", inner, 0, 700), ("inner height", inner, 1, 800)]:
        matched = False
        if bounds:
            for dim in dimensions:
                a, b = dim.dxf.get("defpoint2"), dim.dxf.get("defpoint3")
                if a is None or b is None:
                    continue
                low, high = sorted((a[axis], b[axis]))
                direction = float(dim.dxf.get("angle", 0)) % 180
                angle_ok = near(direction, 0 if axis == 0 else 90)
                displayed = float(dim.get_measurement()) * dim.override().get("dimlfac", 1)
                if (angle_ok and near(low, bounds[axis]) and near(high, bounds[axis + 2])
                        and near(a[1 - axis], b[1 - axis]) and near(displayed, value)
                        and dim.dxf.get("text", "") in ("", "<>")):
                    matched = True
        check(label + " dimension refers to its actual endpoints and source reading", matched)
    check("exactly four native dimensions", len(dimensions) == 4)
    title = [t for t in model.query("TEXT") if t.dxf.text.replace(" ", "") == "3-3"]
    check("section title is retained", len(title) == 1)
    underlines = []
    centers = []
    if outer:
        cx, cy = (outer[0] + outer[2]) / 2, (outer[1] + outer[3]) / 2
        for line in model.query("LINE"):
            a, b = line.dxf.start, line.dxf.end
            if near(a.y, b.y) and a.y < outer[1] and b.y < outer[1]:
                underlines.append(line)
            if (near(a.x, b.x) and near(a.x, cx)) or (near(a.y, b.y) and near(a.y, cy)):
                linetype = line.dxf.get("linetype", "BYLAYER")
                if linetype.upper() == "BYLAYER":
                    linetype = doc.layers.get(line.dxf.layer).dxf.linetype
                centers.append({"linetype": linetype, "effective_scale": line.dxf.ltscale * doc.header.get("$LTSCALE", 1)})
    check("one visible source underline, without an added second line", len(underlines) == 1)
    check("two centerlines lie on the section axes", len(centers) == 2)
    check("centerlines use a center linetype", len(centers) == 2 and all("CENTER" in c["linetype"].upper() for c in centers))
    hatches = list(model.query("HATCH"))
    patterns = {h.dxf.pattern_name.upper() for h in hatches}
    check("source diagonal and particle textures are both represented", "ANSI31" in patterns and bool(patterns & {"DOTS", "AR-SAND"}))
    check("each texture preserves an inner loop", bool(hatches) and all(len(h.paths) == 2 for h in hatches))
    return {"case": "sixth-section33", "dxf_sha256": hashlib.sha256(Path(path).read_bytes()).hexdigest(),
            "scope": "Selected source semantics only; font shapes, all line spacing and pixel identity require visual review.",
            "source_accuracy_verified": False, "checks": checks, "passed": sum(c["passed"] for c in checks),
            "total": len(checks), "hatch_patterns": sorted(patterns), "centerlines": centers}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dxf", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = verify(args.dxf)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"passed": report["passed"], "total": report["total"], "source_accuracy_verified": False}))
