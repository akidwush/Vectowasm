#!/usr/bin/env python3
"""Ground-truth audit of experiment/edge-sampling sweep at source resolution.

This uses the SAME historical metric definition (CairoSVG, JPEG grayscale
threshold <128, white composite) as the previous eagle 4-mode report.
All outputs are experimental and NOT production code.
"""
import argparse
import csv
import io
import json
import pathlib
import sys

import cairosvg
import numpy as np
from PIL import Image, ImageDraw
from scipy.ndimage import binary_erosion, distance_transform_edt
from skimage.metrics import structural_similarity


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--source", required=True)
    p.add_argument("--svgs", required=True)
    p.add_argument("--out", required=True)
    args = p.parse_args()
    root = pathlib.Path(args.svgs)
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    src = np.asarray(Image.open(args.source).convert("L"))
    h, w = src.shape
    ref = src < 128
    src_pixels = int(np.count_nonzero(ref))
    ref_edge = ref & ~binary_erosion(ref)
    distance_to_ref = distance_transform_edt(~ref_edge)

    rows = []
    masks = {}
    errors = {}
    for path in sorted(root.glob("*.svg")):
        png = cairosvg.svg2png(url=str(path),
            output_width=w, output_height=h)
        rgba = Image.open(io.BytesIO(png)).convert("RGBA")
        rgba = Image.alpha_composite(
            Image.new("RGBA", (w, h), (255, 255, 255, 255)),
            rgba)
        gray = np.asarray(rgba.convert("L"))
        mask = gray < 128
        masks[path.stem] = mask

        intersection = np.count_nonzero(ref & mask)
        union = np.count_nonzero(ref | mask)
        missed = int(np.count_nonzero(ref & ~mask))
        added = int(np.count_nonzero(~ref & mask))
        edge = mask & ~binary_erosion(mask)
        to_output = distance_transform_edt(~edge)
        distances = np.concatenate([to_output[ref_edge], distance_to_ref[edge]])
        row = {
            "variant": path.stem,
            "iou": float(intersection / union) if union else 1.0,
            "ssim": float(structural_similarity(src, gray, data_range=255)),
            "mismatch": missed + added,
            "missed": missed,
            "added": added,
            "p99_px": float(np.percentile(distances, 99)) if len(distances) else None,
            "max_boundary_px": float(distances.max()) if len(distances) else None,
            "mae": float(np.abs(gray.astype(np.int16) -
                src.astype(np.int16)).mean()),
            "svg_bytes": path.stat().st_size,
        }
        errors[path.stem] = (missed, added)
        rows.append(row)
        print("{variant:29s} IoU={iou:.6f} SSIM={ssim:.6f} "
              "wrong={mismatch:5d} P99={p99_px:.2f} max={max_boundary_px:.2f}".format(**row), flush=True)

    baseline = next(r for r in rows if r["variant"] == "baseline_planar_off")
    bests = {}
    for shape in ("planar", "stacked"):
        variants = [r for r in rows if r["variant"].startswith(shape+"_")]
        variants.sort(key=lambda r: (-r["iou"], r["mismatch"]))
        best = variants[0]
        canonical = next(r for r in variants if r["variant"] ==
            shape + "_prepared_t500")
        bests[shape] = {
            "best": best["variant"],
            "best_iou": best["iou"],
            "baseline": canonical["variant"],
            "baseline_iou": canonical["iou"],
            "best_delta_pp": (best["iou"] - canonical["iou"]) * 100,
            "best_delta_wrong": best["mismatch"] - canonical["mismatch"],
            "best_max_edge_px": best["max_boundary_px"],
            "baseline_max_edge_px": canonical["max_boundary_px"],
        }
    assert abs(baseline["iou"] - next(r for r in rows
        if r["variant"] == "planar_prepared_t500")["iou"]) < 1e-12, (
            "Critical: explicit default changed source-relative fidelity!")

    # A single benchmarked photograph cannot establish a general default.
    report = {
        "source": pathlib.Path(args.source).name,
        "width": w, "height": h, "source_foreground": src_pixels,
        "metric": "CairoSVG 1536x1536, white composite, PIL convert(L), mask<128, "
                  "4-connected inward edge+euclidean EDT, skimage SSIM",
        "baseline_planar_iou": baseline["iou"],
        "best_by_shape": bests, "results": rows,
        "disclaimer": "This is a single-input exploratory parameter sweep; "
                      "requires validation on other logos before a production default change."
    }
    (out / "edge_sweep_groundtruth.json").write_text(
        json.dumps(report, indent=2), encoding="utf-8")
    with (out / "edge_sweep_groundtruth.csv").open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)

    # Visual diff at the original pixels; keep the full image for small tips.
    for shape in ("planar", "stacked"):
        if shape == "planar":
            baseline_name = "planar_prepared_t500"
        else:
            baseline_name = "stacked_prepared_t500"
        best_name = bests[shape]["best"]
        old, new = masks[baseline_name], masks[best_name]
        img = np.full((h, w, 3), 255, np.uint8)
        img[ref] = (35, 35, 35)
        img[ref & ~new] = (0, 166, 222)
        img[~ref & new] = (235, 50, 55)
        # Earlier-wrong corrected vs newly wrong.
        fixed = (old ^ ref) & ~(new ^ ref)
        regress = (new ^ ref) & ~(old ^ ref)
        img[fixed] = (20, 150, 105)
        img[regress] = (237, 172, 33)
        output = Image.fromarray(img)
        output.save(out / (shape + "_best_difference.png"))
        print("BEST", shape, json.dumps(bests[shape]), flush=True)

    print("PASS edge-ground-truth audit, SVGs=", len(rows), flush=True)


if __name__ == "__main__":
    main()
