# DikaTrace hybrid Rust quality lab — experiment (PR #19)

**Production DikaTracer is completely unchanged.** This branch only adds a
standalone web benchmark and an isolated CI artifact. Do not merge or switch
default engines without a real source-relative comparison.

## Why this instead of blindly rewriting a second vectorizer?
The tiger benchmark found 294 -> 46 top-level paths with almost unchanged
true geometry (2,395 -> 2,393 L/C segments). Vector Magic had far fewer
segments and lower source RGB MAE. A third independent algorithm is useful
only when the comparison uses the *same source image and measurement*.

This lab uses the uploaded Rust Covecto architecture, specifically its
VTracer 0.6.5-backed Spline engine and optionally PixelExact. It **does not**
pretend Covecto invented the spline engine.

## Reproducible binary
GitHub Actions fetches **a pinned upstream commit** of Covecto v0.3.0:
`b9142fbb6516b179afa76d6bb831dfeea54f5132`.
It compiles `covecto-wasm` using `cargo build --locked --release` targeting
`wasm32-unknown-unknown`; executes the upstream ABI smoke test; and bundles
the binary + MIT LICENSE in the CI artifact. No random npm CDN, API key, or
remote inference. Source and SVG never leave the browser once the lab starts.

## Usage (CI artifact, not public deployment)
1. Open the successful PR #19 GitHub Actions check named **Hybrid Rust VTracer quality lab (isolated)**.
2. Download the `dikatrace-hybrid-rust-quality-lab` artifact, extract it.
3. From the extracted folder run `python3 -m http.server 8000` on a PC.
4. Open `http://localhost:8000` in the same machine's browser. For a phone,
   serve the folder over a trusted local HTTPS or same-LAN HTTP origin.
   Simply opening `file://index.html` won't work (Web Worker and WASM fetch).
5. Upload the original raster input and, optionally, the three existing
   comparison SVGs (DikaTracer Baseline, DikaTracer Experiment, Vector Magic).
6. Choose Rust resolution, keep Spline for complex JPGs, run quality lab.
7. Compare metrics and visually inspect small details; download any candidate
   SVG or export the machine-readable JSON measurements.

## What the quality gate measures
All candidate SVGs are rendered into RGBA on white at the **same**
source-relative evaluation resolution (max 512px side), including the original
full-size DikaTracer/Vector Magic files. The Rust engine may trace a
downscaled input (default max side 768px); its output retains **that** actual
trace resolution. Results are not claimed to be full-resolution vectorizations.

- RGB MAE across all pixels (lower better).
- Block-local luminance SSIM (nonoverlapping 8×8 blocks, **not** skimage's
  standard Gaussian-window SSIM; higher better).
- Dark-mask IoU using luminance threshold 128 (not a multicolor segmentation
  measure; higher better).
- Accent MAE over small chromatic source bins (diagnostic only, not an object
  classifier; can also pick up JPEG fringe).
- SVG `<path>` count, M subpath token count, and shape-command **tokens**,
  which are approximate for SVG paths with implicit repetition.
- Processing time for the Rust vectorizer (uploaded SVGs have no timing).

The reported candidate ranking minimizes
`MAE + 8*(1-SSIM_8x8) + 0.12*accentMAE`.
Only within 0.15 loss does the tie-break prefer fewer SVG commands.
This policy chooses from candidates *without modifying their geometry* and
must not be misconstrued as proving a candidate's editability or correctness.

## Safety
- Raw raster, uploaded reference SVGs and exports stay local in your browser.
- Workers isolate the Rust computation. **Cancel** terminates the current
  worker; each engine run is capped at 120s.
- Rust accepts up to 1.5MP raw RGBA and 40MB SVG in the worker; lab caps
  input images to 12MP, uploaded SVG to 12MB.
- PixelExact is blocked for JPEG, more than 256x256 pixels or excessive
  sampled color diversity to avoid a pathological explosion in regions.
- The lab rejects SVGs containing script/event attributes/foreignObject.
  In-browser SVG is rendered as an image, never as executable HTML.
- Browser canvas memory and decoder behavior vary: no guarantee every old
  Android WebView supports all the APIs; use a modern Chrome/Firefox browser.
- Metrics do not certify topology, hidden layers, true circles, or correct
  narrow accents. Full-size SVG inspection remains necessary.
- No uploading a private user-provided tiger JPG to public repo/CI.

## Next acceptance gate
Run the actual tiger.jpg and 10 representative icons/illustrations through:
DikaTracer Baseline, DikaTracer Experiments, Vector Magic, Covecto Spline.
Report source-relative MAE, same-raster SSIM/IoU, path and command counts, and
zoomed manual inspection (eye/yellow, teeth, black/white whiskers, tips).
Only if third-party engine wins, consider porting/embedding it in the default
DikaTracer workflow. Otherwise use measured failures to improve the C#
Bezier fitting or accent-color palette preservation.

