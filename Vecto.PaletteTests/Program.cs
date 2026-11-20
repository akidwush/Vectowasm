using Vecto.Core;

static RasterImage Bands(params Rgba32[] palette)
{
    var image = new RasterImage(192, 128);
    for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            image.SetPixel(x, y, palette[Math.Min(palette.Length - 1, x * palette.Length / image.Width)]);
    return image;
}
static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

var black = new Rgba32(12, 11, 10, 255);
var shade = new Rgba32(31, 30, 28, 255);
var white = new Rgba32(255, 255, 255, 255);
var yellow = new Rgba32(232, 219, 7, 255);
var red = new Rgba32(217, 36, 45, 255);
var green = new Rgba32(24, 167, 106, 255);
var maroon = new Rgba32(138, 59, 67, 255);

var plain = AutoPaletteEstimator.Analyze(Bands(black, white, red));
Check(plain.EstimatedInkCount == 3 && plain.RecommendedMaxColors == 3,
    "Flat three-colour logo needs three inks, no spare cluster");
var similar = AutoPaletteEstimator.Analyze(Bands(black, shade));
Check(similar.EstimatedInkCount == 1, "Near-black JPEG-like shades do not become separate inks");
var many = AutoPaletteEstimator.Analyze(Bands(black, white, yellow, red, green, maroon));
Check(many.EstimatedInkCount == 6, "Six strong graphic inks remain distinct");

var accent = Bands(black, white);
for (int y = 40; y < 55; y++)
    for (int x = 46; x < 61; x++)
        accent.SetPixel(x, y, green);
var accentResult = AutoPaletteEstimator.Analyze(accent);
Check(accentResult.EstimatedInkCount == 3,
    "Small isolated coloured accent is retained instead of merged away");

var first = AutoPaletteEstimator.Analyze(Bands(black, white, yellow, red, green, maroon));
var again = AutoPaletteEstimator.Analyze(Bands(black, white, yellow, red, green, maroon));
Check(first == again, "Repeated estimates are deterministic");

var transparent = new RasterImage(64, 64);
for (int y = 0; y < 64; y++)
    for (int x = 0; x < 64; x++)
        transparent.SetPixel(x, y, new Rgba32(255, 0, 0, 0));
Check(AutoPaletteEstimator.Analyze(transparent).EstimatedInkCount == 1,
    "Invisible RGB does not create phantom colours");

Console.WriteLine("Adaptive palette estimator tests complete.");


// All supported files pass through the same pixel quality guard.
 // Tests use already-decoded RGBA to isolate the cleanup engine.
var noisy = Bands(new Rgba32(231, 20, 19, 255));
noisy.SetPixel(40, 50, new Rgba32(220, 31, 28, 255));
byte[] before = (byte[])noisy.Pixels.Clone();
var optimized = PixelCompressor.Prepare(noisy);
Check(optimized.Applied && optimized.ChangedPixels > 0,
    "All image sizes can receive automatic safe pixel cleanup");
var atThreshold = optimized;
Check(atThreshold.Image.Width == noisy.Width && atThreshold.Image.Height == noisy.Height,
    "Pixel cleanup preserves width and height");
Check(atThreshold.Image.GetPixel(40, 50).R == 231,
    "Pixel cleanup repairs low-contrast isolated speck");
Check(noisy.Pixels.SequenceEqual(before),
    "Pixel cleanup never modifies original RGBA or preview");

var stripe = Bands(new Rgba32(255, 255, 255, 255));
for (int y = 0; y < stripe.Height; y++)
    stripe.SetPixel(91, y, new Rgba32(0, 0, 0, 255));
var stripeResult = PixelCompressor.Prepare(stripe);
Check(stripeResult.Image.GetPixel(91, 60).R == 0 &&
    stripeResult.Image.GetPixel(90, 60).R == 255,
    "High-contrast one-pixel black line is preserved");

var semi = Bands(new Rgba32(20, 150, 90, 127));
var semiResult = PixelCompressor.Prepare(semi);
Check(semiResult.Image.Pixels.SequenceEqual(semi.Pixels),
    "Semi-transparent pixels are not modified");

var repeatA = PixelCompressor.Prepare(noisy);
var repeatB = PixelCompressor.Prepare(noisy);
Check(repeatA.Image.Pixels.SequenceEqual(repeatB.Image.Pixels),
    "Pixel cleanup is deterministic");


var gradient = new RasterImage(192, 128);
for (int y = 0; y < gradient.Height; y++)
    for (int x = 0; x < gradient.Width; x++)
    {
        byte v = (byte)Math.Round(x * 255.0 / (gradient.Width - 1));
        gradient.SetPixel(x, y, new Rgba32(v, v, v, 255));
    }
var gradientCleaned = PixelCompressor.Prepare(gradient);
Check(gradientCleaned.Image.Pixels.SequenceEqual(gradient.Pixels),
    "Smooth gradient remains pixel-identical");

var underThreshold = PixelCompressor.Prepare(noisy);
Check(underThreshold.Applied && underThreshold.Image.GetPixel(40, 50).R == 231,
    "Input below the old 900 KB threshold is optimized");

var twoColorGraphic = Bands(new Rgba32(245, 245, 245, 255),
    new Rgba32(13, 13, 13, 255));
var edgeX = 96;
var edgeOrig = twoColorGraphic.GetPixel(edgeX, 60);
var edgePrepared = PixelCompressor.Prepare(twoColorGraphic).Image.GetPixel(edgeX, 60);
Check(edgePrepared == edgeOrig,
    "Automatic cleanup preserves sharp two-color boundary");
Check(PixelCompressor.Prepare(twoColorGraphic).Image.Pixels
        .SequenceEqual(twoColorGraphic.Pixels),
    "Already clean artwork remains pixel-identical");


