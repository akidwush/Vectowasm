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
