# Vecto WASM — C# in the browser

The original Vecto vectorization engine runs in **Blazor WebAssembly**. JPEG uses the original Vecto C# decoder when possible; PNG, WebP and BMP are decoded by the browser canvas API. SVG tracing is performed by original Vecto.Core C# inside WebAssembly. This is a source-code project, not a prebuilt WASM artifact.

- **Engine:** original C# Vecto.Core (source: https://github.com/danielmevit/vecto); 17 algorithm files copied unchanged, plus browser-only Parallel adapter.
- **UI:** standalone Blazor WebAssembly, no server-side tracing or database.
- **Hosting:** GitHub Pages via .github/workflows/deploy.yml on every main push.
- **Defaults:** Style Auto, Detail Medium, MaxColors 16. Input limit 20 MiB and 8 megapixels.
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

Upload a PNG/JPG/WebP/BMP and **Auto Minimum Colors** automatically estimates a small number of useful inks before invoking the original C# tracer. Manual color limits and the original High/24 and desktop presets remain available.

This is a deterministic, MDL-inspired **heuristic**, not a guarantee of mathematically minimal colors or superior SVG fidelity. See [AUTO_PALETTE.md](AUTO_PALETTE.md) for design details, tests and QA requirements.
