# Ekstracev1 — third tracing mode (experimental)

## Scope and provenance

Ekstracev1 is an **independent experimental C# contour fitting path**, adapted from our Stage 1–6 research prototype. It is **not** the recovered original Vector Magic source. It is not claimed to match Vector Magic or be better than Vecto on arbitrary inputs.

The host project is **DikaTrace / Vectowasm** (.NET 8, Blazor WebAssembly). Existing production Vecto fitting, Local Edge V3, palette inference, segmentation, shared-boundary graph, output writer, and A/B Original–V3 mode remain unchanged.

This integration specifically ports the following pieces of the earlier Stage 6 contour research:

- chord-length parameterization;
- constrained least-squares for cubic Bézier endpoint handles (nonnegative 2-variable fit);
- three rounds of bounded Newton reparameterization;
- G1 tangent alignment at smooth subdivided joints;
- protected G0 corners and open-chain endpoints;
- adaptive error-based splitting;
- safeguards against curve overshoot between source vertices;
- bounded work and per-chain fallback to Vecto's existing fitter.

The research CLI's other settings (uniform/centripetal parameter modes, full global C1 solve, external CSV CLI) are **not exposed** in the browser integration. This is not an assertion of one-to-one complete Stage 6 port.

## How it works

```text
PNG/JPG/WebP/BMP (browser decode)
  -> optional pixel cleanup (existing)
  -> palette / segmentation (existing)
  -> topology-safe shared boundary graph (existing)
  -> smoothing, protected corners, subpixel edge location (existing)
  -> Ekstracev1 cubic fitter (new, opt-in)
  -> unchanged shared-edge / reversed-chain assembly
  -> SVG paths, no embedded raster
```

For each already-prepared contour chain, the new fitter preserves graph junction endpoints and uses a single curve list for both neighboring regions. This avoids the crack risk from independently fitting both sides of an edge.

Ekstracev1 is chosen in the **Engine vektorisasi** control. Default is **Original**. **Local Edge V3** and **Original vs V3** continue to work as before. While Ekstracev1 is selected, the existing Vecto node-reduction pass is bypassed to avoid inadvertently removing its G1 joints. The UI labels and filename suffix make the selected mode visible in exports.

If a chain is unusually large (>4096 input points), invalid, or otherwise cannot meet basic validation, the existing Vecto curve fitter is used for that chain; no output quality parity is implied.

## Checks and limits

Run:

```bash
dotnet build Ekstracev1.Tests/Ekstracev1.Tests.csproj -c Release
dotnet run --project Ekstracev1.Tests/Ekstracev1.Tests.csproj -c Release
dotnet publish Vecto.Browser/Vecto.Browser.csproj -c Release -o publish
dotnet run --project Vecto.PaletteTests/Vecto.PaletteTests.csproj -c Release
```

The GitHub Actions file `.github/workflows/ekstracev1-ci.yml` runs these checks on the feature branch/PR. Test harness checks open straight segments, a protected corner, a closed circle with tangent continuity, and an actual raster-to-SVG trace through `Tracer.Trace`.

Full visual parity (IoU, boundary deviation, microholes) and performance on complex user images remain **unverified** until comparable raster images are tested in a browser. Do not merge as a default production mode on the basis of synthetic checks alone.
