using Vecto.Core;

// Independent test harness: no NuGet test runner or OS-specific resources.
static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

static bool Close(Vec2 a, Vec2 b) => a.DistSq(b) < 1e-10;
static bool Finite(CubicBezier c) =>
    double.IsFinite(c.P0.X) && double.IsFinite(c.P0.Y) &&
    double.IsFinite(c.P1.X) && double.IsFinite(c.P1.Y) &&
    double.IsFinite(c.P2.X) && double.IsFinite(c.P2.Y) &&
    double.IsFinite(c.P3.X) && double.IsFinite(c.P3.Y);

var line = Enumerable.Range(0, 26)
    .Select(i => new Vec2(i * 3, 20)).ToList();
var pristine = line.ToArray();
var lineResult = EkstraceV1Fitter.FitChain(line, false, new List<int>(), 0.25);
Check(lineResult.Count == 1 && lineResult[0].IsLine(),
    "Collinear points remain one line");
Check(line.SequenceEqual(pristine), "Source chain is not mutated");

var corner = new List<Vec2>();
for (int i = 0; i <= 16; i++) corner.Add(new Vec2(i * 4, 12));
for (int i = 1; i <= 16; i++) corner.Add(new Vec2(64, 12 + i * 4));
var rightAngle = EkstraceV1Fitter.FitChain(corner, false, new List<int> { 16 }, 0.25);
Check(rightAngle.Count >= 2 && rightAngle.Count <= 6,
    "Protected right-angle corner stays a split");
Check(rightAngle.Any(c => Close(c.P3, corner[16])) &&
      rightAngle.First().P0 == corner[0] &&
      rightAngle.Last().P3 == corner[^1],
    "Corner and boundary endpoints remain exact");

const int n = 128;
var ring = Enumerable.Range(0, n).Select(i =>
{
    double theta = 2 * Math.PI * i / n;
    return new Vec2(100 + 70 * Math.Cos(theta), 100 + 70 * Math.Sin(theta));
}).ToList();
ring.Add(ring[0]);
var circles = EkstraceV1Fitter.FitChain(ring, true, new List<int>(), 0.25);
Check(circles.Count >= 4 && circles.Count < n, "Closed circle fitted with bounded curve count");
Check(Close(circles[0].P0, circles[^1].P3) &&
      Enumerable.Range(0, circles.Count - 1).All(i => Close(circles[i].P3, circles[i + 1].P0)),
    "Closed chain and every internal join are connected");
Check(circles.All(Finite), "No non-finite Bézier control points");
for (int i = 0; i < circles.Count; i++)
{
    var a = circles[i];
    var b = circles[(i + 1) % circles.Count];
    Vec2 va = a.P3 - a.P2, vb = b.P1 - b.P0;
    if (va.Length > 1e-6 && vb.Length > 1e-6)
    {
        double cross = Math.Abs(va.X * vb.Y - va.Y * vb.X);
        Check(cross / (va.Length * vb.Length) < 1e-7 &&
              va.Dot(vb) > 0, "Smooth joins preserve tangent direction (G1)");
    }
}

var original = new RasterImage(48, 48);
for (int y = 0; y < original.Height; y++)
    for (int x = 0; x < original.Width; x++)
        original.SetPixel(x, y, x >= 8 && x <= 39 && y >= 8 && y <= 39
            ? new Rgba32(16, 130, 81, 255) : new Rgba32(250, 250, 250, 255));
var options = new TraceOptions
{
    Style = ImageStyle.Crisp,
    Detail = DetailLevel.Medium,
    PaletteMode = PaletteMode.FixedCount,
    ColorCount = 2,
    OptimizeNodes = false,
    ContourFittingEngine = ContourFittingEngine.Ekstracev1
};
var trace = Tracer.Trace(original, options);
var svg = SvgWriter.Write(trace.Document);
Check(svg.Contains("<path", StringComparison.OrdinalIgnoreCase),
    "Raster -> shared boundaries -> Ekstracev1 -> SVG works");
Check(!svg.Contains("<image", StringComparison.OrdinalIgnoreCase),
    "SVG uses vector paths rather than embedded raster");
Check(trace.Diagnostics.FittedChainNodes > 0,
    "Ekstracev1 participates in the actual tracing pipeline");
