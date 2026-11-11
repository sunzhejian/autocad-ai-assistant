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


def rectangle(points, bulges=None):
    """Require an actual axis-aligned straight rectangle, not just its bounds."""
    points = [(float(p[0]), float(p[1])) for p in points]
    if len(points) == 5 and all(near(a, b) for a, b in zip(points[0], points[-1])):
        points.pop()
    if len(points) != 4 or any(not math.isfinite(v) for p in points for v in p):
        return None
    if bulges and any(abs(float(b)) > 1e-12 for b in bulges):
        return None
    xs, ys = zip(*points)
    bounds = min(xs), min(ys), max(xs), max(ys)
    if near(bounds[0], bounds[2]) or near(bounds[1], bounds[3]):
        return None
    corners = [(bounds[0], bounds[1]), (bounds[2], bounds[1]),
               (bounds[2], bounds[3]), (bounds[0], bounds[3])]
    if not all(sum(near(p[0], c[0]) and near(p[1], c[1]) for p in points) == 1 for c in corners):
        return None
    for a, b in zip(points, points[1:] + points[:1]):
        if near(a[0], b[0]) == near(a[1], b[1]):
            return None
    return bounds


def hatch_matches_section(hatch, outer, inner):
    # With one hole, Normal and Outer preserve the same empty interior;
    # Ignore (2) fills through it. Autodesk HatchStyle definitions apply.
    if outer is None or inner is None or hatch.dxf.solid_fill or hatch.dxf.hatch_style not in (0, 1):
        return False
    loops = list(hatch.paths)
    if len(loops) != 2:
        return False
    bounds = []
    for path in loops:
        if not hasattr(path, 'vertices') or not path.is_closed:
            return False
        values = list(path.vertices)
        boundary = rectangle(values, [v[2] for v in values])
        if boundary is None:
            return False
        bounds.append(boundary)
    return all(any(all(near(a, b) for a, b in zip(actual, expected)) for actual in bounds)
               for expected in [outer, inner])


def verify(path):
    doc = ezdxf.readfile(path)
    model = doc.modelspace()
    checks = []

    def check(name, passed):
        checks.append({"criterion": name, "passed": bool(passed)})

    rectangles = []
    for entity in model.query("LWPOLYLINE"):
        points = list(entity.get_points("xy"))
        bounds = rectangle(points, [p[0] for p in entity.get_points('b')]) if entity.closed else None
        if bounds is not None:
            rectangles.append(bounds)
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
                if dim.dimtype not in (0, 1):
                    continue
                a, b = dim.dxf.get("defpoint2"), dim.dxf.get("defpoint3")
                if a is None or b is None:
                    continue
                low, high = sorted((a[axis], b[axis]))
                direction = float(dim.dxf.get("angle", 0)) % 180
                angle_ok = dim.dimtype == 1 or near(direction, 0 if axis == 0 else 90)
                measurement = math.hypot(b.x-a.x,b.y-a.y) if dim.dimtype == 1 else float(dim.get_measurement())
                displayed = measurement * dim.override().get("dimlfac", 1)
                edge = a[1-axis]
                # Opposed sides can be measured at valid mid-edge points too;
                # requiring corners would reject legitimate native dimensions.
                on_boundary = bounds[1-axis] - .01 <= edge <= bounds[3-axis] + .01
                if (angle_ok and near(low, bounds[axis]) and near(high, bounds[axis + 2])
                        and on_boundary and near(a[1 - axis], b[1 - axis]) and near(displayed, value)
                        and dim.dxf.get("text", "") in ("", "<>")):
                    matched = True
        check(label + " dimension refers to its actual endpoints and source reading", matched)
    check("exactly four native dimensions", len(dimensions) == 4)
    title = [t for t in model.query("TEXT MTEXT") if (t.dxf.text if t.dxftype() == 'TEXT' else t.plain_text()).replace(" ", "") == "3-3"]
    check("section title is retained", len(title) == 1)
    underlines = []
    centers = []
    if outer:
        cx, cy = (outer[0] + outer[2]) / 2, (outer[1] + outer[3]) / 2
        for line in model.query("LINE"):
            a, b = line.dxf.start, line.dxf.end
            if near(a.y, b.y) and a.y < outer[1] and b.y < outer[1]:
                underlines.append(line)
            vertical = near(a.x, b.x) and near(a.x, cx) and min(a.y,b.y) <= outer[1] and max(a.y,b.y) >= outer[3]
            horizontal = near(a.y, b.y) and near(a.y, cy) and min(a.x,b.x) <= outer[0] and max(a.x,b.x) >= outer[2]
            if vertical or horizontal:
                linetype = line.dxf.get("linetype", "BYLAYER")
                if linetype.upper() == "BYLAYER":
                    linetype = doc.layers.get(line.dxf.layer).dxf.linetype
                centers.append({"linetype": linetype, "effective_scale": line.dxf.ltscale * doc.header.get("$LTSCALE", 1)})
        for polyline in model.query('LWPOLYLINE'):
            points = list(polyline.get_points('xyb'))
            if (not polyline.closed and len(points) == 2 and all(abs(p[2]) <= 1e-12 for p in points)
                    and near(points[0][1], points[1][1]) and points[0][1] < outer[1]
                    and not near(points[0][0], points[1][0])):
                underlines.append(polyline)
    check("one visible source underline, without an added second line", len(underlines) == 1)
    check("two centerlines lie on the section axes", len(centers) == 2)
    check("centerlines use a center linetype", len(centers) == 2 and all("CENTER" in c["linetype"].upper() for c in centers))
    hatches = list(model.query("HATCH"))
    patterns = {h.dxf.pattern_name.upper() for h in hatches}
    check("source diagonal and particle textures are both represented", "ANSI31" in patterns and bool(patterns & {"DOTS", "AR-SAND"}))
    check("each texture preserves the actual section boundaries and empty hole", bool(hatches) and all(hatch_matches_section(h, outer, inner) for h in hatches))
    return {"case": "sixth-section33", "dxf_sha256": hashlib.sha256(Path(path).read_bytes()).hexdigest(),
            "scope": "Selected source semantics only; font shapes, all line spacing and pixel identity require visual review.",
            "source_accuracy_verified": False, "verifier_version": 4, "checks": checks, "passed": sum(c["passed"] for c in checks),
            "total": len(checks), "hatch_patterns": sorted(patterns), "centerlines": centers}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dxf", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = verify(args.dxf)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"passed": report["passed"], "total": report["total"], "source_accuracy_verified": False}))
