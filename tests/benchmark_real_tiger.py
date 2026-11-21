#!/usr/bin/env python3
"""Actual previously-uploaded tiger: same RGBA raster -> Stacked OFF/ON -> 3-scale comparison.

The source is the user's prior SVG tiger, rasterized to a fixed 512px input.
Metrics use the *original tiger SVG* as vector reference, not a synthetic line.
CairoSVG is an independent SVG renderer; browser screenshots require separate
browser-specific inspection.
"""
import io
import copy
import json
import math
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import cairosvg
import numpy as np
from PIL import Image
from scipy.ndimage import binary_erosion, maximum_filter, minimum_filter, binary_dilation
from skimage.metrics import structural_similarity

NS = "http://www.w3.org/2000/svg"
ET.register_namespace("", NS)
N = 512
SCALES = (1, 2, 4)
ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tests/fixtures/tiger_source_user.svg"


def render_bytes(content, scale=1):
    raw = cairosvg.svg2png(bytestring=content, output_width=N * scale, output_height=N * scale)
    return np.array(Image.open(io.BytesIO(raw)).convert("RGBA"))


def render_file(path, scale=1):
    return render_bytes(Path(path).read_bytes(), scale)


def make_seam_mask(svg_path, scale):
    root = ET.parse(svg_path).getroot()
    out = ET.Element(f"{{{NS}}}svg", {
        "width": str(N), "height": str(N), "viewBox": f"0 0 {N} {N}"
    })
    for path in root.iter(f"{{{NS}}}path"):
        if path.get("data-seam") == "1":
            clone = copy.deepcopy(path)
            clone.tail = None  # strip serializer-external text between SVG paths
            clone.set("fill", "none") # Restore inherited SVG-root fill
clone.set("stroke", "#000000")
            clone.set("stroke-opacity", "1")
            out.append(clone)
    if len(out) == 0:
        return np.zeros((N * scale, N * scale), dtype=bool)
    data = ET.tostring(out, encoding="utf-8")
    return render_bytes(data, scale)[:, :, 3] > 0


def nonwhite(a):
    rgb = a[:, :, :3].astype(np.int16)
    return np.max(np.abs(rgb - 254), axis=2) > 27


def eye_roi(label, width, height):
    # Fixed 736-viewBox coordinates grounded in the previously uploaded tiger.
    boxes = {
        "left_eye": (155, 265, 325, 385),
        "right_eye": (405, 265, 585, 385),
        "nose_and_mouth": (250, 400, 495, 635),
        "whisker_left": (75, 370, 250, 610),
        "whisker_right": (485, 370, 665, 610),
    }
    x0, y0, x1, y1 = boxes[label]
    rx = width / 736
    ry = height / 736
    return (slice(int(y0 * ry), int(y1 * ry)), slice(int(x0 * rx), int(x1 * rx)))