// Shape stacking: a black frame around a red fill with a smaller white
// foreground and black dot. Thin parent frame has smaller area than its fill.
static RasterImage StackedFixture()
{
    var img = new RasterImage(64, 64);
    var white = new Rgba32(250, 250, 250, 255);
    var black = new Rgba32(0, 0, 0, 255);
    var red = new Rgba32(222, 17, 20, 255);
    for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            var c = white;
            if (x >= 8 && x < 56 && y >= 8 && y < 56) c = black;
            if (x >= 10 && x < 54 && y >= 10 && y < 54) c = red;
            if (x >= 20 && x < 44 && y >= 20 && y < 44) c = white;
            if (x >= 29 && x < 35 && y >= 29 && y < 35) c = black;
            img.SetPixel(x, y, c);
        }
    return img;
}
TraceOptions StackingOptions(ShapeMode mode) => new()
{
    Style = ImageStyle.Crisp, ShapeMode = mode,
    CurveFitting = false, PolygonEpsilon = 0
};
var sampleImage = StackedFixture();
var planarTrace = Tracer.Trace(sampleImage, StackingOptions(ShapeMode.Planar));
var stackedTrace = Tracer.Trace(sampleImage, StackingOptions(ShapeMode.Stacked));
Check(stackedTrace.Document.ShapeMode == ShapeMode.Stacked,
    "Stacked option produces stacked vector document");
Check(stackedTrace.Document.Regions.Count == planarTrace.Document.Regions.Count,
    "Stacked mode retains connected shapes");
Check(stackedTrace.Document.Regions.Sum(r => r.Loops.Count) <
      planarTrace.Document.Regions.Sum(r => r.Loops.Count),
    "Opaque nested holes become overlapping shapes");
var rasterPlanar = Rasterizer.Render(planarTrace.Document, supersample: 1);
var rasterStack = Rasterizer.Render(stackedTrace.Document, supersample: 1);
Check(rasterPlanar.Pixels.SequenceEqual(rasterStack.Pixels),
    "Stacked mode produces same visible pixels in nested example");
string stackedSvg = SvgWriter.Write(stackedTrace.Document);
Check(!stackedSvg.Contains("<g ") && !stackedSvg.Contains("</g>"),
    "Stacked SVG contains no groups");
Check(stackedSvg.Split("<path ", StringSplitOptions.None).Length - 1
      == stackedTrace.Document.Regions.Count,
    "Every stacked shape is one independent SVG path");
Check(!stackedSvg.Contains("dikatrace-layer-") &&
      !stackedSvg.Contains("data-palette-index="),
    "Stacked SVG has no group-only attributes");
Check(!SvgWriter.Write(planarTrace.Document).Contains("<g "),
    "Planar SVG remains ungrouped");

var transparentRing = new RasterImage(48, 48);
for (int y = 5; y < 42; y++)
    for (int x = 5; x < 42; x++)
        if (x < 8 || x >= 39 || y < 8 || y >= 39)
            transparentRing.SetPixel(x, y, new Rgba32(0, 0, 0, 255));
var transparentStack = Tracer.Trace(transparentRing, StackingOptions(ShapeMode.Stacked));
Check(Rasterizer.Render(transparentStack.Document, supersample: 1)
    .GetPixel(20, 20).A == 0, "Transparent holes remain transparent");
Check(SvgWriter.Write(Tracer.Trace(sampleImage, StackingOptions(ShapeMode.Stacked)).Document) == stackedSvg,
    "Stacked output remains deterministic");


// Segment minimization: splitting one known smooth cubic must be reversible
// without adding new paths, shifting sharp corners or adding group nodes.
static (CubicBezier First, CubicBezier Second) SplitHalf(CubicBezier c)
{
    static Vec2 Midpoint(Vec2 x, Vec2 y) => (x + y) * 0.5;
    var a = Midpoint(c.P0, c.P1);
    var b = Midpoint(c.P1, c.P2);
    var d = Midpoint(c.P2, c.P3);
    var e = Midpoint(a, b);
    var f = Midpoint(b, d);
    var center = Midpoint(e, f);
    return (new CubicBezier(c.P0, a, e, center),
            new CubicBezier(center, f, d, c.P3));
}
var smooth = new CubicBezier(
    new Vec2(0, 0), new Vec2(20, 0),
    new Vec2(20, 20), new Vec2(40, 20));
var halves = SplitHalf(smooth);
var mergedSmooth = BezierNodeOptimizer.Reduce(
    new[] { halves.First, halves.Second }, null, 1.0);
Check(mergedSmooth.Count == 1,
    "Two smooth consecutive cubic segments become one");
Check(mergedSmooth[0].P0 == smooth.P0 &&
      mergedSmooth[0].P3 == smooth.P3,
    "Node minimization preserves chain endpoints");

var corner = BezierNodeOptimizer.Reduce(
    new[] { CubicBezier.Line(new Vec2(0, 0), new Vec2(30, 0)),
            CubicBezier.Line(new Vec2(30, 0), new Vec2(30, 30)) },
    null, 1.0);
Check(corner.Count == 2,
    "Right-angle corner must not be simplified away");

var pinned = BezierNodeOptimizer.Reduce(
    new[] { CubicBezier.Line(new Vec2(0, 0), new Vec2(20, 0)),
            CubicBezier.Line(new Vec2(20, 0), new Vec2(40, 0)) },
    new HashSet<Vec2> { new Vec2(20, 0) }, 1.0);
Check(pinned.Count == 2,
    "Manually protected shared corner is never merged");

