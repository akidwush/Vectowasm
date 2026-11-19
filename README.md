# Vecto WASM — C# in the browser

The original Vecto vectorization engine runs in **Blazor WebAssembly**. PNG, JPEG, WebP and BMP are decoded locally and SVG is generated entirely in the browser. This is a source-code project, not a prebuilt WASM artifact.

- **Engine:** original C# Vecto.Core (source: https://github.com/danielmevit/vecto); 17 algorithm files copied unchanged, plus browser-only Parallel adapter.
- **UI:** standalone Blazor WebAssembly, no server-side tracing or database.
- **Hosting:** GitHub Pages via .github/workflows/deploy.yml on every main push.
- **Defaults:** Style Auto, Detail Medium, MaxColors 16. Input limit 20 MiB and 8 megapixels.
- **Validation:** compile and run in GitHub Actions; then compare browser output and CLI output on representative images before claiming byte parity.
- **Disclaimer:** browser UI does synchronous work on the main thread; large images may freeze the tab. Numerical parity between desktop and WASM is not guaranteed. ImageSharp 4.1.3 has licensing requirements that must be reviewed for commercial use.

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
