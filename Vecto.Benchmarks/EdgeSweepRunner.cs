using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using System.Text;
using Vecto.Core;
using Vecto.ImageDecoding;
using Vecto.Cli;

/// <summary>
/// Non-production A/B: keeps image cleanup, color quantization, topology and
/// Bézier algorithms fixed. Sweeps only the existing subpixel edge locator's
/// coverage isovalue and source RGBA. Does not modify the normal browser UI.
/// </summary>
internal static class EdgeSweepRunner
{
    internal static int Run(string[] args)
    {
        string? Get(string key)
        {
            int i = Array.IndexOf(args, key);
            if (i < 0) return null;
            if (i + 1 == args.Length) throw new ArgumentException("Missing " + key);
            return args[i + 1];
        }
        string file = Get("--image") ?? throw new ArgumentException("--image original JPEG/PNG required");
        string output = Get("--output") ?? "edge-sweep-results";
        byte[] encoded = File.ReadAllBytes(file);
        var original = PngDecoder.TryDecode(encoded)
            ?? (JpegDecoder.IsJpeg(encoded) ? JpegDecoder.TryDecode(encoded) : null)
            ?? throw new InvalidDataException("Unsupported JPEG/PNG");
        var prepared = PixelCompressor.Prepare(original);
        var estimate = AutoPaletteEstimator.Analyze(prepared.Image);

        Directory.CreateDirectory(output);
        var lines = new List<string>
        {
            "Variant,Shape,Sampling,Coverage,Paths,Subpaths,C,L,MicroL,Total,FittedChainNodes,OptimizedChainNodes,GlobalReducedChains,TimeMs,LocalV3MovedPoints"
        };

        // Direct baseline is explicitly no edge-sampling overrides; this
        // catches even tiny differences between baseline and the sweep mode.
        var baselineOptions = Options(ShapeMode.Planar, false, .5, null, false, .20, .25, .012);
        var baseline = Tracer.Trace(prepared.Image, baselineOptions);
        var baselineSvg = SvgWriter.Write(baseline.Document);
        File.WriteAllText(Path.Combine(output, "baseline_planar_off.svg"), baselineSvg, Encoding.UTF8);

        var cases = new List<(string Sample, double Coverage, RasterImage? Reference,
            bool V3, double Shift, double Gradient, double MinMove)>();
        foreach (double coverage in new[] { .40, .45, .475, .50, .525, .55, .60 })
        {
            cases.Add(("prepared", coverage, null, false, .20, .25, .012));
            cases.Add(("raw", coverage, original, false, .20, .25, .012));
        }

        // Independent V3 variants at the unchanged 50% isovalue.
        // Test both raw and prepared source, with small extra shift budgets.
        foreach (double shift in new[] { .12, .20, .30 })
        {
            cases.Add(("prepared", .50, null, true, shift, .25, .012));
            cases.Add(("raw", .50, original, true, shift, .25, .012));
        }

        // Conservative variants: reject weak gradients and tiny corrections.
        // These test whether V3 can improve IoU without adding micro-L.
        foreach (var guard in new[] { (Name: 280, G: .28, M: .012),
                                      (Name: 300, G: .30, M: .012),
                                      (Name: 330, G: .33, M: .012),
                                      (Name: 360, G: .36, M: .012),
                                      (Name: 450, G: .45, M: .08),
                                      (Name: 600, G: .60, M: .14) })
        {
            cases.Add(("prepared", .50, null, true, .20, guard.G, guard.M));
            cases.Add(("raw", .50, original, true, .20, guard.G, guard.M));
        }

        foreach (var shape in new[] { ShapeMode.Planar, ShapeMode.Stacked })
        {
            bool optimize = shape == ShapeMode.Stacked;
            foreach (var c in cases)
            {
                var opts = Options(shape, optimize, c.Coverage, c.Reference, c.V3, c.Shift,
                    c.Gradient, c.MinMove);
                var stopwatch = Stopwatch.StartNew();
                var result = Tracer.Trace(prepared.Image, opts);
                stopwatch.Stop();

                string id = c.V3
                    ? $"{shape.ToString().ToLowerInvariant()}_{c.Sample}_v3_d{(int)Math.Round(c.Shift * 1000):D3}" +
                      (c.Gradient > .25 ? $"_g{(int)Math.Round(c.Gradient * 1000):D3}" : "")
                    : $"{shape.ToString().ToLowerInvariant()}_{c.Sample}_t{(int)Math.Round(c.Coverage * 1000):D3}";
                string svg = SvgWriter.Write(result.Document);
                File.WriteAllText(Path.Combine(output, id + ".svg"), svg, Encoding.UTF8);

                if (shape == ShapeMode.Planar && !optimize
                    && !c.V3 && c.Sample == "prepared" && c.Coverage == .50
                    && svg != baselineSvg)
                    throw new InvalidOperationException("AB reference drifted from production edge locator");

                int cubics = 0, lineSegments = 0, micros = 0, contours = 0;
                foreach (var region in result.Document.Regions)
                foreach (var loop in region.Loops)
                {
                    contours++;
                    foreach (var seg in loop)
                    {
                        if (seg.IsLine())
                        {
                            lineSegments++;
                            if ((seg.P3 - seg.P0).Length < 2) micros++;
                        }
                        else cubics++;
                    }
                }
                lines.Add(string.Join(",", id, shape, c.Sample,
                    c.Coverage.ToString("0.000", CultureInfo.InvariantCulture),
                    result.Document.Regions.Count, contours, cubics,
                    lineSegments, micros, cubics + lineSegments,
                    result.Diagnostics.FittedChainNodes,
                    result.Diagnostics.OptimizedChainNodes,
                    result.Diagnostics.GlobalReducedChains,
                    stopwatch.Elapsed.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                    result.Diagnostics.LocalEdgeV3MovedPoints));
                Console.WriteLine(id + " total=" + (cubics + lineSegments) +
                    " paths=" + result.Document.Regions.Count +
                    " contours=" + contours +
                    " ms=" + stopwatch.Elapsed.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture));
            }
        }
        File.WriteAllLines(Path.Combine(output, "trace_stats.csv"), lines);
        Console.WriteLine("PASS edge sweep: baseline identical, output cases=" + (cases.Count * 2));
        return 0;

        TraceOptions Options(ShapeMode shape, bool optimize, double threshold, RasterImage? reference,
            bool localV3, double maxShift, double gradient, double minMove) => new()
        {
            Style = ImageStyle.Auto,
            Detail = DetailLevel.Medium,
            PaletteMode = PaletteMode.Auto,
            MaxColors = estimate.RecommendedMaxColors,
            CurveFitting = true,
            OptimizeNodes = optimize,
            BezierMode = BezierOptimizationMode.GlobalDp,
            ShapeMode = shape,
            EdgeCoverageThreshold = threshold,
            EdgeReferenceImage = reference,
            LocalEdgeRefinementV3 = localV3,
            LocalEdgeMaxShiftV3 = maxShift,
            LocalEdgeMinGradientV3 = gradient,
            LocalEdgeMinCandidateShiftV3 = minMove,
        };
    }
}
