#!/usr/bin/env python3
"""Raster regression: no external images; all SVGs are emitted by the real C# SvgWriter.

CairoSVG gives a reproducible antialiased renderer; browser-specific AA still
needs separate manual validation on representative real artwork.
"""
import io
import math
import sys
from pathlib import Path

import cairosvg
from PIL import Image


def render(path, scale):
    png = cairosvg.svg2png(
        bytestring=Path(path).read_bytes(),
        output_width=128 * scale,
        output_height=96 * scale,
    )
    return Image.open(io.BytesIO(png)).convert("RGBA")


def rgb_error(pixel, expected):
    return sum((pixel[i] - expected[i]) ** 2 for i in range(3))


def test_reference(folder, scale):
    before = render(folder / "seam_reference_baseline.svg", scale)
    after = render(folder / "seam_reference_protected.svg", scale)
    back = (220, 60, 49)
    front = (32, 108, 185)
    edge_x = 62.35
    error_before = 0.0
    error_after = 0.0
    changed = 0
    outside_changes = 0
    for y in range(before.height):
        for x in range(before.width):
            b = before.getpixel((x, y))
            a = after.getpixel((x, y))
            if a != b:
                changed += 1
                # A local seam fill must never affect outer silhouette,
                # background or 1px black detail at x=40.
                if abs((x + .5) / scale - edge_x) > 1.0 or not (
                    14 <= (y + .5) / scale <= 82
                ):
                    outside_changes += 1
            if 25 * scale <= y < 70 * scale and abs((x + .5) / scale - edge_x) < 1.1:
                # Exact ideal subpixel colour for a straight, opaque red/blue
                # shared boundary, without a white background leaking through.
                front_area = max(0.0, min(1.0, x + 1 - edge_x * scale))
                expected = tuple(back[i] * (1 - front_area) + front[i] * front_area for i in range(3))
                error_before += rgb_error(b, expected)
                error_after += rgb_error(a, expected)

    assert changed > 0, f"{scale}x: fixture underpaint does not change any seam pixels"
    assert outside_changes == 0, (
        f"{scale}x: {outside_changes} pixels changed outside internal seam guard"
    )
    assert error_before > 0 and error_after < error_before, (
        f"{scale}x: seam white-leak error not improved: {error_before} -> {error_after}"
    )
    print(
        f"PASS {100 * scale}% reference: changed={changed} "
        f"outside_changes={outside_changes} "
        f"ideal_rgb_sse={error_before:.1f}->{error_after:.1f} "
        f"relative={error_after / error_before:.4f}"
    )


def test_traced(folder, scale):
    before = render(folder / "seam_traced_baseline.svg", scale)
    after = render(folder / "seam_traced_protected.svg", scale)
    other_changes = 0
    changes = 0
    for y in range(before.height):
        for x in range(before.width):
            if before.getpixel((x, y)) == after.getpixel((x, y)):
                continue
            changes += 1
            # The only legal geometric underpaint in this raster fixture lies
            # on the long RED/BLUE internal boundary, NOT its outer silhouette.
            xx = (x + .5) / scale
            yy = (y + .5) / scale
            if not (60 <= xx <= 64 and 18 <= yy <= 78):
                other_changes += 1
    assert other_changes == 0, (
        f"{scale}x: traced SVG changes {other_changes} pixels outside the safe shared edge"
    )
    print(f"PASS {100 * scale}% end-to-end: {changes} seam-band pixel changes, "
          "0 silhouette/background/detail changes")


if __name__ == "__main__":
    folder = Path(sys.argv[1] if len(sys.argv) > 1 else "seam-artifacts")
    for scale in (1, 2, 4):
        test_reference(folder, scale)
        test_traced(folder, scale)
    print("All 100%, 200%, 400% raster seam tests passed.")
