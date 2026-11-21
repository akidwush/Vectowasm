#!/usr/bin/env python3
"""Real native-raster photograph benchmark; NOT rasterized from any SVG.

Source: scikit-image's bundled astronaut photograph. The source remains PNG
throughout preparation, and the exact same RGBA buffer goes into both tracings.
"""
import io
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import cairosvg
import numpy as np
from PIL import Image
from scipy.ndimage import binary_dilation, maximum_filter, minimum_filter
from skimage.data import astronaut
from skimage.metrics import structural_similarity

N = 256
SCALES = (1, 2, 4)
IN = Path("real-png-benchmark-input")
OUT = Path("real-png-benchmark-output")
NS = "http://www.w3.org/2000/svg"
ET.register_namespace("", NS)


def prepare():
    IN.mkdir(exist_ok=True)
    img = Image.fromarray(astronaut()).convert("RGB").resize(
        (N, N), Image.Resampling.LANCZOS).convert("RGBA")
    img.save(IN / "astronaut_original_256.png")
    (IN / "astronaut_rgba_256.bin").write_bytes(img.tobytes())
    print("Prepared independent original photographic PNG: astronaut 256x256")


def raster(path, scale):
    png = cairosvg.svg2png(url=str(path), output_width=N * scale,
                           output_height=N * scale)
    return np.array(Image.open(io.BytesIO(png)).convert("RGBA"))


def seam_mask(path, scale):
    tree = ET.parse(path)
    root = ET.Element(f"{{{NS}}}svg",
                      {"width": str(N), "height": str(N),
                       "viewBox": f"0 0 {N} {N}"})
    for entry in tree.getroot().iter(f"{{{NS}}}path"):
        if entry.get("data-seam") != "1":
            continue
        entry.set("fill", "none") # Restore inherited SVG-root fill
entry.set("stroke", "#000000")
        entry.set("stroke-opacity", "1")
        root.append(entry)
    if not len(root):
        return np.zeros((N * scale, N * scale), dtype=bool)
    png = cairosvg.svg2png(bytestring=ET.tostring(root),
                           output_width=N * scale, output_height=N * scale)
    return np.array(Image.open(io.BytesIO(png)).convert("RGBA"))[:, :, 3] > 0


def report():
    OUT.mkdir(exist_ok=True)
    core = json.loads((OUT / "benchmark_core.json").read_text())
    original = Image.open(IN / "astronaut_original_256.png").convert("RGBA")
    records = []
    for scale in SCALES:
        ref = np.array(original.resize((N * scale, N * scale),
                                       Image.Resampling.BICUBIC))
        off = raster(OUT / "photo_stacked_OFF.svg", scale)
        on = raster(OUT / "photo_stacked_ON.svg", scale)
        r, a, b = ref[:, :, :3], off[:, :, :3], on[:, :, :3]
        changed = np.max(np.abs(b.astype(np.int16) - a.astype(np.int16)),
                         axis=2) > 1
        allowed = binary_dilation(seam_mask(OUT / "photo_stacked_ON.svg", scale),
                                  iterations=3)
        minimum = np.minimum.reduce([minimum_filter(r[:, :, i], size=5)
                                     for i in range(3)])
        maximum = np.maximum.reduce([maximum_filter(r[:, :, i], size=5)
                                     for i in range(3)])
        flat = (maximum.astype(np.int16) - minimum.astype(np.int16)) <= 3
        ssim = lambda rgb: float(structural_similarity(
            r, rgb, channel_axis=2, data_range=255))
        dark = r.mean(axis=2) < 180
        iou = lambda rgb: float((dark & (rgb.mean(axis=2) < 180)).sum() /
            max(1, (dark | (rgb.mean(axis=2) < 180)).sum()))
        item = {
            "scale_pct": 100 * scale,
            "ssim_off": round(ssim(a), 8), "ssim_on": round(ssim(b), 8),
            "iou_off": round(iou(a), 8), "iou_on": round(iou(b), 8),
            "changed_pixels": int(changed.sum()),
            "changed_outside_seam": int((changed & ~allowed).sum()),
            "flat_interior_changes": int((changed & flat).sum()),
        }
        records.append(item)
        Image.fromarray(off).save(OUT / f"photo_OFF_{100*scale}.png")
        Image.fromarray(on).save(OUT / f"photo_ON_{100*scale}.png")
        print(json.dumps(item), flush=True)
    passed = core["main_paths_byte_identical"] and core["source_underpaint_curves"] > 0 and all(
        x["changed_outside_seam"] == 0 and
        x["flat_interior_changes"] == 0 and
        x["ssim_on"] >= x["ssim_off"] - 0.001 and
        x["iou_on"] >= x["iou_off"] - 0.002 for x in records)
    summary = {"source": "skimage.data.astronaut original photo PNG",
               "native_raster": True, "core": core, "scales": records,
               "quality_gate": bool(passed)}
    (OUT / "photo_png_report.json").write_text(json.dumps(summary, indent=2) + "\n")
    if not passed:
        raise SystemExit("REAL NATIVE-RASTER PNG QUALITY GATE FAILED")
    print("REAL NATIVE-RASTER PNG QUALITY GATE PASSED")


if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] not in ("prepare", "report"):
        raise SystemExit("Usage: benchmark_real_png.py prepare|report")
    prepare() if sys.argv[1] == "prepare" else report()
