using System.Globalization;
using System.Text.RegularExpressions;
using Vecto.Core;

static void Check(bool good, string message)
{
    if (!good) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static RasterImage ThreePaints(bool thin = false)
{
    var img = new RasterImage(128, 96);
    var white = new Rgba32(250, 250, 250, 255);
    var red = new Rgba32(220, 60, 49, 255);
    var blue = new Rgba32(32, 108, 185, 255);
    for (int y = 0; y < img.Height; y++)
    for (int x = 0; x < img.Width; x++)
    {
        var c = white;
        if (x >= 20 && x < 62 && y >= 16 && y < 80) c = red;
        if (x >= 62 && x < (thin ? 64 : 108) && y >= 16 && y < 80) c = blue;
        img.SetPixel(x, y, c);
    }
    return img;
}

static TraceOptions Options(ShapeMode shape, bool protect, bool reasoning = false) =>
    new()
    {
        Style = ImageStyle.Crisp,
        Detail = DetailLevel.High,
        PaletteMode = PaletteMode.FixedCount,
        ColorCount = 3,
        ShapeMode = shape,
        CurveFitting = false,
        PolygonEpsilon = 0,
        OptimizeNodes = false,
        EnableTopologySeamProtection = protect,
        TopologySeamBleedPx = .35,
        StackedLayerReasoningV1 = reasoning,
    };

static string[] OriginalPathTags(string svg) =>
    Regex.Matches(svg, "<path [^>]*?/>", RegexOptions.CultureInvariant)
        .Select(m => m.Value).Where(tag => !tag.Contains("data-seam"))
        .ToArray();

var source = ThreePaints();
var baseline = Tracer.Trace(source, Options(ShapeMode.Stacked, false));
var augmented = Tracer.Trace(source, Options(ShapeMode.Stacked, true));
string svgBase = SvgWriter.Write(baseline.Document);
string svgAug = SvgWriter.Write(augmented.Document);
Check(augmented.Diagnostics.TopologySeams.ConsideredSharedEdges > 0,
    "Interior shared-edge graph is inspected");
Check(augmented.Diagnostics.TopologySeams.SafeSharedEdges > 0 &&
      augmented.Diagnostics.TopologySeams.UnderpaintRuns > 0,
    "At least one internal back/front edge receives underpainting");
Check(svgAug.Contains("data-seam=\"1\"") &&
      svgAug.Contains("stroke-linejoin=\"round\""),
    "Only explicit auxiliary underpaint paths receive a stroke");
Check(!svgBase.Contains("data-seam"),
    "Flag OFF retains existing SVG exporter output");
Check(OriginalPathTags(svgBase).SequenceEqual(OriginalPathTags(svgAug)),
    "Every original region path is byte-for-byte unchanged with underpaint ON");
Check(baseline.Document.Regions.Count == augmented.Document.Regions.Count &&
      Enumerable.Range(0, baseline.Document.Regions.Count)
        .All(i => baseline.Document.Regions[i].Loops
            .Zip(augmented.Document.Regions[i].Loops)
            .All(x => x.First.SequenceEqual(x.Second))),
    "Region boundaries, Bézier controls and painter order are unchanged");
Check(Rasterizer.Render(baseline.Document, supersample: 1).Pixels.SequenceEqual(
      Rasterizer.Render(augmented.Document, supersample: 1).Pixels),
    "Original vector fill geometry stays identical at exact subpixel ownership");

var paths = Regex.Matches(svgAug, "<path [^>]*?/>")
    .Select(match => match.Value).ToArray();
int underpaintTag = Array.FindIndex(paths, p => p.Contains("data-seam"));
Check(underpaintTag >= 1 && underpaintTag + 1 < paths.Length &&
      !paths[underpaintTag + 1].Contains("data-seam"),
    "Underpaint emitted after a painted back region and before a front region");
Check(augmented.Diagnostics.TopologySeams.SkippedNoOverlapNeed > 0 ||
      augmented.Diagnostics.TopologySeams.SkippedUnsafe > 0,
    "Background and/or already stacked edges are excluded");

var planarOff = SvgWriter.Write(Tracer.Trace(source, Options(ShapeMode.Planar, false)).Document);
var planarFlag = Tracer.Trace(source, Options(ShapeMode.Planar, true));
Check(SvgWriter.Write(planarFlag.Document) == planarOff &&
      planarFlag.Diagnostics.TopologySeams.UnderpaintRuns == 0,
    "Planar output stays byte-identical even with experimental flag ON");

var thinRaster = ThreePaints(thin: true);
var thinOff = Tracer.Trace(thinRaster, Options(ShapeMode.Stacked, false));
var thinOn = Tracer.Trace(thinRaster, Options(ShapeMode.Stacked, true));
Check(OriginalPathTags(SvgWriter.Write(thinOff.Document))
    .SequenceEqual(OriginalPathTags(SvgWriter.Write(thinOn.Document))),
    "Thin detail boundary geometry is unchanged");
Check(thinOn.Diagnostics.TopologySeams.SafeSharedEdges == 0,
    "Two-pixel-thick feature is never expanded by a seam stroke");

// Solid black outline, transparent interior: an exterior/transparent
// boundary cannot be underpainted under this policy.
var transparent = new RasterImage(64, 64);
for (int y = 9; y < 55; y++)
for (int x = 9; x < 55; x++)
    if (x < 15 || x >= 49 || y < 15 || y >= 49)
        transparent.SetPixel(x, y, new Rgba32(0, 0, 0, 255));
var transparentResult = Tracer.Trace(transparent, Options(ShapeMode.Stacked, true));
Check(transparentResult.Diagnostics.TopologySeams.UnderpaintRuns == 0 &&
      Rasterizer.Render(transparentResult.Document, supersample: 1)
          .GetPixel(32, 32).A == 0,
    "Transparent hole and outer silhouette remain untouched");

var reasoning = Tracer.Trace(source, Options(ShapeMode.Stacked, true, reasoning: true));
Check(reasoning.Diagnostics.TopologySeams.SkippedForLayerReasoning &&
      !SvgWriter.Write(reasoning.Document).Contains("data-seam"),
    "Seam feature safely skips the optional region-merging painter");
Check(svgAug == SvgWriter.Write(Tracer.Trace(source, Options(ShapeMode.Stacked, true)).Document),
    "Seam underpaint export is deterministic");

// Export an independently controlled subpixel-edge fixture through the REAL
// SVG writer. A black 1px line is deliberately well away from the seam.
static List<CubicBezier> Box(double x0, double y0, double x1, double y1)
{
    Vec2 a = new(x0, y0), b = new(x1, y0), c = new(x1, y1), d = new(x0, y1);
    return new()
    {
        CubicBezier.Line(a, b), CubicBezier.Line(b, c),
        CubicBezier.Line(c, d), CubicBezier.Line(d, a)
    };
}
static RegionPath Part(int palette, int area, List<CubicBezier> loop,
    IReadOnlyList<SeamUnderpaint>? seams = null) => new()
{
    PaletteIndex = palette, Area = area, Loops = new() { loop },
    SeamUnderpaints = seams ?? Array.Empty<SeamUnderpaint>()
};
VectorDocument Reference(bool withSeam)
{
    const double boundary = 62.35;
    var red = new RegionPath
    {
        PaletteIndex = 1,
        Area = 46 * 76,
        Loops = new() { Box(16, 10, boundary, 86) },
        SeamUnderpaints = withSeam
            ? new[] { new SeamUnderpaint(
                new[] { CubicBezier.Line(new Vec2(boundary, 15),
                                         new Vec2(boundary, 81)) }, .70) }
            : Array.Empty<SeamUnderpaint>()
    };
    return new VectorDocument
    {
        Width = 128,
        Height = 96,
        ShapeMode = ShapeMode.Stacked,
        Palette = new()
        {
            new() { Color = new Rgba32(255, 255, 255, 255), PixelCount = 1 },
            new() { Color = new Rgba32(220, 60, 49, 255), PixelCount = 1 },
            new() { Color = new Rgba32(32, 108, 185, 255), PixelCount = 1 },
            new() { Color = new Rgba32(12, 12, 12, 255), PixelCount = 1 }
        },
        Regions = new()
        {
            Part(0, 128 * 96, Box(0, 0, 128, 96)),
            red,
            Part(2, 46 * 76, Box(boundary, 10, 112, 86)),
            Part(3, 14, Box(40, 42, 41, 56))
        }
    };
}
string referenceBase = SvgWriter.Write(Reference(false));
string referenceFixed = SvgWriter.Write(Reference(true));
Check(OriginalPathTags(referenceBase).SequenceEqual(OriginalPathTags(referenceFixed)),
    "Subpixel fixture front layer, silhouette and hairline are unchanged exactly");
string output = args.Length >= 2 && args[0] == "--output" ? args[1] : "seam-artifacts";
Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output, "seam_reference_baseline.svg"), referenceBase);
File.WriteAllText(Path.Combine(output, "seam_reference_protected.svg"), referenceFixed);
File.WriteAllText(Path.Combine(output, "seam_traced_baseline.svg"), svgBase);
File.WriteAllText(Path.Combine(output, "seam_traced_protected.svg"), svgAug);
Console.WriteLine($"SEAM_STATS: {augmented.Diagnostics.TopologySeams}");
Console.WriteLine("Seam geometry checks passed; SVG fixtures written to " + output);