var second = SvgWriter.Write(Tracer.Trace(original, options).Document);
Check(svg == second, "Ekstracev1 raster trace is deterministic");
var old = Tracer.Trace(original, new TraceOptions
{
    Style = ImageStyle.Crisp, Detail = DetailLevel.Medium,
    PaletteMode = PaletteMode.FixedCount, ColorCount = 2,
    ContourFittingEngine = ContourFittingEngine.Original
});
Check(old.Document.Regions.Count == trace.Document.Regions.Count,
    "Original and Ekstracev1 preserve region counts");
// Prove the selected algorithm does not silently produce the original SVG
// on every curved raster. Straight edges are intentionally allowed to match.
static RasterImage CurvedRaster(bool wavy)
{
    var raster = new RasterImage(160, 160);
    for (int y = 0; y < raster.Height; y++)
        for (int x = 0; x < raster.Width; x++)
        {
            double dx = x - 80, dy = y - 80;
            double a = Math.Atan2(dy, dx);
            double radius = wavy ? 47 + 9 * Math.Sin(5 * a) : 48;
            bool inside = wavy
                ? Math.Sqrt(dx * dx + dy * dy) < radius
                : dx * dx / (58.0 * 58) + dy * dy / (40.0 * 40) < 1;
            raster.SetPixel(x, y, inside
                ? new Rgba32(19, 120, 86, 255)
                : new Rgba32(251, 251, 251, 255));
        }
    return raster;
}
int distinctCurvedFixtures = 0;
foreach (bool wavy in new[] { false, true })
{
    var img = CurvedRaster(wavy);
    var originalOptions = new TraceOptions
    {
        Style = ImageStyle.Crisp, Detail = DetailLevel.Medium,
        PaletteMode = PaletteMode.FixedCount, ColorCount = 2,
        OptimizeNodes = false, ContourFittingEngine = ContourFittingEngine.Original
    };
    var experimentalOptions = new TraceOptions
    {
        Style = ImageStyle.Crisp, Detail = DetailLevel.Medium,
        PaletteMode = PaletteMode.FixedCount, ColorCount = 2,
        OptimizeNodes = false, ContourFittingEngine = ContourFittingEngine.Ekstracev1
    };
    var traceOriginal = Tracer.Trace(img, originalOptions);
    var traceEkstrace = Tracer.Trace(img, experimentalOptions);
    string originalSvg = SvgWriter.Write(traceOriginal.Document);
    string eksperimenSvg = SvgWriter.Write(traceEkstrace.Document);
    Console.WriteLine($"Curved fixture {(wavy ? "wavy" : "ellipse")}: " +
        $"byte_identical={originalSvg == eksperimenSvg}, " +
        $"original_segments={traceOriginal.Diagnostics.FittedChainNodes}, " +
        $"ekstrace_segments={traceEkstrace.Diagnostics.FittedChainNodes}, " +
        $"ekstrace_chains={traceEkstrace.Diagnostics.EkstraceV1Chains}, " +
        $"fallback={traceEkstrace.Diagnostics.EkstraceV1OriginalFallbackChains}, " +
        $"curved={traceEkstrace.Diagnostics.EkstraceV1CurvedChains}");
    Check(traceEkstrace.Diagnostics.EkstraceV1Chains > 0,
        "Ekstrace actual raster dispatch reached fitter");
    Check(traceOriginal.Diagnostics.EkstraceV1Chains == 0,
        "Original mode never records experimental fitter");
    if (!string.Equals(originalSvg, eksperimenSvg, StringComparison.Ordinal))
        distinctCurvedFixtures++;
}
Check(distinctCurvedFixtures > 0,
    "Ekstracev1 differs from Original on at least one curved raster SVG");
var bigChain = Enumerable.Range(0, 4100).Select(x => new Vec2(x, 10)).ToList();
_ = EkstraceV1Fitter.FitChain(bigChain, false, new List<int>(), 0.25, out bool usedFallback);
Check(usedFallback, "Large chain fallback is explicitly reported");

