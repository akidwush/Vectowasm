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

Console.WriteLine("Ekstracev1 integration smoke checks complete.");
