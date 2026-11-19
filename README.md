# Vecto WASM — C# in the browser

The original Vecto vectorization engine runs in **Blazor WebAssembly**. JPEG uses the original Vecto C# decoder when possible; PNG, WebP and BMP are decoded by the browser canvas API. SVG tracing is performed by original Vecto.Core C# inside WebAssembly. This is a source-code project, not a prebuilt WASM artifact.

- **Engine:** original C# Vecto.Core (source: https://github.com/danielmevit/vecto); 17 algorithm files copied unchanged, plus browser-only Parallel adapter.
- **UI:** standalone Blazor WebAssembly, no server-side tracing or database.
- **Hosting:** GitHub Pages via .github/workflows/deploy.yml on every main push.
- **Defaults:** Style Auto, Detail Medium, automatically inferred palette size. Input limit 20 MiB and 8 megapixels.
- **Validation:** compile and run in GitHub Actions; then compare browser output and CLI output on representative images before claiming byte parity.
- **Disclaimer:** browser UI does synchronous work on the main thread; large images may freeze the tab. Numerical parity between desktop and WASM is not guaranteed. Browser canvas decoding and alpha/colorspace handling can differ from the original CLI, so input pixel hashes and SVG output may differ for some files. No ImageSharp license is required for this browser build.

## Local build
```bash
dotnet restore Vecto.Browser/Vecto.Browser.csproj
dotnet publish Vecto.Browser/Vecto.Browser.csproj -c Release -o publish
```

The static site is in `publish/wwwroot`.

## Deploy from Android
GitHub Actions builds the site automatically from main. Set **Settings → Pages → Build and deployment → Source: GitHub Actions** on this repo. No PC/VPS is required to build or host it.

## Security
Files are processed in the browser and never intentionally uploaded to a remote server. No credentials or private licensing file are included.

## Auto Minimum Colors (experimental)

Upload a PNG/JPG/WebP/BMP and Vecto automatically estimates the useful number of distinct inks before invoking the original C# tracer. The UI does not ask users to choose 7, 14, 15, 24 or another hard-coded count; Style and Detail remain configurable. More colors are preserved when they are supported by input evidence.

This is a deterministic, MDL-inspired **heuristic**, not a guarantee of mathematically minimal colors or superior SVG fidelity. See [AUTO_PALETTE.md](AUTO_PALETTE.md) for design details, tests and QA requirements.

## DikaTrace Classic Studio UI

The public interface is branded **DikaTrace**, with a classic early-2000s desktop wizard layout inspired by Vector Magic's historical interface (not an official Vector Magic product). Fully Automatic, Basic and Advanced modes, bitmap/vector/split preview, zoom, reset and SVG export use the unchanged Vecto C# tracing pipeline. The engine/project names and licensing are unchanged.


## Automatic pixel preparation (all input sizes)

Every PNG, JPG, WebP, or BMP upload is decoded before passing through a
quality-gated, deterministic pixel cleanup stage, **regardless of encoded byte size**.
For eligible flat-vector illustrations, nearby RGB variations in opaque interior
fills are merged and isolated low-contrast specks are repaired. Alpha, high-contrast
edges, thin strokes, pixel dimensions, and the original preview are preserved.
A 24-level per-channel maximum difference and mean error guard reject overly
aggressive cleanup; photos and continuous gradients may remain pixel-identical.

This improves the raster **used for vectorization**, not the encoded source file size.
It does not use iLoveIMG's undisclosed implementation and cannot guarantee
exact 100% visual equivalence or a specific SVG node/path reduction.
Its purpose is to remove unnecessary raster noise while favoring fidelity.

The browser Canvas API remains the fallback decoder for WebP, BMP and unsupported
encoded formats; the pixel-preparation stage itself runs on RGBA in .NET C#
to avoid redundant Canvas encode/decode and unnecessary lossy recompression.
Auto Palette and tracing read the exact same prepared raster.
