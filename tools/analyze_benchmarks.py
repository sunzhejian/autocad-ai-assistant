"""Produce a bounded evidence summary: native dimensions and selected source readings, not a general accuracy score."""
import json
from collections import Counter
from pathlib import Path

root = Path(__file__).resolve().parents[1]
bench = root / "artifacts/benchmarks"
cases = json.loads((root / "tests/corpus_cases.json").read_text(encoding="utf-8"))["cases"]
mapping = {"fifth-channel": "candidate-fast-fifth", "sixth-section33": "candidate-source-sixth33", "seventh-detail": "candidate-seventh-detail"}
rows = []
for case in cases:
    if case["id"] not in mapping:
        continue
    response = json.loads((bench / (mapping[case["id"]] + ".json")).read_text(encoding="utf-8"))
    plan = json.loads(response["plan"])
    native = json.loads((root / "artifacts/reproductions" / (case["id"] + ".native.json")).read_text(encoding="utf-8"))
    reproduction = next(r for r in native if r.get("test") == "reproduction")
    dimensions = reproduction["dimensions"]
    measurements = Counter(round(d["measurement"], 3) for d in dimensions)
    expected = Counter(case.get("dimensions", []))
    matched = sum((measurements & expected).values())
    rows.append(dict(id=case["id"], model_seconds=response["seconds"], commands=len(plan["commands"]),
                     native_entities=reproduction["entities"], sourceUnit=plan.get("sourceUnit"),
                     native_dimensions=len(dimensions), selected_readings_checked=sum(expected.values()),
                     selected_readings_matched=matched,
                     estimated_dimensions=sum("估" in d["text"] for d in dimensions), note=case["note"]))
baseline = json.loads((bench / "baseline-fifth.json").read_text(encoding="utf-8"))
cache = json.loads((bench / "cache-sixth33.json").read_text(encoding="utf-8"))
native_tests = json.loads((root / "artifacts/native/report.json").read_text(encoding="utf-8"))
summary = dict(cases=rows, baseline_fifth_seconds=baseline["seconds"],
               baseline_fifth_commands=len(json.loads(baseline["plan"])["commands"]),
               cache_median_lookup_ms=cache["median_lookup_ms"], cache_samples=cache["samples"],
               native_tests_passed=sum(r.get("passed", False) for r in native_tests),
               native_400_commands_ms=next(r["milliseconds"] for r in native_tests if r.get("test") == "400 command execution"),
               limitations=["单次/少量模型样本，不能推出总体准确率或首轮整图提速。", "第七届中部竖向小字及尺寸链仍需复核。", "全图含小字、复杂扭面及材质纹理存在遗漏和简化。", "缓存复用已执行计划，不训练模型权重，也不把识图误差自动判为正确。"])
output = root / "docs/benchmark-summary-2026-11-05.json"
output.parent.mkdir(exist_ok=True)
output.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(summary, ensure_ascii=False, indent=2))