var beforeReduction = Tracer.Trace(StackedFixture(), new TraceOptions
{
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    CurveFitting = true, OptimizeNodes = false
});
var afterReduction = Tracer.Trace(StackedFixture(), new TraceOptions
{
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    CurveFitting = true, OptimizeNodes = true
});
Check(beforeReduction.Diagnostics.OptimizedChainNodes ==
      beforeReduction.Diagnostics.FittedChainNodes,
    "Disabled node optimizer leaves curves unmodified");
Check(afterReduction.Diagnostics.OptimizedChainNodes <=
      afterReduction.Diagnostics.FittedChainNodes,
    "Enabled node optimizer never increases segments");
Check(afterReduction.Document.Regions.Count ==
      beforeReduction.Document.Regions.Count,
    "Curve simplification cannot remove or duplicate regions");
Check(!SvgWriter.Write(afterReduction.Document).Contains("<g "),
    "Optimized SVG still contains no groups");


// Bounded global DP: choose curve intervals, preserving the original boundary.
// Tests use synthetic cubic geometry independent of raster segmentation.
static (CubicBezier A, CubicBezier B) Bisect(CubicBezier b)
{
    static Vec2 M(Vec2 a, Vec2 c) => (a + c) * 0.5;
    Vec2 p01 = M(b.P0, b.P1), p12 = M(b.P1, b.P2), p23 = M(b.P2, b.P3);
    Vec2 p012 = M(p01, p12), p123 = M(p12, p23), p = M(p012, p123);
    return (new CubicBezier(b.P0, p01, p012, p),
            new CubicBezier(p, p123, p23, b.P3));
}
var underlying = new CubicBezier(
    new Vec2(4, 4), new Vec2(26, 2),
    new Vec2(35, 44), new Vec2(64, 38));
var pieces = new List<CubicBezier> { underlying };
for (int level = 0; level < 3; level++)
{
    var divided = new List<CubicBezier>();
    foreach (var piece in pieces)
    {
        var pair = Bisect(piece);
        divided.Add(pair.A); divided.Add(pair.B);
    }
    pieces = divided;
}
var dpResult = GlobalBezierOptimizer.Reduce(pieces, null, 0.5);
Check(dpResult.Count == 1,
    "Global DP reconstitutes eight smooth cubic fragments into one");
Check(dpResult[0].P0 == underlying.P0 &&
      dpResult[0].P3 == underlying.P3,
    "Global DP keeps exact chain endpoints");
var dpAgain = GlobalBezierOptimizer.Reduce(pieces, null, 0.5);
Check(dpResult.SequenceEqual(dpAgain),
    "Global DP is deterministic");

var withCorner = new[]
{
    CubicBezier.Line(new Vec2(0, 0), new Vec2(30, 0)),
    CubicBezier.Line(new Vec2(30, 0), new Vec2(30, 30))
};
Check(GlobalBezierOptimizer.Reduce(withCorner, null, 0.8).Count == 2,
    "Multiscale corner guard keeps a genuine 90-degree tip");
Check(GlobalBezierOptimizer.Reduce(withCorner,
    new HashSet<Vec2> { new Vec2(30, 0) }, 0.8).Count == 2,
    "Global DP does not cross pinned corners");

var stairs = new List<CubicBezier>();
var jogs = new []
{
    new Vec2(0, 0), new Vec2(5, 0.10), new Vec2(10, -0.08),
    new Vec2(15, 0.12), new Vec2(20, -0.10),
    new Vec2(25, 0.08), new Vec2(30, 0)
};
for (int i = 1; i < jogs.Length; i++)
    stairs.Add(CubicBezier.Line(jogs[i - 1], jogs[i]));
var stairsReduced = GlobalBezierOptimizer.Reduce(stairs, null, 0.5);
Check(stairsReduced.Count < stairs.Count,
    "Global DP smooths low-amplitude micro-jogs across several L spans");

var noGlobal = Tracer.Trace(StackedFixture(), new TraceOptions
{
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    CurveFitting = true, OptimizeNodes = false
});
var withGlobal = Tracer.Trace(StackedFixture(), new TraceOptions
{
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    CurveFitting = true, OptimizeNodes = true
});
Check(withGlobal.Diagnostics.OptimizedChainNodes <=
      noGlobal.Diagnostics.FittedChainNodes,
    "Global simplification never increases total SVG curve segments");
Check(withGlobal.Document.Regions.Count == noGlobal.Document.Regions.Count &&
      !SvgWriter.Write(withGlobal.Document).Contains("<g "),
    "Global simplification leaves topology and ungrouped stacked paths intact");

var withoutRaster = Rasterizer.Render(noGlobal.Document, supersample: 1);
var withRaster = Rasterizer.Render(withGlobal.Document, supersample: 1);
int changedInFixture = 0;
for (int i = 0; i < withRaster.Pixels.Length; i += 4)
{
    if (withRaster.Pixels[i] != withoutRaster.Pixels[i] ||
        withRaster.Pixels[i + 1] != withoutRaster.Pixels[i + 1] ||
        withRaster.Pixels[i + 2] != withoutRaster.Pixels[i + 2] ||
        withRaster.Pixels[i + 3] != withoutRaster.Pixels[i + 3])
        changedInFixture++;
}
Check(changedInFixture <= 80,
    "Global optimizer stays within 2 percent raster change in nested fixture");


// Benchmark engine modes must be strictly separable without changing palette
// or topology, and production default must remain GlobalDp.
Check(new TraceOptions().BezierMode == BezierOptimizationMode.GlobalDp,
    "Production node optimization mode remains Global DP");
