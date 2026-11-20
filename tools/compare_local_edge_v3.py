#!/usr/bin/env python3
"""Local edge V3 audit versus unchanged same-source, same-shape 50% baseline."""
import argparse
import csv
import json
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument("--metrics", required=True)
p.add_argument("--traces", required=True)
p.add_argument("--out", required=True)
args = p.parse_args()
metrics = {r["variant"]: r for r in csv.DictReader(open(args.metrics))}
traces = {r["Variant"]: r for r in csv.DictReader(open(args.traces))}
report = {}
for shape in ("planar", "stacked"):
    baseline = metrics[f"{shape}_prepared_t500"]
    base_trace = traces[f"{shape}_prepared_t500"]
    entries = []
    for name, row in metrics.items():
        if not name.startswith(f"{shape}_") or "_v3_" not in name:
            continue
        t = traces[name]
        baseline_same_source = metrics[f"{shape}_{'raw' if '_raw_' in name else 'prepared'}_t500"]
        btrace_same_source = traces[f"{shape}_{'raw' if '_raw_' in name else 'prepared'}_t500"]
        result = {
            "variant": name,
            "iou": float(row["iou"]),
            "delta_iou_pp_same_source": 100 * (float(row["iou"]) - float(baseline_same_source["iou"])),
            "delta_wrong_same_source": int(row["mismatch"]) - int(baseline_same_source["mismatch"]),
            "delta_ssim_same_source": float(row["ssim"]) - float(baseline_same_source["ssim"]),
            "max_boundary_px": float(row["max_boundary_px"]),
            "baseline_max_boundary_px": float(baseline_same_source["max_boundary_px"]),
            "p99_px": float(row["p99_px"]),
            "baseline_p99_px": float(baseline_same_source["p99_px"]),
            "missed": int(row["missed"]),
            "added": int(row["added"]),
            "delta_missed_same_source": int(row["missed"]) - int(baseline_same_source["missed"]),
            "delta_added_same_source": int(row["added"]) - int(baseline_same_source["added"]),
            "net_ink_bias": int(row["added"]) - int(row["missed"]),
            "total_segments": int(t["Total"]),
            "delta_segments_same_source": int(t["Total"]) - int(btrace_same_source["Total"]),
            "microL": int(t["MicroL"]),
            "delta_microL_same_source": int(t["MicroL"]) - int(btrace_same_source["MicroL"]),
            "moved_vertices": int(t["LocalV3MovedPoints"]),
            "trace_ms": float(t["TimeMs"]),
        }
        entries.append(result)
    entries.sort(key=lambda e: (-e["delta_iou_pp_same_source"], e["delta_wrong_same_source"]))
    report[shape] = {
        "baseline_prepared": {
            "iou": float(baseline["iou"]),
            "ssim": float(baseline["ssim"]),
            "wrong": int(baseline["mismatch"]),
            "segments": int(base_trace["Total"]),
            "microL": int(base_trace["MicroL"]),
        },
        "candidates": entries,
        "best_iou_delta": entries[0] if entries else None,
        "quality_safe_candidates": [
            e for e in entries
            if e["delta_iou_pp_same_source"] > 0
            and e["delta_ssim_same_source"] >= -0.0002
            and e["max_boundary_px"] <= e["baseline_max_boundary_px"] + 0.1
            and e["delta_segments_same_source"] <= 10
            and e["delta_microL_same_source"] <= 2
        ],
    }
    print(f"{shape} baseline {report[shape]['baseline_prepared']}")
    for e in entries:
        print(f"  {e['variant']}: ΔIoU {e['delta_iou_pp_same_source']:+.5f}pp "
              f"Δwrong {e['delta_wrong_same_source']:+d} "
              f"ΔSSIM {e['delta_ssim_same_source']:+.6f}, "
              f"Δseg {e['delta_segments_same_source']:+d}, "
              f"ΔmicroL {e['delta_microL_same_source']:+d}, "
              f"Δadded {e['delta_added_same_source']:+d}, "
              f"Δmissed {e['delta_missed_same_source']:+d}, "
              f"P99 {e['p99_px']:.2f}, max {e['max_boundary_px']:.2f}, "
              f"moved {e['moved_vertices']}")
assert any(report[x]["candidates"] for x in report), "No V3 candidates were measured"
Path(args.out).write_text(json.dumps(report, indent=2))
