using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Vecto.Core;
using Vecto.ImageDecoding;
using Vecto.Cli;

return BenchmarkRunner.Run(args);

internal static class BenchmarkRunner
{
    public static int Run(string[] args)
    {
        try { return Execute(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Benchmark error: " + ex.Message);
            return 1;
        }
    }

    private static int Execute(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("DikaTrace four-mode benchmark");
            Console.WriteLine("Usage: dotnet run --project Vecto.Benchmarks -c Release -- --image input.png [--json result.json] [--csv result.csv] [--svg-dir output] [--repeat 3]");
            Console.WriteLine("       dotnet run --project Vecto.Benchmarks -c Release -- --demo [--json result.json]");
            Console.WriteLine("Optional: --detail Low|Medium|High --style Auto|Crisp|Blended|Photo --background FFFFFF --mask-threshold 45");
            Console.WriteLine("PNG and JPEG are decoded with the project's C# decoders; unsupported layouts fail, not silently converted.");
            return args.Length == 0 ? 1 : 0;
        }

        static string? Value(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            if (i < 0) return null;
            if (i + 1 >= a.Length) throw new ArgumentException("Missing value for " + name);
            return a[i + 1];
        }

        string? imagePath = Value(args, "--image");
        bool demo = args.Contains("--demo");
        if (demo == (imagePath is not null))
            throw new ArgumentException("Choose exactly one of --image or --demo");

        RasterImage original;
        string sourceName;
        if (demo)
        {
            original = DemoFixture();
            sourceName = "built-in-synthetic-fixture";
        }
        else
        {
            byte[] encoded = File.ReadAllBytes(imagePath!);
            original = PngDecoder.TryDecode(encoded)
                ?? (JpegDecoder.IsJpeg(encoded) ? JpegDecoder.TryDecode(encoded) : null)
                ?? throw new InvalidDataException(
                    "Unsupported PNG/JPEG encoding: supply non-interlaced standard PNG or supported JPEG. No silent decoder fallback in CLI.");
            sourceName = Path.GetFileName(imagePath!);
        }

        if ((long)original.Width * original.Height > 8_000_000)
            throw new InvalidDataException("Input exceeds DikaTrace browser's 8MP limit");

        var details = Enum.Parse<DetailLevel>(Value(args, "--detail") ?? "Medium", true);
        var style = Enum.Parse<ImageStyle>(Value(args, "--style") ?? "Auto", true);
        int repeat = int.Parse(Value(args, "--repeat") ?? "1", CultureInfo.InvariantCulture);
        if (repeat is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(repeat), "Repeat must be 1 to 8.");
        double maskThreshold = double.Parse(Value(args, "--mask-threshold") ?? "45", CultureInfo.InvariantCulture);
        if (maskThreshold is < 1 or > 441) throw new ArgumentOutOfRangeException(nameof(maskThreshold));
        Rgba32? givenBackground = null;
        if (Value(args, "--background") is { } bg)
        {
            bg = bg.TrimStart('#');
            if (bg.Length != 6 || !int.TryParse(bg, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                throw new ArgumentException("Background must be hex RRGGBB");
            givenBackground = new Rgba32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        }

        var prepared = PixelCompressor.Prepare(original);
        var analysis = AutoPaletteEstimator.Analyze(prepared.Image);
        var specs = new (string Name, ShapeMode Shape, bool Optimize, BezierOptimizationMode Mode)[]
        {
            ("planar_off", ShapeMode.Planar, false, BezierOptimizationMode.Pairwise),
            ("planar_pairwise", ShapeMode.Planar, true, BezierOptimizationMode.Pairwise),
            ("planar_global_dp", ShapeMode.Planar, true, BezierOptimizationMode.GlobalDp),
            ("stacked_global_dp", ShapeMode.Stacked, true, BezierOptimizationMode.GlobalDp),
        };

        // Keep decode/cleanup/palette inference outside trace timings so
        // rows differ only in geometry optimization and stacking.
        Console.WriteLine("Input: " + sourceName + " (" + original.Width + "x" + original.Height +
            "), precleaned pixels " + prepared.ChangedPixels + "; inferred inks " + analysis.EstimatedInkCount);
        var rows = new List<BenchmarkRow>();
        string? svgDirectory = Value(args, "--svg-dir");
        if (svgDirectory is not null) Directory.CreateDirectory(svgDirectory);

        // Prepare a small JIT warmup; not part of measured runs.
        Tracer.Trace(DemoFixture(), new TraceOptions
        {
            Style = ImageStyle.Crisp,
            MaxColors = 3,
            CurveFitting = true,
            OptimizeNodes = false
        });

        foreach (var spec in specs)
        {
            var durations = new List<double>(repeat);
            TraceResult? traced = null;
            for (int k = 0; k < repeat; k++)
            {
                var traceOptions = new TraceOptions
                {
                    Style = style,
                    Detail = details,
                    ShapeMode = spec.Shape,
                    CurveFitting = true,
                    OptimizeNodes = spec.Optimize,
                    BezierMode = spec.Mode,
                    PaletteMode = PaletteMode.Auto,
                    MaxColors = analysis.RecommendedMaxColors,
                };
                var timer = Stopwatch.StartNew();
                traced = Tracer.Trace(prepared.Image, traceOptions);
                timer.Stop();
                durations.Add(timer.Elapsed.TotalMilliseconds);
            }

            durations.Sort();
            double medianMs = durations[durations.Count / 2];
            var result = traced!;
            var doc = result.Document;
            string svg = SvgWriter.Write(doc);
            if (svgDirectory is not null)
                File.WriteAllText(Path.Combine(svgDirectory, spec.Name + ".svg"), svg, Encoding.UTF8);

            var resultRaster = Rasterizer.Render(doc, supersample: 1);
            var quality = FidelityMetrics.Compare(original, resultRaster, givenBackground, maskThreshold);

            int curves = 0, lines = 0, micros = 0, loops = 0;
            foreach (var region in doc.Regions)
                foreach (var loop in region.Loops)
                {
                    loops++;
                    foreach (var segment in loop)
                    {
                        if (segment.IsLine())
                        {
                            lines++;
                            if ((segment.P3 - segment.P0).Length < 2.0) micros++;
                        }
                        else curves++;
                    }
                }

            rows.Add(new BenchmarkRow(
                spec.Name,
                doc.Regions.Count, loops, doc.Palette.Count,
                curves, lines, micros, curves + lines, result.Diagnostics.FittedChainNodes,
                result.Diagnostics.OptimizedChainNodes, result.Diagnostics.GlobalReducedChains,
                Math.Round(medianMs, 2), result.Diagnostics.FitMs,
                quality.RgbMae, quality.BlockSsim, quality.MaskIou,
                quality.BoundaryP99, quality.BoundaryHausdorff,
                quality.ForegroundReferencePixels, quality.ForegroundOutputPixels,
                Encoding.UTF8.GetByteCount(svg)));
        }

        Console.WriteLine();
        Console.WriteLine("Mode                 Path  Loops  C     L     L<2  Total   Trace ms   IoU    SSIM   P99px   Maxpx");
        foreach (var r in rows)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-21}{1,4}{2,7}{3,6}{4,6}{5,6}{6,7}{7,11:0.00}{8,8}{9,8:0.0000}{10,8}{11,8}",
                r.Mode, r.Paths, r.Subpaths, r.Cubic, r.Lines, r.MicroLines,
                r.TotalSegments, r.TraceMs, Format(r.MaskIou),
                r.BlockSsim, Format(r.BoundaryP99Px), Format(r.BoundaryHausdorffPx)));