var modeFixture = StackedFixture();
var modeOff = Tracer.Trace(modeFixture, new TraceOptions {
    ShapeMode = ShapeMode.Planar, Style = ImageStyle.Crisp,
    OptimizeNodes = false, CurveFitting = true
});
var modePairwise = Tracer.Trace(modeFixture, new TraceOptions {
    ShapeMode = ShapeMode.Planar, Style = ImageStyle.Crisp,
    OptimizeNodes = true, BezierMode = BezierOptimizationMode.Pairwise
});
var modeGlobal = Tracer.Trace(modeFixture, new TraceOptions {
    ShapeMode = ShapeMode.Planar, Style = ImageStyle.Crisp,
    OptimizeNodes = true, BezierMode = BezierOptimizationMode.GlobalDp
});
var modeStacked = Tracer.Trace(modeFixture, new TraceOptions {
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    OptimizeNodes = true, BezierMode = BezierOptimizationMode.GlobalDp
});
Check(modeOff.Diagnostics.FittedChainNodes == modeOff.Diagnostics.OptimizedChainNodes,
    "Off benchmark uses unoptimized fitted chains");
Check(modePairwise.Diagnostics.GlobalReducedChains == 0,
    "Pairwise benchmark never invokes global DP");
Check(modePairwise.Diagnostics.OptimizedChainNodes <= modeOff.Diagnostics.FittedChainNodes,
    "Pairwise benchmark never adds chain segments");
Check(modeGlobal.Diagnostics.OptimizedChainNodes <= modePairwise.Diagnostics.OptimizedChainNodes,
    "Global DP benchmark never worsens pairwise segment count");
Check(modeOff.Document.Regions.Count == modePairwise.Document.Regions.Count &&
      modeOff.Document.Regions.Count == modeGlobal.Document.Regions.Count &&
      modeOff.Document.Regions.Count == modeStacked.Document.Regions.Count,
    "Four benchmark configurations preserve connected region count");
Check(modeOff.Document.Regions.Sum(x => x.Loops.Count) ==
      modePairwise.Document.Regions.Sum(x => x.Loops.Count) &&
      modeOff.Document.Regions.Sum(x => x.Loops.Count) ==
      modeGlobal.Document.Regions.Sum(x => x.Loops.Count),
    "Planar benchmark compares identical path topology");


// Sub-pixel sampling benchmark hooks are opt-in. Production defaults remain
// the same as before and are checked against the explicit 0.50 coverage.
var subPixelArt = StackedFixture();
var defaultSubpixel = Tracer.Trace(subPixelArt, new TraceOptions {
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    OptimizeNodes = true, BezierMode = BezierOptimizationMode.GlobalDp
});
var explicitSubpixel = Tracer.Trace(subPixelArt, new TraceOptions {
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    OptimizeNodes = true, BezierMode = BezierOptimizationMode.GlobalDp,
    EdgeCoverageThreshold = .5, EdgeReferenceImage = subPixelArt
});
Check(SvgWriter.Write(defaultSubpixel.Document) == SvgWriter.Write(explicitSubpixel.Document),
    "Default edge sampler is bit-identical to explicit coverage=0.50 on same raster");
var shiftedCoverage = Tracer.Trace(subPixelArt, new TraceOptions {
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    OptimizeNodes = true, EdgeCoverageThreshold = .46
});
Check(shiftedCoverage.Document.Regions.Count == defaultSubpixel.Document.Regions.Count,
    "Coverage change cannot create or delete connected regions");
bool edgeDimensionRejected = false;
try {
    Tracer.Trace(subPixelArt, new TraceOptions {
        EdgeReferenceImage = new RasterImage(24, 24)
    });
} catch (ArgumentException) { edgeDimensionRejected = true; }
Check(edgeDimensionRejected, "Edge source must match target raster dimensions");


// Local Edge Refinement V3 is opt-in and never moves protected sharp tips,
// shared-chain endpoints, or low-contrast features.
Check(!new TraceOptions().LocalEdgeRefinementV3,
    "Local Edge V3 is disabled by default (production remains identical)");
var localSource = new RasterImage(32, 20);
for (int y = 0; y < localSource.Height; y++)
for (int x = 0; x < localSource.Width; x++)
    localSource.SetPixel(x, y, y < 8
        ? new Rgba32(250, 250, 250, 255)
        : new Rgba32(5, 5, 5, 255));
List<Vec2> Horizontal() => Enumerable.Range(0, 12)
    .Select(x => new Vec2(x + 5, 8.25)).ToList();
var v3Points = Horizontal();
var beforeV3 = v3Points.ToArray();
int adjusted = LocalEdgeRefiner.Refine(v3Points, false, new[] { 3 },
    localSource, new Rgba32(250, 250, 250, 255),
    new Rgba32(5, 5, 5, 255), .20);
Check(adjusted > 0 && v3Points[7].Y < beforeV3[7].Y &&
      v3Points[7].Y >= beforeV3[7].Y - .20 - 1e-9,
    "V3 reprojects high-confidence flat edge toward 50% coverage within shift cap");
Check(v3Points[3] == beforeV3[3] &&
      v3Points[2] == beforeV3[2] &&
      v3Points[4] == beforeV3[4] &&
      v3Points[0] == beforeV3[0] &&
      v3Points[^1] == beforeV3[^1],
    "V3 keeps sharp pins, adjacent shoulders, and chain endpoints unchanged");
var v3Repeat = Horizontal();
Check(LocalEdgeRefiner.Refine(v3Repeat, false, new[] { 3 },
      localSource, new Rgba32(250, 250, 250, 255),
      new Rgba32(5, 5, 5, 255), .20) == adjusted &&
      v3Points.SequenceEqual(v3Repeat),
    "V3 local adjustments are deterministic");
var lowContrast = Horizontal();
Check(LocalEdgeRefiner.Refine(lowContrast, false, Array.Empty<int>(),
      localSource, new Rgba32(250, 250, 250, 255),
      new Rgba32(246, 246, 246, 255), .20) == 0 &&
      lowContrast.SequenceEqual(beforeV3),
    "V3 skips low-contrast and ambiguous boundaries");
