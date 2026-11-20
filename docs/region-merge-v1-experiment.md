# Region Merge V1 — experimental, not for default / main yet

## Problem and source evidence
The supplied tiger illustration and two SVG outputs were compared by rendering both SVGs at the 1536×1536 source resolution. The **existing** DikaTracer V3 output (not V1) had 150 `<path>` elements, 2,048 total L/C segments, 7 distinct fill colors and ~76,609 UTF-8 bytes. Vector Magic stacked had 84 paths, 1,345 segments, 6 colors, and ~63,692 bytes. DikaTracer's source-relative RGB MAE was ~5.75, compared with ~4.83 for Vector Magic. These are **baseline observations**, NOT improvements achieved by this PR. The user's source JPEG is **not committed** to a public repository.

## Scope — ONLY priority 1: source-aware tiny-region merge
Earlier `Segmenter.AbsorbSmallRegions` uses longest shared border alone. It can lose sharp colored accents. This PR adds an **additional optional** pass after fringe treatment, palette consolidation and noise cleanup, but before boundary graph extraction.

1. Derive 4-connected region adjacency from the current label map; never merge a region based on diagonal contact.
2. Consider only components of at most `RegionMergeMaxAreaV1=24` pixels whose destination is a **larger already-stable** neighboring region (at least 3× its area), sharing at least 60% of the source component's 4-neighbor border. Image boundary and transparency are included in the border denominator.
3. Compare the source pixels' squared Oklab error against the original and target palette colors. The target must not significantly worsen mean color fidelity; palette distance must remain bounded.
4. Protect high-confidence colored cores, 1–2 pixel wide deliberate strokes, transparent areas and independent large regions.
5. Apply selected relabels **simultaneously** from a frozen region map (no cascading or cycles). Cap total changed pixels to 0.1% of input.
6. Recompute palette for actually changed labels; extract shared edges normally. Existing Local Edge V3 and curve optimizers are untouched.

### Defaults and switches
- `TraceOptions.RegionMergeV1=false`: production baseline unchanged, including palette, topology, and SVG bytes.
- Browser: `Pengaturan lanjutan → Region Merge V1 (eksperimen)` checkbox. Both Original/V3 modes preserve the checkbox's value; V3 A/B compares only V3 toggling, **not** Region Merge toggling.
- If enabled, the metadata panel displays regions before/after, count removed, changed input labels, and max component area. A zero result is legitimate.

## Regression coverage
- Baseline SVG bit-identical with feature absent/OFF.
- Source-supported mislabeled 2×2 component is merged, no color change to source pixels.
- High-contrast yellow eye, real 10×1 stroke, real transparent boundary and hole are preserved.
- Same source and settings give deterministic component relabels.
- Existing palette, nested Stacked, Bézier, Local Edge V3 and Blazor WASM checks remain green.

## Decision gate for follow-up (manual test required)
Before ever enabling V1 by default, benchmark on the exact uploaded tiger JPEG with **the same** style, detail, palette and Stacked settings. Export V3 with Region Merge V1 OFF and ON; compare:
- Path/segment counts, number of moved label pixels and before/after region counts.
- Full-resolution IoU/error/MAE/SSIM against the source raster, using consistent renderer and compositing; do not confuse UI's 8×8 SSIM with the external reference.
- Whether tiger eyes, teeth, tips, moustache-like fur lines and narrow orange stripes survive intact.
- Rendered diff bounding boxes and any new holes/gaps.
- A second set of transparency, icon, text, exact flat-paletted, and photograph fixtures.

**Reject or revise** if the feature removes actual detail, worsens source error beyond benchmark tolerances, or shows no compelling benefit. A lower path count alone is not a fidelity win.

This is an experiment in a draft PR; **do not merge to main** until reviewed and benchmarked on representative real images.

## Priority 2 (same PR): topology-safe region graph

The second research step is additive to the opt-in V1 pass and remains **unmerged**. This is a conservative topology safety improvement, not a new global shape-fitting or stacking algorithm.

- Each merge must now erase only a tiny *fully enclosed* mislabeled island inside **one** stable 4-connected destination component: all exposed 4-neighbor edges must border that destination component. Touching a third region, transparency, the image frame or a different component vetoes the merge.
- A 2×2 pixel-corner scan finds 3+ region T/cross junctions and diagonal checkerboards. Regions touching those pixels are protected so the planar graph's junction successor order does not change.
- After proposed merges, connected-component count must decline by **exactly** the number of accepted islands; otherwise revert the entire pass and report a topology rejection. No partial state is kept on failure.
- When V1 is ON, `BoundaryTopologyValidator` inspects the unmodified lattice cracks before Bézier smoothing: each segment must have the exact source-region pair, every adjoining region must reuse the shared crack exactly once (with opposite direction), and every region contour must close without gaps. An invalid graph throws an experimental error rather than exporting an unchecked corrupt SVG.
- Metadata includes **topology-rejected regions**, **verified crack segments** and **verified closed contours**. Both Planar and Stacked use the same validated segmentation graph.
- Added regression fixtures for 3-color T-junctions, diagonal pinch points, four-way multi-color crossings, transparent holes, deterministic merges and full-raster planar/stacked equality on exact-palette shapes.

**Scope and limitations:** The tighter guard deliberately rejects many ambiguous fringe candidates; it may yield zero merges on the tiger. This stage protects topology and eliminates a risky class of changes; it does not promise fewer paths or improved raster fidelity. It does not detect geometric crossings introduced later by Bézier smoothing; those require separate geometric validation in a subsequent stage. The 0.1% label-change budget and all existing appearance-confidence checks remain in force.

**Merge policy remains unchanged:** PR #18 is Draft, `RegionMergeV1=false` by default, and `main` must not change. A real tiger A/B must be run before enabling anything by default.