// Stage 7: independently verify inverse unit-pixel area, colour unmixing,
// bounded correction, corner locking, and Original-mode isolation.
foreach (var normal in new[]
{
    new Vec2(1, 0), new Vec2(0, 1),
    new Vec2(.6, .8), new Vec2(Math.Sqrt(.5), Math.Sqrt(.5))
})
{
    double h = (Math.Abs(normal.X) + Math.Abs(normal.Y)) / 2;
    for (int k = -18; k <= 18; k++)
    {
        double d = h * k / 20;
        double alpha = EkstraceInverseArea.PixelCoverage(d, normal.X, normal.Y);
        double recovered = EkstraceInverseArea.SignedDistance(alpha, normal.X, normal.Y);
        Check(Math.Abs(recovered - d) < 1e-9,
            "Inverse pixel-square area roundtrip");
    }
}
Check(Math.Abs(EkstraceInverseArea.SrgbToLinear(0)) < 1e-10 &&
      Math.Abs(EkstraceInverseArea.SrgbToLinear(255) - 1) < 1e-10,
    "Exact sRGB transfer endpoints");

var fg = new Rgba32(38, 157, 70, 255);
var bg = new Rgba32(240, 240, 240, 255);
static Rgba32 MixSrgb(Rgba32 a, Rgba32 b, double fraction) =>
    new((byte)Math.Round(a.R * fraction + b.R * (1 - fraction)),
        (byte)Math.Round(a.G * fraction + b.G * (1 - fraction)),
        (byte)Math.Round(a.B * fraction + b.B * (1 - fraction)), 255);
var mixed = MixSrgb(fg, bg, .37);
var projected = EkstraceInverseArea.Project(mixed, fg, bg, false);
Check(Math.Abs(projected.Alpha - .37) < .01 &&
      projected.RelativeResidual < .001,
    "sRGB colour projection recovers coverage");

static byte FromLinear(double v)
{
    v = Math.Clamp(v, 0, 1);
    double srgb = v <= .0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - .055;
    return (byte)Math.Round(Math.Clamp(srgb * 255, 0, 255));
}
var inkA = new Rgba32(31, 197, 45, 255);
var inkB = new Rgba32(247, 49, 215, 255);
var linearMix = new Rgba32(
    FromLinear(.4 * EkstraceInverseArea.SrgbToLinear(inkA.R) +
                  .6 * EkstraceInverseArea.SrgbToLinear(inkB.R)),
    FromLinear(.4 * EkstraceInverseArea.SrgbToLinear(inkA.G) +
                  .6 * EkstraceInverseArea.SrgbToLinear(inkB.G)),
    FromLinear(.4 * EkstraceInverseArea.SrgbToLinear(inkA.B) +
                  .6 * EkstraceInverseArea.SrgbToLinear(inkB.B)), 255);
var linearPrediction = EkstraceInverseArea.Project(linearMix, inkA, inkB, true);
var srgbPrediction = EkstraceInverseArea.Project(linearMix, inkA, inkB, false);
Check(Math.Abs(linearPrediction.Alpha - .4) < .02 &&
      linearPrediction.RelativeResidual < srgbPrediction.RelativeResidual,
    "Linear-light colour projection favoured for linear blends");

// Supersampling in this fixture is deliberately independent of
// EkstraceInverseArea.PixelCoverage, so this is not a tautological test.
const int fixtureSize = 64;
const int supersamples = 20;
Vec2 lineNormal = new(.6, .8), lineTangent = new(-.8, .6);
Vec2 lineOrigin = new(32, 32);
var antialiased = new RasterImage(fixtureSize, fixtureSize);
for (int y = 0; y < fixtureSize; y++)
for (int x = 0; x < fixtureSize; x++)
{
    int positive = 0;
    for (int sy = 0; sy < supersamples; sy++)
    for (int sx = 0; sx < supersamples; sx++)
    {
        var delta = new Vec2(x + (sx + .5) / supersamples - lineOrigin.X,
                             y + (sy + .5) / supersamples - lineOrigin.Y);
        if (delta.Dot(lineNormal) >= 0) positive++;
    }
    antialiased.SetPixel(x, y, MixSrgb(fg, bg,
        positive / (double)(supersamples * supersamples)));
}
var displaced = Enumerable.Range(-10, 21)
    .Select(i => lineOrigin + lineTangent * (1.3 * i) + lineNormal * .18)
    .ToList();
var beforeMove = displaced.ToArray();
var inversion = EkstraceInverseArea.Refine(displaced, false,
    Array.Empty<int>(), antialiased, fg, bg, .25);
Check(inversion.EligiblePoints > 0 && inversion.MovedPoints >= 3,
    "Inverse pixel area moves smooth uncertain contour samples");