var baselineV3Off = Tracer.Trace(StackedFixture(), new TraceOptions
{
    Style = ImageStyle.Crisp, ShapeMode = ShapeMode.Stacked,
    LocalEdgeRefinementV3 = false
});
var baselineV3Default = Tracer.Trace(StackedFixture(), new TraceOptions
{
    Style = ImageStyle.Crisp, ShapeMode = ShapeMode.Stacked
});
Check(SvgWriter.Write(baselineV3Off.Document) ==
      SvgWriter.Write(baselineV3Default.Document),
    "V3 opt-in never changes production baseline SVG");


// Shared-boundary orientation: reversing the contour and exchanging left/right
// paint colors must yield the same refined geometry in reverse order.
var reverseTest = Horizontal();
int countForward = LocalEdgeRefiner.Refine(reverseTest, false,
    Array.Empty<int>(), localSource,
    new Rgba32(250, 250, 250, 255),
    new Rgba32(5, 5, 5, 255), .20);
var reverseInput = Horizontal();
reverseInput.Reverse();
int countBackward = LocalEdgeRefiner.Refine(reverseInput, false,
    Array.Empty<int>(), localSource,
    new Rgba32(5, 5, 5, 255),
    new Rgba32(250, 250, 250, 255), .20);
reverseInput.Reverse();
Check(countForward == countBackward &&
    reverseInput.Zip(reverseTest).All(p => p.First.DistSq(p.Second) < 1e-18),
    "V3 is orientation-independent when a shared edge is reversed");
Check(LocalEdgeRefiner.Refine(Horizontal(), false, Array.Empty<int>(),
    localSource, null, new Rgba32(5, 5, 5, 255), .20) == 0,
    "V3 preserves real transparent-side boundaries");


// SVG output metadata must describe the exact path instructions that SvgWriter emits.
var statsDoc = new VectorDocument
{
    Width = 20,
    Height = 10,
    ShapeMode = ShapeMode.Planar,
    Palette = new List<PaletteEntry>
    {
        new() { Color = black, PixelCount = 99 }
    },
    Regions = new List<RegionPath>
    {
        new()
        {
            PaletteIndex = 0,
            Area = 99,
            Loops = new List<List<CubicBezier>>
            {
                new()
                {
                    CubicBezier.Line(new Vec2(1, 1), new Vec2(10, 1)),
                    new CubicBezier(new Vec2(10, 1), new Vec2(10, 4),
                        new Vec2(1, 7), new Vec2(1, 1))
                }
            }
        }
    }
};
var statsSvg = SvgWriter.Write(statsDoc);
var outputStats = SvgOutputStatistics.Analyze(statsDoc, statsSvg);
Check(outputStats.Width == 20 && outputStats.Height == 10 &&
    outputStats.ViewBox == "0 0 20 10" && outputStats.PathCount == 1 &&
    outputStats.SubpathCount == 1 && outputStats.TotalSegments == 2 &&
    outputStats.LineCommands == 1 && outputStats.CubicCommands == 1 &&
    outputStats.EmittedAnchors == 2 && outputStats.CubicControlPoints == 2,
    "SVG statistics count actual emitted paths, subpaths, lines and curves");
Check(outputStats.Colors.Count == 1 &&
    outputStats.Colors[0].Hex == "#0c0b0a" &&
    outputStats.Colors[0].RegionPaths == 1 &&
    outputStats.Colors[0].PalettePixelCount == 99 &&
    outputStats.FileBytes == System.Text.Encoding.UTF8.GetByteCount(statsSvg) &&
    outputStats.ApproximateOutlineLength > 18,
    "SVG stats preserve color, UTF-8 file size and approximate curve length");
var emptyDoc = new VectorDocument
{
    Width = 1, Height = 1,
    Palette = new List<PaletteEntry>(),
    Regions = new List<RegionPath>()
};
var emptyStats = SvgOutputStatistics.Analyze(emptyDoc, SvgWriter.Write(emptyDoc));
Check(emptyStats.PathCount == 0 && emptyStats.SubpathCount == 0 &&
    emptyStats.TotalSegments == 0 && emptyStats.MinSegmentsPerPath == 0 &&
    emptyStats.AverageSegmentsPerPath == 0 && emptyStats.Colors.Count == 0,
    "Empty SVG metadata does not divide by zero or invent paths");


// Region Merge V1: benchmark on an intentionally fragmented but softly coloured
// component. This option must not affect the production trace unless enabled.
Check(!new TraceOptions().RegionMergeV1,
    "Source-aware region merge remains opt-in / disabled by default");
var softImage = new RasterImage(64, 64);
for (int y = 0; y < 64; y++)
    for (int x = 0; x < 64; x++)
        softImage.SetPixel(x, y, new Rgba32(108, 108, 108, 255));
var softLabels = new int[64 * 64]; // background label 0
for (int y = 30; y < 32; y++)
    for (int x = 30; x < 32; x++)
        softLabels[y * 64 + x] = 1; // mislabeled patch, source still background
var softPalette = new List<PaletteEntry>
{
    new() { Color = new Rgba32(108, 108, 108, 255), PixelCount = 4092 },
    new() { Color = new Rgba32(118, 118, 118, 255), PixelCount = 4 }
};
var softCopy = (int[])softLabels.Clone();
var mergedSoft = RegionMergeRefiner.Refine(softImage, softLabels, softPalette);
Check(mergedSoft.RegionsBefore == 2 && mergedSoft.RegionsAfter == 1
      && mergedSoft.MergedRegions == 1 && mergedSoft.RelabeledPixels == 4
      && softLabels.All(label => label == 0),
    "Opt-in region merge absorbs source-supported fragmented 2x2 region");
var softAgain = RegionMergeRefiner.Refine(softImage, softCopy, softPalette);
Check(softAgain == mergedSoft && softCopy.SequenceEqual(softLabels),
    "Region merge deterministic across identical inputs");