def inspect(source_path, off_svg, on_svg, out_dir):
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    core = json.loads((out_dir / "benchmark_core.json").read_text())
    ref_bytes = Path(source_path).read_bytes()
    records = []
    max_ssim_loss = 0
    max_iou_loss = 0
    clean = True
    for scale in SCALES:
        print(f"Rendering real tiger {scale * 100}% / {N * scale}px", flush=True)
        ref = render_bytes(ref_bytes, scale)
        off = render_file(off_svg, scale)
        on = render_file(on_svg, scale)
        hh, ww = off.shape[:2]
        seam = make_seam_mask(on_svg, scale)
        # Allow one antialias pixel at a helper's boundary, no more.
        allowed = binary_dilation(seam, iterations=2)
        d = np.max(np.abs(off[:, :, :3].astype(np.int16) -
                              on[:, :, :3].astype(np.int16)), axis=2)
        changed = d > 1
        outside = int((changed & ~allowed).sum())
        seam_changed = int((changed & allowed).sum())
        ref_rgb = ref[:, :, :3].astype(np.uint8)
        off_rgb = off[:, :, :3].astype(np.uint8)
        on_rgb = on[:, :, :3].astype(np.uint8)
        off_ssim = float(structural_similarity(ref_rgb, off_rgb,
            channel_axis=2, data_range=255))
        on_ssim = float(structural_similarity(ref_rgb, on_rgb,
            channel_axis=2, data_range=255))
        ref_fg = nonwhite(ref)
        iou = lambda v: float((ref_fg & nonwhite(v)).sum() /
                          max(1, (ref_fg | nonwhite(v)).sum()))
        off_iou, on_iou = iou(off), iou(on)
        max_ssim_loss = max(max_ssim_loss, off_ssim - on_ssim)
        max_iou_loss = max(max_iou_loss, off_iou - on_iou)
        # Interior solid pixels cannot change. Use neighbour-aware flat area
        # detection to exclude legitimate antialiased edge samples.
        chmin = np.minimum.reduce([minimum_filter(ref_rgb[:, :, i],size=5) for i in range(3)])
        chmax = np.maximum.reduce([maximum_filter(ref_rgb[:, :, i],size=5) for i in range(3)])
        flat = (chmax - chmin <= 3)
        solid_interior_changes = int((changed & flat).sum())
        rois = {}
        for label in ("left_eye", "right_eye", "nose_and_mouth",
                      "whisker_left", "whisker_right"):
            roi = eye_roi(label, ww, hh)
            rois[label] = {
                "changed_pixels": int(changed[roi].sum()),
                "outside_safe_seam": int((changed & ~allowed)[roi].sum()),
                "flat_interior_changes": int((changed & flat)[roi].sum())
            }
        # Save before-after and highlighted difference for visual inspection.
        if scale == 1:
            Image.fromarray(ref).save(out_dir / "tiger_reference_input.png")
        Image.fromarray(off).save(out_dir / f"tiger_OFF_{100*scale}.png")
        Image.fromarray(on).save(out_dir / f"tiger_ON_{100*scale}.png")
        heat = np.zeros((hh, ww, 4), dtype=np.uint8)
        heat[:, :, 0] = 235
        heat[:, :, 1] = 35
        heat[:, :, 2] = 60
        heat[:, :, 3] = (changed * 225).astype(np.uint8)
        Image.fromarray(heat).save(out_dir / f"tiger_diff_{100*scale}.png")
        record = dict(
            scale_pct=scale*100, width=ww, height=hh,
            ssim_off=round(off_ssim, 8), ssim_on=round(on_ssim, 8),
            ssim_delta=round(on_ssim-off_ssim, 8),
            iou_off=round(off_iou,8), iou_on=round(on_iou,8),
            iou_delta=round(on_iou-off_iou,8),
            changed_pixels=int(changed.sum()),
            changed_in_safe_seam=seam_changed,
            changed_outside_seam=outside,
            flat_interior_changes=solid_interior_changes,
            protected_details=rois
        )
        records.append(record)
        print(json.dumps(record, ensure_ascii=False), flush=True)
        if outside > 0 or solid_interior_changes > 0:
            clean = False

    size_ok = core["increase_pct"] < 10.0
    core_paths_ok = core["main_paths_byte_identical"]
    visual_gate = max_ssim_loss <= 0.001 and max_iou_loss <= 0.002
    summary = {
        "fixture": "user_uploaded_tiger_svg_rasterized_to_512x512_rgba",
        "qualification": "not an independent PNG/RAW input or browser native AA",
        "core": core,
        "scales": records,
        "merge_gate": {
            "size_increase_below_10pct": size_ok,
            "main_paths_byte_identical": core_paths_ok,
            "no_pixel_changes_outside_safe_seam": clean,
            "no_material_ssim_or_iou_drop": visual_gate,
            "cairo_real_tiger_pass": size_ok and core_paths_ok and clean and visual_gate
        }
    }
    (out_dir / "tiger_benchmark_report.json").write_text(
        json.dumps(summary, indent=2, ensure_ascii=False) + "\n")
    return summary


