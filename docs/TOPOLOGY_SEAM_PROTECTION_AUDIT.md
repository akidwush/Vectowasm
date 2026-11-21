# Audit & experimental plan: Topology-Aware Seam Protection

**Repository:** akidwush/Vectowasm  
**Branch:** `experiment/topology-aware-underlayer-seams`  
**Status:** draft prototype; **DO NOT MERGE** without user review and a passing CI gate.

## 1. Direct audit of existing Stacked implementation

Source examined: `Vecto.Core/StackedShapeBuilder.cs`, `BoundaryGraph.cs`, `Tracer.cs`, and `SvgWriter.cs` from main.

- `BoundaryTracer` records open shared chains between degree-3+ junctions and closed cycles between two region components. A shared fitted `Chain.Curve` is reused by both regions (the right-hand one reversed by `Tracer.BuildLoop`).
- `StackedShapeBuilder` builds graph adjacency from `chain.Left/Right`, identifies opaque nested holes via a flood of connected regions excluding the enclosing parent, and **does not fill genuine transparent holes** (void sentinel).
- It creates a `frontOf` occlusion DAG and orders region IDs topologically. Sorting only by pixel area is not safe (thin outline regions can be smaller than their interior).
- Only selected nested parent cutouts are removed. **Two adjacent regions with no containment still share an exact curve but have no paint overlap.** Their separately anti-aliased fills can leave an apparent hairline in some renderers.
- Existing `StackedLayerReasoner` optionally groups consecutive same-colour paths; its raster gate checks pixel ownership parity, **not antialiased seam leakage**. The output no longer has one-to-one region ID mapping, so this prototype deliberately skips seam insertion when that optimizer is enabled.
- `SvgWriter` currently emits one ungrouped, fill-only region path. No per-shared-edge underpaint existed.

## 2. Implementation prototype (off by default)

`TraceOptions.EnableTopologySeamProtection = false` is the absolute default. Valid bleed range is 0.30–0.50 px; UI uses 0.35 px. Enabled via Advanced Settings **only in Stacked**. Planar and all existing default SVG bytes remain unchanged.

For each graph chain with two opaque region IDs, choose the back/front by the **actual Stacked topological painter order**, not polygon size. Reject:

- outer/transparent boundaries, and the dominant canvas background (opaque outer silhouette heuristic);
- a closed chain (nested closed contours are handled via Stacked hole fill);
- edges where parent already covers contained foreground;
- too-short chains, sharp or high-curvature spans, endpoint/junction guard radius 3 px, or any span whose 1.8px and 2.8px lateral raster samples do not remain in the two expected regions;
- thin region components, same-palette edges, ambiguous per-segment labels;
- `StackedLayerReasoningV1` combined with this experiment (skip rather than guess merged IDs).

On surviving open chain portions, subdivide the existing fitted Bézier **exactly** with de Casteljau, and add an auxiliary stroke with the BACK colour, `stroke-width = 2 × bleed` (0.70 px by default), flat caps and **no closing segment**. Emit immediately AFTER the back region's unmodified normal path and BEFORE the front region. This is an internal shared-edge helper, **not a global stroke on all shapes**. The front region's original `<path>` XML remains unchanged. The helper is the only new paint. Main SVG geometry, palette, segment count and topology are unchanged.

Because the stroke is centred on the shared path, half extends into the front footprint and half is hidden inside back fill. When the front is subsequently painted, its geometry and paint are unchanged. This can mask independently antialiased background leakage at some scales; it is not a proof of universal renderer parity.

Files:
- `Vecto.Core/TopologySeamProtection.cs`: topology/raster guards, curve slicing, helper selection and stats.
- `Vecto.Core/Tracer.cs`: optional `RegionPath.SeamUnderpaints` and `TraceDiagnostics.TopologySeams`, hook Stacked builder.
- `Vecto.Core/StackedShapeBuilder.cs`: paint-order-aware, Stacked-only integration.
- `Vecto.Core/SvgWriter.cs`: append **auxiliary** underpainting strokes; normal paths untouched.
- `Vecto.Core/TraceOptions.cs` + `Vecto.Browser/Pages/Index.razor`: opt-in flag, disabled unless Stacked and layer-reasoning is off, diagnostics.
- `Vecto.SeamTests` and `tests/verify_topology_seams.py`: C# and raster quality gate, plus CI.

## 3. Automated acceptance criteria

C# unit/integration assertions:
1. Flag OFF SVG is unchanged; ON must preserve **every original region path byte-for-byte**, its Bézier controls, fill and painter order.
2. Positive example of long internal red/blue boundary emits auxiliary underpaint only after the back fill and before the front fill.
3. Outer background adjacency, transparent holes, thin 2px feature, Planar, and the optional region-merging optimizer receive **no unsafe underpainting**.
4. All SVGs deterministic; the conventional exact-owner `Rasterizer` sees unchanged original vector fill geometry.

SVG renderer tests using **CairoSVG** at 100%, 200%, 400%:
- Generated via the actual C# SVG writer; independent 62.35px red/blue shared boundary with white backdrop, fixed original front and a separate 1px black detail.
- Measure RGB squared error to the analytically ideal two-colour compositing result. At each scale the protected variant must decrease seam-area background leakage.
- Absolutely no raster differences allowed outside a 1-source-pixel neighbourhood of the allowed seam; in particular no changed silhouette, background, or thin lineart pixels.
- End-to-end traced fixture uses actual shared-edge graph, and differences must remain confined to its known red/blue internal boundary.

These tests establish a **renderer-specific regression**. CairoSVG and Chromium/Firefox/WebKit may differ in antialiasing, colour management or zoom artefacts. Therefore the user should compare real artwork at multiple browser zoom levels before promoting the flag. It is possible for a safe shape to be skipped and receive no improvement.

## 4. Merge gate

Keep the feature in a draft PR until:
- all project/CI tests pass;
- original path parity, transparent-hole, thin-feature, 3-scale raster guards pass;
- there is no default output change;
- the user approves after reviewing the audit and real preview.

A zero-eligible-edge result is not forcibly turned into a visible difference.