var eye = new RasterImage(64, 64);
var eyeLabels = new int[64 * 64];
for (int y = 0; y < 64; y++)
    for (int x = 0; x < 64; x++)
        eye.SetPixel(x, y, new Rgba32(255, 255, 255, 255));
for (int y = 30; y < 32; y++)
    for (int x = 30; x < 32; x++)
    {
        eye.SetPixel(x, y, new Rgba32(250, 214, 15, 255));
        eyeLabels[y * 64 + x] = 1;
    }
var eyePalette = new List<PaletteEntry>
{
    new() { Color = new Rgba32(255, 255, 255, 255), PixelCount = 4092 },
    new() { Color = new Rgba32(250, 214, 15, 255), PixelCount = 4 }
};
var eyeBefore = (int[])eyeLabels.Clone();
var eyeStats = RegionMergeRefiner.Refine(eye, eyeLabels, eyePalette);
Check(eyeStats.RelabeledPixels == 0 && eyeLabels.SequenceEqual(eyeBefore),
    "Source-supported tiny yellow eye detail survives region merge");

var stroke = new RasterImage(64, 64);
var strokeLabels = new int[64 * 64];
for (int y = 0; y < 64; y++)
    for (int x = 0; x < 64; x++)
        stroke.SetPixel(x, y, new Rgba32(150, 150, 150, 255));
for (int x = 28; x < 38; x++)
{
    stroke.SetPixel(x, 28, new Rgba32(140, 140, 140, 255));
    strokeLabels[28 * 64 + x] = 1;
}
var strokePalette = new List<PaletteEntry>
{
    new() { Color = new Rgba32(150, 150, 150, 255), PixelCount = 4086 },
    new() { Color = new Rgba32(140, 140, 140, 255), PixelCount = 10 }
};
var strokeBefore = (int[])strokeLabels.Clone();
var strokeStats = RegionMergeRefiner.Refine(stroke, strokeLabels, strokePalette);
Check(strokeStats.RelabeledPixels == 0 && strokeLabels.SequenceEqual(strokeBefore),
    "Long thin intentional gray stroke is protected from region merge");

var semiVoid = new RasterImage(64, 64);
var semiLabels = new int[64 * 64];
Array.Fill(semiLabels, -1);
for (int y = 10; y < 32; y++)
    for (int x = 10; x < 32; x++)
    {
        semiVoid.SetPixel(x, y, new Rgba32(108, 108, 108, 255));
        semiLabels[y * 64 + x] = 0;
    }
for (int y = 32; y < 34; y++)
    for (int x = 20; x < 22; x++)
    {
        semiVoid.SetPixel(x, y, new Rgba32(108, 108, 108, 255));
        semiLabels[y * 64 + x] = 1;
    }
var semiBefore = (int[])semiLabels.Clone();
var transparencyStats = RegionMergeRefiner.Refine(semiVoid, semiLabels, softPalette);
Check(transparencyStats.RelabeledPixels == 0 && semiLabels.SequenceEqual(semiBefore)
      && semiLabels.Count(l => l == -1) == semiBefore.Count(l => l == -1),
    "Region merge preserves alpha-adjacent islands and transparent holes");

var defaultsForMerge = Tracer.Trace(StackedFixture(), StackingOptions(ShapeMode.Stacked));
var explicitMergeOff = Tracer.Trace(StackedFixture(), new TraceOptions
{
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    CurveFitting = false, PolygonEpsilon = 0,
    RegionMergeV1 = false
});
Check(SvgWriter.Write(defaultsForMerge.Document) ==
      SvgWriter.Write(explicitMergeOff.Document),
    "Opt-in region merge keeps existing stacked SVG byte-identical by default");
var mergeFixture = Tracer.Trace(StackedFixture(), new TraceOptions
{
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    CurveFitting = false, PolygonEpsilon = 0,
    RegionMergeV1 = true
});
Check(Rasterizer.Render(defaultsForMerge.Document, 1).Pixels.SequenceEqual(
        Rasterizer.Render(mergeFixture.Document, 1).Pixels) &&
    mergeFixture.Diagnostics.RegionMergeV1ChangedPixels == 0,
    "Deliberate sharp nested logo stays raster-identical with merge opt-in");


// Priority #2: topology-safe region graph. Never erase a 2x2 fringe component
// when it touches a third distinct color at a T-junction.
var junctionImage = new RasterImage(64, 64);
var junctionLabels = new int[64 * 64];
for (int y = 0; y < 64; y++)
for (int x = 0; x < 64; x++)
    junctionImage.SetPixel(x, y, new Rgba32(108, 108, 108, 255));
for (int y = 30; y < 32; y++)
for (int x = 30; x < 32; x++)
    junctionLabels[y * 64 + x] = 1;
junctionImage.SetPixel(32, 30, new Rgba32(250, 214, 15, 255));
junctionLabels[30 * 64 + 32] = 2;
var junctionPalette = new List<PaletteEntry>
{
    new() { Color = new Rgba32(108, 108, 108, 255), PixelCount = 4091 },
    new() { Color = new Rgba32(118, 118, 118, 255), PixelCount = 4 },
    new() { Color = new Rgba32(250, 214, 15, 255), PixelCount = 1 }
};
var junctionBefore = (int[])junctionLabels.Clone();
var junctionAudit = RegionMergeRefiner.Refine(junctionImage, junctionLabels, junctionPalette);
Check(junctionAudit.RelabeledPixels == 0 &&
      junctionAudit.TopologyRejectedRegions >= 1 &&
      junctionLabels.SequenceEqual(junctionBefore),
    "Topology guard protects tiny component touching a third-color T-junction");