double beforeError = 0, afterError = 0;
for (int i = 3; i < displaced.Count - 3; i++)
{
    beforeError += Math.Abs((beforeMove[i] - lineOrigin).Dot(lineNormal));
    afterError += Math.Abs((displaced[i] - lineOrigin).Dot(lineNormal));
}
Check(afterError < beforeError * .75,
    "Inverse-area refinement reduces independently known position error");
Check(displaced[0] == beforeMove[0] &&
      displaced[^1] == beforeMove[^1],
    "Open chain junction endpoints remain exact");
Check(displaced.Zip(beforeMove).All(v => (v.First - v.Second).Length <= .2500001),
    "No point exceeds the 0.25px movement bound");
var protectedPoints = beforeMove.ToList();
_ = EkstraceInverseArea.Refine(protectedPoints, false,
    new[] { 10 }, antialiased, fg, bg, .25);
Check(Enumerable.Range(8, 5).All(k => protectedPoints[k] == beforeMove[k]),
    "Protected corner and its immediate shoulders remain pinned");

// Input without visible antialias should not be shifted just to force
// differences between the engines.
var hardEdge = new RasterImage(64, 64);
for (int y = 0; y < 64; y++)
for (int x = 0; x < 64; x++)
    hardEdge.SetPixel(x, y, x >= 32 ? fg : bg);
var hardPoints = Enumerable.Range(0, 25)
    .Select(y => new Vec2(32, y + 15)).ToList();
var hardBefore = hardPoints.ToArray();
var hardStats = EkstraceInverseArea.Refine(hardPoints, false,
    Array.Empty<int>(), hardEdge, fg, bg, .25);
Check(hardStats.MovedPoints == 0 &&
      hardPoints.SequenceEqual(hardBefore),
    "Hard two-colour edge has no invented anti-alias shift");

// Original mode must remain entirely byte-identical if its option is
// mistakenly set, since the experimental solver runs ONLY in Ekstracev1.
var originalOptionsUnchanged = new TraceOptions
{
    Style = ImageStyle.Crisp, PaletteMode = PaletteMode.FixedCount,
    ColorCount = 2, OptimizeNodes = false, ContourFittingEngine = ContourFittingEngine.Original
};
var originalOptionsExperimentalFlag = new TraceOptions
{
    Style = ImageStyle.Crisp, PaletteMode = PaletteMode.FixedCount,
    ColorCount = 2, OptimizeNodes = false, ContourFittingEngine = ContourFittingEngine.Original,
    EkstraceInverseAreaV1 = true
};
var unchangedTrace = Tracer.Trace(antialiased, originalOptionsUnchanged);
var optInIgnored = Tracer.Trace(antialiased, originalOptionsExperimentalFlag);
Check(SvgWriter.Write(unchangedTrace.Document) ==
      SvgWriter.Write(optInIgnored.Document) &&
      optInIgnored.Diagnostics.InverseAreaMovedPoints == 0,
    "Original engine is byte-identical even when experimental area flag is set");

var areaOptIn = new TraceOptions
{
    Style = ImageStyle.Blended, PaletteMode = PaletteMode.FixedCount,
    ColorCount = 2, OptimizeNodes = false,
    ContourFittingEngine = ContourFittingEngine.Ekstracev1,
    EkstraceInverseAreaV1 = true
};
var fullAreaTrace = Tracer.Trace(antialiased, areaOptIn);
Check(fullAreaTrace.Diagnostics.EkstraceV1Chains > 0 &&
      fullAreaTrace.Diagnostics.InverseAreaEligiblePoints >=
      fullAreaTrace.Diagnostics.InverseAreaMovedPoints &&
      fullAreaTrace.Diagnostics.InverseAreaMovedPoints ==
      fullAreaTrace.Diagnostics.InverseAreaSrgbPoints +
      fullAreaTrace.Diagnostics.InverseAreaLinearPoints,
    "End-to-end Ekstracev1 trace reports truthful area-inversion diagnostics");
Check(SvgWriter.Write(fullAreaTrace.Document) ==
      SvgWriter.Write(Tracer.Trace(antialiased, areaOptIn).Document),
    "Inverse-area mode yields deterministic end-to-end SVG output");

Console.WriteLine("Ekstracev1 integration smoke checks complete.");