def create_browser_preview(folder):
    folder = Path(folder)
    html = """<!doctype html>
<html lang="id"><meta charset="utf-8"><title>DikaTrace Tiger Seam A/B</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
body{font:15px system-ui;background:#0c1220;color:#eef2fa;margin:0;padding:20px}
header{max-width:1400px;margin:auto}h1{font-size:24px}.info{color:#b1c0d3}
.controls{margin:12px 0;display:flex;gap:18px;flex-wrap:wrap}
button{padding:8px 12px;background:#203752;color:white;border:1px solid #52729a;border-radius:7px;cursor:pointer}
.panels{display:grid;grid-template-columns:1fr 1fr;gap:18px;max-width:1400px;margin:auto}
.panel{background:#101f31;border:1px solid #334962;border-radius:9px;padding:10px}
.viewport{height:600px;overflow:auto;background:white;scrollbar-width:thin}
.viewport img{display:block;width:512px;height:512px;max-width:none}
h2{font-size:16px}a{color:#a2d9ff}pre{white-space:pre-wrap}
@media(max-width:760px){.panels{grid-template-columns:1fr}.viewport{height:360px}}
</style>
<header><h1>Ekstracev1 — Topology-Aware Seam Protection</h1>
<p class="info">Gambar harimau nyata yang sama, dua tracing Stacked Shapes. Kiri = flag OFF; kanan = flag ON. File bisa dibuka langsung di Chrome atau Firefox. Zoom disinkronkan.</p>
<div class="controls">
<label>Zoom: <strong id="zlabel">100%</strong> <input type="range" id="zoom" min="100" max="400" value="100" step="50"></label>
<button id="reset">Reset posisi</button>
<a href="tiger_stacked_OFF.svg" download>Download OFF.svg</a>
<a href="tiger_stacked_ON.svg" download>Download ON.svg</a>
<a href="tiger_benchmark_report.json">Laporan metrik</a>
</div></header>
<div class="panels"><section class="panel"><h2>OFF — Stacked original</h2><div class="viewport" id="left"><img id="img0" src="tiger_stacked_OFF.svg" alt="Original"></div></section>
<section class="panel"><h2>ON — Stacked + guarded underpaint</h2><div class="viewport" id="right"><img id="img1" src="tiger_stacked_ON.svg" alt="Seam protected"></div></section></div>
<script>
const a=document.getElementById('left'),b=document.getElementById('right'),z=document.getElementById('zoom');
function zoom(){let t=Number(z.value)/100;for(let id of ['img0','img1']){let im=document.getElementById(id);im.style.width=512*t+'px';im.style.height=512*t+'px'}document.getElementById('zlabel').textContent=z.value+'%'}
z.oninput=zoom;document.getElementById('reset').onclick=()=>{a.scrollTop=a.scrollLeft=b.scrollTop=b.scrollLeft=0};
let syncing=false;for(let [one,two] of [[a,b],[b,a]])one.addEventListener('scroll',()=>{if(syncing)return;syncing=true;two.scrollLeft=one.scrollLeft;two.scrollTop=one.scrollTop;requestAnimationFrame(()=>syncing=false)});
zoom();
</script></html>
"""
    (folder / "compare_in_browser.html").write_text(html)


if __name__ == "__main__":
    if len(sys.argv) == 2 and sys.argv[1] == "prepare":
        rgba = render_file(SOURCE, 1)
        out = ROOT / "tiger-benchmark-input"
        out.mkdir(exist_ok=True)
        (out / "tiger_rgba_512.bin").write_bytes(rgba.tobytes())
        Image.fromarray(rgba).save(out / "tiger_input_512.png")
        print(f"Prepared {N}x{N} tiger RGBA raster", flush=True)
    elif len(sys.argv) == 3 and sys.argv[1] == "report":
        folder = Path(sys.argv[2])
        report = inspect(SOURCE, folder / "tiger_stacked_OFF.svg",
                         folder / "tiger_stacked_ON.svg", folder)
        create_browser_preview(folder)
        if not report["merge_gate"]["cairo_real_tiger_pass"]:
            raise SystemExit("REAL TIGER QUALITY GATE FAILED (artifacts still uploaded)")
        print("REAL TIGER QUALITY GATE PASSED", flush=True)
    else:
        raise SystemExit("Usage: script.py prepare | report <benchmark-output>")