**Do not merge this branch before reviewing actual image results.**


## First real tiger benchmark: VTracer loses (not a production upgrade)

We executed the pinned Rust WASM directly on the uploaded `tiger.jpg`
**without uploading that image to GitHub**. Both Rust presets traced a 768×768
resized raster. Existing DikaTracer and Vector Magic SVGs retained their
original geometry. All five outputs were rasterized by CairoSVG at **512×512**
and compared to the same source JPEG downsample at 512×512.

| Candidate | RGB MAE ↓ | Gaussian RGB SSIM ↑ | Dark IoU ↑ | SVG paths | SVG geometric command tokens | SVG bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Vector Magic Stacked | **3.618** | **0.94520** | **97.32%** | 84 | **1,345** | 63,692 |
| DikaTracer Experimental | 4.363 | 0.94260 | 96.95% | 46 | 2,393 | 90,234 |
| DikaTracer Baseline | 4.376 | 0.94249 | 96.96% | 294 | 2,395 | 101,252 |
| Covecto/VTracer poster (768) | 6.501 | 0.91206 | 94.04% | 3,024 | 36,565 | 1,261,325 |
| Covecto/VTracer photo (768) | 8.419 | 0.87954 | 91.68% | 205 | 10,502 | 381,264 |

These values are **not the browser's 8×8 local SSIM**; Gaussian SSIM was
computed externally with scikit-image to compare real SVG renders. The
metric resolution and Rust trace downscaling are crucial to interpretation.

Conclusion: **do not adopt VTracer as the DikaTracer default on this
complex mascot**. Covecto Spline poster preserves the yellow irises that both
DikaTracer and Vector Magic omitted, but produces extreme path fragmentation
and worse source-relative image error. This is a clue about small chromatic
regions, not evidence VTracer's geometry is superior.

### Next algorithm experiment: Accent Ink V1
The PR also adds a completely opt-in C# `AccentInkPaletteV1`. It looks for
small but spatially coherent saturated **solid source pixels** that the global
palette may underweight. Coarse hue/value groups and local 4-neighbor flatness
must receive sufficient support in one 24px tile. A candidate must be far from
every existing Oklab palette seed. At most two additions are permitted, and
the original max-color budget is respected. Only `Blended` non-exact palette
mode runs the pass, so Crisp exact colors and Photo are unaffected.
No source pixel is rewritten: existing segmentation, boundary topology and
curve-fitting still decide output. Defaults **OFF**.

Synthetic tests ensure a yellow eye absent from the starter palette can be
recovered, a scattered chromatic speckle cannot, palette bounds are respected,
and exact/disabled outputs remain byte-identical.

**Important:** An 18×18 synthetic test is not proof of better tiger image
fidelity. Next release gate: run the same 1536×1536 tiger image in **Blended /
Stacked / High** with Accent Ink V1 OFF and ON; compare eye saturation,
preservation of actual yellow, input-relative MAE/SSIM and geometry count.
Decline to enable by default if error or topology regress. No production merge.


### Real C# A/B after Accent Ink V1: partial improvement only, NOT accepted

We built the self-contained Linux C# benchmark from this PR, and **executed
it locally on the user's original 1536×1536 tiger.jpg** (not in a public CI
fixture). Both modes used identical **Blended / High / Stacked + Global DP**,
recommended AutoPaletteEstimator color budget, and the same PixelCompressor.

| Full-resolution C# output | MAE ↓ | Internal local SSIM ↑ | Mask IoU ↑ | Paths | Geometric segments | P99 boundary error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline (Accent OFF) | 5.7973 | **0.92309** | 96.672% | **551** | **6,315** | **2.236px** |
| Accent V1 (initial, no hue guard) | **5.6593** | 0.92409 | 96.752% | 607 | 6,950 | 2.828px |
| Accent V1 + hue guard (current) | 5.7838 | 0.92273 | 96.677% | 562 | 6,377 | 2.236px |

This is **a different trace configuration and quality metric resolution**
from the uploaded, exported DikaTracer baseline SVG described above:
do not compare these numbers to the 512px external Gaussian SSIM.

The first V1 added a **dark brown** pigment, not the desired yellow. Adding
the Oklab hue novelty gate suppresses redundant brown shading and creates a
more ochre-colored palette seed, but does **not** restore the eyes accurately
enough. In the current version, SSIM drops marginally and complexity rises.
Thus the new C# palette heuristic is **not a confirmed quality improvement**.
It stays OFF, and should not be enabled automatically or merged into the
production engine without better evidence. The separate Rust A/B lab is
still a useful validated benchmark.

The next optimization should be an error-guided **local region palette**
candidate evaluated with the *original source*, preserving topological
boundaries and rejecting any candidate that worsens fidelity/complexity.
A global hue detector by itself is not sufficient on this tiger.
