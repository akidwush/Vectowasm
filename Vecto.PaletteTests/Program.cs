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
