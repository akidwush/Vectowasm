# Adaptive Minimum Palette — Vecto WASM

This feature selects a compact starting palette automatically *before* calling the unchanged original C# tracer.

- Source: `Vecto.Core/AutoPaletteEstimator.cs` (new file; the original 17 algorithm source files remain untouched).
- Browser: Auto Minimum Colors is the default. Manual MaxColors and FixedCount remain available, as do the original desktop and High/24 presets.
- Algorithm: a deterministic 4-bit/channel RGB histogram, an interior-pixel evidence gate, Oklab perceptual separation, small saturated accent protection, an MDL-inspired residual/parameter-cost test, and at most one spare palette slot for complex edges.
- This is an **independent, heuristic approximation inspired by** Inkvec's MDL ideas, Kittl's dominant-color auto count, and interior-pixel sampling. It is **not** a port of Inkvec and makes no theoretical optimality guarantee.
- Selection estimates ink count, then supplies an upper bound to Vecto's existing PaletteBuilder via TraceOptions.MaxColors. The actual SVG may contain fewer colors because Vecto performs its own merging and compaction.
- No extra complete SVG trace is run to estimate palette count; it scans pixels once (4,096 histogram bins).
- The estimator examines RGBA **after** the existing decoder, so the established PNG/Canvas fallback limitations still apply.
- The estimate is a starting point for flat illustrations/logos, not a general-purpose photographic palette optimizer.

## Validation

`dotnet run --project Vecto.PaletteTests/Vecto.PaletteTests.csproj -c Release`

Tests cover flat multicolor graphics, near-black shades, small saturated accents, transparent hidden RGB, and deterministic repeatability. PR CI also publishes the Blazor app without deploying it.

## QA / manual regression (required before release)

On the **same original OSIS JPEG**:
1. Auto Minimum Colors (expected approximately 6–7 initial clusters; measure actual selection).
2. Manual 7, manual 14, manual 24.
3. Compare visual fidelity (OSIS text, wheat, cotton, book, ball), region/path count, node count, and image dimensions.
4. Confirm no detail disappears in a second clean flat-vector logo and an image with small colored accents.
5. Check Android Chrome performance and the decoder path; verify the input source is identical across runs.

Do not claim a universal color optimum or quality improvement before this validation. There are no changes to boundary graph, subpixel fitting, or SVG writer.

## Relevant inspirations and licenses
- https://github.com/logolabs/inkvec (Apache-2.0; palette MDL)
- https://github.com/Kittl/vectorizing (GPL-2.0; reference only, **no source copied**)
- https://github.com/vardirhq/open-vectorizer (MIT; interior sample concepts)

The existing Vecto project originates from danielmevit/vecto; verify the applicable original-version license obligations before release.
