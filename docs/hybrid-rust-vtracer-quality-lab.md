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