// A third-color island may be diagonal to a fringe component, without any
// direct 4-connected border. Changing the fringe can still alter the graph
// degree at the shared pixel-corner, so protect checkerboard junctions.
var diagonalImage = new RasterImage(64, 64);
var diagonalLabels = new int[64 * 64];
for (int y = 0; y < 64; y++)
for (int x = 0; x < 64; x++)
    diagonalImage.SetPixel(x, y, new Rgba32(108, 108, 108, 255));
for (int y = 30; y < 32; y++)
for (int x = 30; x < 32; x++)
    diagonalLabels[y * 64 + x] = 1;
diagonalImage.SetPixel(29, 29, new Rgba32(250, 214, 15, 255));
diagonalLabels[29 * 64 + 29] = 2;
var diagonalBefore = (int[])diagonalLabels.Clone();
var diagonalAudit = RegionMergeRefiner.Refine(diagonalImage, diagonalLabels, junctionPalette);
Check(diagonalAudit.RelabeledPixels == 0 &&
      diagonalAudit.TopologyRejectedRegions >= 1 &&
      diagonalLabels.SequenceEqual(diagonalBefore),
    "Diagonal third-color pixel-corner junction is not bridged by a merge");

// Explicitly exercise the boundary validator on a multi-color 4-way junction.
// All four quadrants are independent graph regions; the shared cracks around
// the central crossing must be assigned to two sides exactly once.
var fourWayImage = new RasterImage(64, 64);
for (int y = 0; y < 64; y++)
for (int x = 0; x < 64; x++)
{
    var color = (x < 32, y < 32) switch
    {
        (true, true) => new Rgba32(255, 255, 255, 255),
        (false, true) => new Rgba32(8, 8, 8, 255),
        (true, false) => new Rgba32(235, 107, 45, 255),
        _ => new Rgba32(30, 143, 210, 255)
    };
    fourWayImage.SetPixel(x, y, color);
}
var fourWayOptions = new TraceOptions
{
    Style = ImageStyle.Crisp,
    ShapeMode = ShapeMode.Planar,
    Detail = DetailLevel.High,
    CurveFitting = false,
    PolygonEpsilon = 0,
    RegionMergeV1 = true
};
var fourWayTrace = Tracer.Trace(fourWayImage, fourWayOptions);
Check(fourWayTrace.Diagnostics.RegionMergeV1VerifiedCracks > 0 &&
      fourWayTrace.Diagnostics.RegionMergeV1VerifiedContours >= 4 &&
      fourWayTrace.Diagnostics.RegionMergeV1ChangedPixels == 0,
    "Boundary validator verifies every shared crack and four-way closed contour");
Check(fourWayTrace.Document.Regions.Count == 4,
    "Four-way junction keeps four independently editable color regions");
var fourWayStacked = Tracer.Trace(fourWayImage, new TraceOptions
{
    Style = ImageStyle.Crisp,
    ShapeMode = ShapeMode.Stacked,
    Detail = DetailLevel.High,
    CurveFitting = false,
    PolygonEpsilon = 0,
    RegionMergeV1 = true
});
Check(fourWayStacked.Diagnostics.RegionMergeV1VerifiedCracks ==
      fourWayTrace.Diagnostics.RegionMergeV1VerifiedCracks &&
      fourWayStacked.Document.Regions.Count == 4,
    "Planar and Stacked share the same verified topology at four-way junction");

var fourWayRendered = Rasterizer.Render(fourWayTrace.Document, supersample: 1);
var fourWayStackedRendered = Rasterizer.Render(fourWayStacked.Document, supersample: 1);
Check(fourWayRendered.Pixels.SequenceEqual(fourWayStackedRendered.Pixels),
    "Four-way planar/stacked outputs remain exactly equivalent as pixels");
Check(fourWayRendered.Pixels.SequenceEqual(fourWayImage.Pixels),
    "Exact-palette four-way graph keeps every input pixel without holes or overlaps");

var transparentWithGuard = Tracer.Trace(transparentRing, new TraceOptions
{
    Style = ImageStyle.Crisp,
    ShapeMode = ShapeMode.Stacked,
    Detail = DetailLevel.High,
    CurveFitting = false,
    PolygonEpsilon = 0,
    RegionMergeV1 = true
});
Check(Rasterizer.Render(transparentWithGuard.Document, supersample: 1)
        .GetPixel(20, 20).A == 0 &&
      transparentWithGuard.Diagnostics.RegionMergeV1VerifiedContours >= 2,
    "Topology guard validates contour sides while preserving genuine transparent holes");


// Priority 3: stacked-shape reasoning is opt-in and must preserve true layered
// visibility, rather than simply converting the entire palette into one path.
Check(!new TraceOptions().StackedLayerReasoningV1,
    "Stacked layer reasoning stays OFF in the production defaults");

