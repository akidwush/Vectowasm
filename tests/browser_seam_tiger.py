#!/usr/bin/env python3
"""Actual browser engines, not just CairoSVG: chromium and firefox OFF/ON at 1/2/4x."""
import asyncio
import json
import shutil
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from playwright.async_api import async_playwright
from scipy.ndimage import binary_dilation, maximum_filter, minimum_filter
from skimage.metrics import structural_similarity

sys.path.insert(0, str(Path(__file__).resolve().parent))
from benchmark_real_tiger import N, SOURCE, eye_roi, make_seam_mask, nonwhite

SCALES = (1, 2, 4)


def analyze(ref, off, on, seam_mask, scale):
    ref_rgb = ref[:, :, :3].astype(np.uint8)
    off_rgb = off[:, :, :3].astype(np.uint8)
    on_rgb = on[:, :, :3].astype(np.uint8)
    changed = np.max(np.abs(on_rgb.astype(np.int16)-off_rgb.astype(np.int16)),axis=2) > 1
    legal = binary_dilation(seam_mask, iterations=3)
    outside = int((changed & ~legal).sum())
    chmin = np.minimum.reduce([
        minimum_filter(ref_rgb[:, :, i], size=5) for i in range(3)])
    chmax = np.maximum.reduce([
        maximum_filter(ref_rgb[:, :, i], size=5) for i in range(3)])
    flat = chmax - chmin <= 3
    flat_changes = int((changed & flat).sum())
    ssim = lambda rgb: float(structural_similarity(ref_rgb, rgb, channel_axis=2,data_range=255))
    fg = nonwhite(ref)
    iou = lambda v: float((fg & nonwhite(v)).sum() / max(1,(fg | nonwhite(v)).sum()))
    roi = {name:int((changed & ~legal)[eye_roi(name, N*scale, N*scale)].sum())
        for name in ("left_eye","right_eye","nose_and_mouth","whisker_left","whisker_right")}
    return {
        "scale_pct":100*scale,
        "ssim_off":ssim(off_rgb),"ssim_on":ssim(on_rgb),
        "iou_off":iou(off),"iou_on":iou(on),
        "changed_pixels":int(changed.sum()),
        "changed_outside_safe_area":outside,
        "flat_interior_changes":flat_changes,
        "outside_safe_area_by_detail_roi":roi
    }


async def screenshot(browser, output, kind, scale, folder):
    size=N*scale
    page=await browser.new_page(viewport={"width":size,"height":size}, device_scale_factor=1)
    html=folder / f"_temporary_{kind}_{scale}.html"
    html.write_text(f"""<!doctype html><html><meta charset='utf-8'>
<style>html,body{{margin:0;padding:0;overflow:hidden;background:white}}
img{{width:{size}px;height:{size}px;display:block;max-width:none}}</style>
<img src='{kind}.svg' alt='{kind}'/></html>""")
    await page.goto(html.resolve().as_uri())
    await page.wait_for_function("document.images.length===1 && document.images[0].complete && document.images[0].naturalWidth>0")
    result=output / f"{kind}_{100*scale}.png"
    await page.screenshot(path=str(result), animations="disabled")
    await page.close()
    html.unlink(missing_ok=True)
    return np.array(Image.open(result).convert("RGBA"))


async def main():
    folder=Path(sys.argv[1] if len(sys.argv)>1 else "tiger-benchmark-output").resolve()
    shutil.copy(SOURCE,folder/"tiger_reference_original.svg")
    (folder/"reference.svg").write_bytes((folder/"tiger_reference_original.svg").read_bytes())
    (folder/"off.svg").write_bytes((folder/"tiger_stacked_OFF.svg").read_bytes())
    (folder/"on.svg").write_bytes((folder/"tiger_stacked_ON.svg").read_bytes())
    rows=[]
    async with async_playwright() as p:
        for name, browser_type in (("chromium",p.chromium),("firefox",p.firefox)):
            print(f"Launching {name} for actual SVG browser raster...",flush=True)
            browser=await browser_type.launch(headless=True)
            out=folder / f"browser_{name}"
            out.mkdir(exist_ok=True)
            for scale in SCALES:
                ref=await screenshot(browser,out,"reference",scale,folder)
                off=await screenshot(browser,out,"off",scale,folder)
                on=await screenshot(browser,out,"on",scale,folder)
                allowed=make_seam_mask(folder/"tiger_stacked_ON.svg",scale)
                row=analyze(ref,off,on,allowed,scale)
                row["browser"]=name
                rows.append(row)
                print(json.dumps(row),flush=True)
            await browser.close()
    report={"screenshots":rows,"passes":all(
        r["changed_outside_safe_area"]==0 and
        r["flat_interior_changes"]==0 and
        r["ssim_on"]>=r["ssim_off"]-.001 and
        r["iou_on"]>=r["iou_off"]-.002
        for r in rows)}
    (folder/"tiger_browser_report.json").write_text(json.dumps(report,indent=2)+"\n")
    if not report["passes"]:
        raise SystemExit("CHROMIUM/FIREFOX QUALITY GATE FAILED; inspect screenshot artifact")
    print("CHROMIUM + FIREFOX 100/200/400% QUALITY GATE PASSED",flush=True)


if __name__=="__main__":
    asyncio.run(main())