        var report = new BenchmarkReport(
            sourceName, original.Width, original.Height,
            details.ToString(), style.ToString(), analysis.EstimatedInkCount,
            prepared.ChangedPixels, repeat, maskThreshold,
            (givenBackground ?? FidelityMetrics.GuessCornerBackground(original)).ToString(),
            rows);

        var pretty = new JsonSerializerOptions { WriteIndented = true };
        if (Value(args, "--json") is { } jsonPath)
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, pretty));
        if (Value(args, "--csv") is { } csvPath)
        {
            var csv = new StringBuilder();
            csv.AppendLine("Mode,Paths,Subpaths,Colors,Cubic,Lines,MicroLines,TotalSegments,FittedChainNodes,OptimizedChainNodes,GlobalReducedChains,TraceMs,FitMs,RgbMae,BlockSsim,MaskIou,BoundaryP99Px,BoundaryHausdorffPx,ForegroundSource,ForegroundOutput,SvgBytes");
            foreach (var r in rows)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    r.Mode, r.Paths.ToString(), r.Subpaths.ToString(),
                    r.Colors.ToString(), r.Cubic.ToString(), r.Lines.ToString(),
                    r.MicroLines.ToString(), r.TotalSegments.ToString(),
                    r.FittedChainNodes.ToString(), r.OptimizedChainNodes.ToString(),
                    r.GlobalReducedChains.ToString(), Num(r.TraceMs), r.FitMs.ToString(),
                    Num(r.RgbMae), Num(r.BlockSsim), Num(r.MaskIou),
                    Num(r.BoundaryP99Px), Num(r.BoundaryHausdorffPx),
                    r.ForegroundReferencePixels.ToString(),
                    r.ForegroundOutputPixels.ToString(), r.SvgBytes.ToString()
                }));
            }
            File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);
        }
        return 0;
    }

    private static string Num(double? value) =>
        value is null ? "" : value.Value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Format(double? value) =>
        value is null ? "n/a" : value.Value.ToString("0.00", CultureInfo.InvariantCulture);

    private static RasterImage DemoFixture()
    {
        var result = new RasterImage(96, 96);
        for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                var color = new Rgba32(250, 250, 250, 255);
                if (x is >= 12 and < 84 && y is >= 12 and < 84)
                    color = new Rgba32(0, 0, 0, 255);
                if (x is >= 16 and < 80 && y is >= 16 and < 80)
                    color = new Rgba32(218, 20, 22, 255);
                if (x is >= 32 and < 64 && y is >= 32 and < 64)
                    color = new Rgba32(250, 250, 250, 255);
                result.SetPixel(x, y, color);
            }
        return result;
    }
}

internal sealed record BenchmarkRow(
    string Mode, int Paths, int Subpaths, int Colors,
    int Cubic, int Lines, int MicroLines, int TotalSegments,
    int FittedChainNodes, int OptimizedChainNodes, int GlobalReducedChains,
    double TraceMs, long FitMs, double RgbMae, double BlockSsim,
    double? MaskIou, double? BoundaryP99Px, double? BoundaryHausdorffPx,
    int ForegroundReferencePixels, int ForegroundOutputPixels,
    int SvgBytes);

internal sealed record BenchmarkReport(
    string SourceFile, int Width, int Height, string Detail, string Style,
    int EstimatedInks, int CleanedPixels, int Repetitions, double MaskThreshold,
    string ReferenceBackground, List<BenchmarkRow> Results);