// A large pale background covers all pixels, with four disjoint red/blue
// motifs. They should remain independent semantic shapes but share a smaller
// number of same-paint compound <path> layers when no occlusion conflicts.
var layeredFixture = new RasterImage(112, 72);
var paper = new Rgba32(252, 252, 252, 255);
var vermilion = new Rgba32(228, 80, 35, 255);
var cobalt = new Rgba32(16, 68, 150, 255);
var charcoal = new Rgba32(18, 18, 20, 255);
for (int y = 0; y < layeredFixture.Height; y++)
for (int x = 0; x < layeredFixture.Width; x++)
{
    var color = paper;
    if (y >= 14 && y < 31 &&
        ((x >= 8 && x < 23) || (x >= 30 && x < 45) ||
         (x >= 52 && x < 67) || (x >= 74 && x < 89)))
        color = vermilion;
    if (y >= 40 && y < 57 && x >= 30 && x < 45)
        color = cobalt;
    if (y >= 19 && y < 23 && x >= 11 && x < 15)
        color = charcoal; // deliberate dark detail over first orange shape
    layeredFixture.SetPixel(x, y, color);
}
TraceOptions LayerOptions(bool experimental) => new()
{
    ShapeMode = ShapeMode.Stacked,
    Style = ImageStyle.Crisp,
    Detail = DetailLevel.High,
    CurveFitting = false,
    PolygonEpsilon = 0,
    StackedLayerReasoningV1 = experimental
};
var layerBaseline = Tracer.Trace(layeredFixture, LayerOptions(false));
var layerReasoned = Tracer.Trace(layeredFixture, LayerOptions(true));
Check(layerReasoned.Diagnostics.StackedLayers.BaselinePaths ==
      layerBaseline.Document.Regions.Count &&
      layerReasoned.Diagnostics.StackedLayers.CandidatePaths <=
      layerReasoned.Diagnostics.StackedLayers.BaselinePaths &&
      layerReasoned.Diagnostics.StackedLayers.OcclusionDependencies > 0,
    "Layer reasoning measures a real stacked dependency graph and compacted candidate");
Check(!layerReasoned.Diagnostics.StackedLayers.Reverted &&
      layerReasoned.Diagnostics.StackedLayers.PathsSaved >= 2 &&
      layerReasoned.Document.Regions.Count <
      layerBaseline.Document.Regions.Count &&
      layerReasoned.Diagnostics.StackedLayers.PixelMismatches == 0,
    "Occlusion-safe stacked path grouping reduces paths without raster mismatch");
Check(Rasterizer.Render(layerBaseline.Document, supersample: 1).Pixels
      .SequenceEqual(Rasterizer.Render(layerReasoned.Document, supersample: 1).Pixels) &&
      Rasterizer.Render(layerBaseline.Document, supersample: 2).Pixels
      .SequenceEqual(Rasterizer.Render(layerReasoned.Document, supersample: 2).Pixels),
    "Packed stacked paths keep pixel-identical 1x and 2x RGBA output");
Check(!SvgWriter.Write(layerReasoned.Document).Contains("<g ") &&
      SvgWriter.Write(layerReasoned.Document).Split("<path ",
          StringSplitOptions.None).Length - 1 == layerReasoned.Document.Regions.Count,
    "Layer reasoning emits direct editable SVG compound paths without groups");
var layerReasonedAgain = Tracer.Trace(layeredFixture, LayerOptions(true));
Check(SvgWriter.Write(layerReasonedAgain.Document) ==
      SvgWriter.Write(layerReasoned.Document) &&
      layerReasonedAgain.Diagnostics.StackedLayers ==
      layerReasoned.Diagnostics.StackedLayers,
    "Stacked layer scheduling and verification are deterministic");

// Feature OFF means byte-for-byte equivalence to the original Stacked route.
Check(SvgWriter.Write(layerBaseline.Document) ==
      SvgWriter.Write(Tracer.Trace(layeredFixture, new TraceOptions
      {
          ShapeMode = ShapeMode.Stacked,
          Style = ImageStyle.Crisp,
          Detail = DetailLevel.High,
          CurveFitting = false,
          PolygonEpsilon = 0
      }).Document),
    "Default stacked output remains exactly unchanged");

// A stacked-logic flag on Planar must never re-order or collapse planar paths.
var planarWithFlag = Tracer.Trace(layeredFixture, new TraceOptions
{
    ShapeMode = ShapeMode.Planar,
    Style = ImageStyle.Crisp,
    Detail = DetailLevel.High,
    CurveFitting = false,
    PolygonEpsilon = 0,
    StackedLayerReasoningV1 = true
});
var planarWithoutFlag = Tracer.Trace(layeredFixture, new TraceOptions
{
    ShapeMode = ShapeMode.Planar,
    Style = ImageStyle.Crisp,
    Detail = DetailLevel.High,
    CurveFitting = false,
    PolygonEpsilon = 0
});
Check(SvgWriter.Write(planarWithFlag.Document) ==
      SvgWriter.Write(planarWithoutFlag.Document) &&
      planarWithFlag.Diagnostics.StackedLayers.BaselinePaths == 0,
    "Planar mode ignores experimental stacked-only layer reasoning");

// Transparency and corner-junction regression with advanced Stacked opt-in.
var seeThroughLayers = Tracer.Trace(transparentRing, new TraceOptions
{
    Style = ImageStyle.Crisp,
    ShapeMode = ShapeMode.Stacked,
    Detail = DetailLevel.High,
    CurveFitting = false, PolygonEpsilon = 0,
    StackedLayerReasoningV1 = true
});
Check(Rasterizer.Render(seeThroughLayers.Document, supersample: 1)
        .GetPixel(20, 20).A == 0 &&
      Rasterizer.Render(seeThroughLayers.Document, supersample: 2).Pixels
        .SequenceEqual(Rasterizer.Render(transparentStack.Document,
            supersample: 2).Pixels),
    "Layer packing preserves intentional transparent holes and cutouts");
var fourWayLayered = Tracer.Trace(fourWayImage, new TraceOptions
{
    ShapeMode = ShapeMode.Stacked, Style = ImageStyle.Crisp,
    Detail = DetailLevel.High, CurveFitting = false, PolygonEpsilon = 0,
    RegionMergeV1 = true, StackedLayerReasoningV1 = true
});
Check(Rasterizer.Render(fourWayLayered.Document, supersample: 1).Pixels
        .SequenceEqual(Rasterizer.Render(fourWayStacked.Document,
            supersample: 1).Pixels) &&
      fourWayLayered.Diagnostics.RegionMergeV1VerifiedCracks ==
      fourWayStacked.Diagnostics.RegionMergeV1VerifiedCracks,
    "Region Merge V1 topology guard remains compatible with Stacked V1 layering");
