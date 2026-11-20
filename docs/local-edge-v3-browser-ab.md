# Local Edge V3: Browser A/B trial (PR #16)

## What changed
The browser now offers **Original**, **Local Edge V3**, or **Compare A/B** in Settings. Original remains the default. A/B traces one raster twice using the same image, palette estimate, style, shape mode, detail, seed, curve/node settings and edge coverage of 0.50. Only `LocalEdgeRefinementV3` differs.

- **Original**: existing SVG output; V3 is OFF.
- **V3**: actual `Tracer.Trace` with V3 ON and a moved-point diagnostic.
- **Compare A/B**: generate two independently downloadable SVGs and show them side-by-side. Switching preview does not regenerate them.
- **Original image vs SVG** remains available under the existing *Bandingkan* view.

## What the audit measures
Metrics are based on browser canvas re-rasterization of both generated SVGs and the uploaded original, with white compositing at identical dimensions limited to a maximum side length of 1024 px. Export remains full resolution.

- *IoU mask* and *mask-error pixels*: grayscale luminance <128, source vs SVG. This is useful mainly for high-contrast, two-color logos; it should not be used alone on colorful artwork or photographs.
- *SSIM luminance 8x8*: nonoverlapping 8×8 local-window estimate, averaged across tiles. This **is not** the same as the `skimage` Gaussian SSIM employed by the eagle benchmark.
- *MAE RGB*: mean absolute RGB channel error (0–255) against the source. More general-purpose than thresholded IoU but still not a perceptual verdict.
- *SVG segments, tiny lines <2 px, optimized nodes, size, duration, moved vertices*: from the actual trace output. Both SVGs are hashed/byte-compared to detect unchanged output.

Canvas metrics are secondary diagnostics; visually inspect sharp tips, corners, shared edges and actual shape fidelity before concluding that V3 is better. No global threshold changes and no production-wide enabling of V3.

## Manual acceptance checklist
1. Upload a flat high-contrast illustration; select **Original**, process and save SVG.
2. Select **A/B**; process the same image. The Original SVG in A/B should match the independently saved Original result byte-for-byte at identical settings.
3. Check the A/B toggle, two downloads, moved-vertex count, node counts, IoU/SSIM/MAE table and independent SVG previews.
4. Repeat with stacked and planar shape mode, a sharp-corner logo, JPEG with anti-aliasing, a colorful illustration and transparent PNG.
5. Confirm file re-upload/reset revokes old preview URLs and clears all stale comparison stats.
6. If V3 moves 0 vertices (or SVG bytes are identical), display that fact instead of claiming an improvement.

## Rollback
The existing production default is Original. To disable the experimental feature, choose Original. For a full code rollback, revert the squash commit of PR #16 after merge. Pre-PR16 main is currently `e239df6ef38ab46c1273cf47411b329840476e1b`; do not force-reset main.
