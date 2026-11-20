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
