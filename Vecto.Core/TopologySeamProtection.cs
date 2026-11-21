namespace Vecto.Core;

/// <summary>
/// Conservative stacked-only seam treatment. Attaches under-layer paint helpers
/// to already-constructed paths: the upper region's Bézier geometry is NEVER
/// modified and no global SVG stroke is added.
/// </summary>
public readonly record struct TopologySeamStats(
    int ConsideredSharedEdges,
    int SafeSharedEdges,
    int UnderpaintRuns,
    int UnderpaintCurves,
    int SkippedNoOverlapNeed,
    int SkippedUnsafe,
    bool SkippedForLayerReasoning,
    int SkippedLowContrast = 0);

internal static class TopologySeamProtection
{
    private const double JunctionGuardSq = 9.0; // stay >= 3px from triple junction endpoints
    private const double MinSideClearance = 1.8; // reject strokes beside narrow features
    private const double OuterSideClearance = 2.8;
    private const double CurvatureDotFloor = .92; // ~23deg max tangent variation per piece
    private const int MaxPiecesPerCurve = 64;

    public static List<RegionPath> Attach(
        List<RegionPath> regions, Segmentation segmentation, BoundaryGraph graph,
        IReadOnlyList<int> paintOrder, HashSet<int>[] frontOf,
        Func<int, Rgba32> colorOfRegion,
        double bleedPx, int width, int height, CancellationToken cancellation, out TopologySeamStats stats)
    {
        if (regions.Count != paintOrder.Count)
            throw new InvalidOperationException("Stacked seam protection requires one painted region per region ID.");
        if (bleedPx < .30 || bleedPx > .50 || !double.IsFinite(bleedPx))
            throw new ArgumentOutOfRangeException(nameof(bleedPx));

        var rank = new int[paintOrder.Count];
        for (int i = 0; i < paintOrder.Count; i++) rank[paintOrder[i]] = i;
        var extras = new List<SeamUnderpaint>[regions.Count];
        // Identify true canvas-background regions. An outer silhouette touching
        // that region must never be stroked, even if the background is opaque.
        var edgeTouches = new int[segmentation.RegionCount];
        for (int x = 0; x < width; x++)
        {
            int top = segmentation.RegionId[x];
            int bottom = segmentation.RegionId[(height - 1) * width + x];
            if (top >= 0) edgeTouches[top]++;
            if (bottom >= 0) edgeTouches[bottom]++;
        }
        for (int y = 0; y < height; y++)
        {
            int left = segmentation.RegionId[y * width];
            int right = segmentation.RegionId[y * width + width - 1];
            if (left >= 0) edgeTouches[left]++;
            if (right >= 0) edgeTouches[right]++;
        }
        int perimeter = 2 * (width + height);
        var canvasBackground = new bool[segmentation.RegionCount];
        for (int i = 0; i < canvasBackground.Length; i++)
            canvasBackground[i] = edgeTouches[i] >= perimeter * .65 &&
                segmentation.RegionArea[i] >= (long)width * height * .35;
        int considered = 0, safe = 0, runs = 0, curves = 0, already = 0, unsafeEdges = 0, lowContrast = 0;

        foreach (var chain in graph.Chains)
        {
            cancellation.ThrowIfCancellationRequested();
            // Exterior/transparent edges and rings receive no seam paint. In the
            // stacked graph enclosed rings are handled by genuine underpainting
            // (filled parent cutouts) rather than an artificial local stroke.
            if (chain.Left < 0 || chain.Right < 0 || chain.Closed ||
                chain.Curve is not { Count: > 0 })
                continue;

            considered++;
            int back = rank[chain.Left] < rank[chain.Right] ? chain.Left : chain.Right;
            int front = back == chain.Left ? chain.Right : chain.Left;
            // A parent with its enclosed hole filled already lies behind the
            // child; adding stroke in that situation is unnecessary.
            if (frontOf[back]?.Contains(front) == true)
            { already++; continue; }
            if (segmentation.RegionArea[back] < 16 ||
                segmentation.RegionArea[front] < 16 ||
                canvasBackground[back] || canvasBackground[front] ||
                segmentation.RegionPalette[back] == segmentation.RegionPalette[front])
            { unsafeEdges++; continue; }

            // Low RGB-contrast seams are visually insignificant and would
            // dominate SVG helper-node overhead on detailed artwork.
            // This conservative gate only affects hidden auxiliary paint.
            var backColor = colorOfRegion(back);
            var frontColor = colorOfRegion(front);
            int dr = backColor.R - frontColor.R;
            int dg = backColor.G - frontColor.G;
            int db = backColor.B - frontColor.B;
            if (dr * dr + dg * dg + db * db < 60 * 60)
            { lowContrast++; continue; }

            var run = new List<CubicBezier>();
            int acceptedPieces = 0;
            void Flush()
            {
                if (run.Count == 0) return;
                (extras[rank[back]] ??= new List<SeamUnderpaint>())
                    .Add(new SeamUnderpaint(run.ToArray(), bleedPx * 2));
                runs++;
                curves += run.Count;
                run.Clear();
            }

            foreach (var whole in chain.Curve)
            {
                if (!Finite(whole)) { Flush(); continue; }
                double lengthUpper = (whole.P1 - whole.P0).Length +
                    (whole.P2 - whole.P1).Length + (whole.P3 - whole.P2).Length;
                if (!double.IsFinite(lengthUpper) || lengthUpper < .1)
                { Flush(); continue; }
                int subdivisions = Math.Clamp((int)Math.Ceiling(lengthUpper / 3), 1, MaxPiecesPerCurve);
                int runStart = -1;
                // Probe small slices independently for corner / thin-feature
                // safety, but reconstruct one EXACT cubic for each consecutive
                // safe interval. Exporting all little slices wastes nodes.
                void AddRun(int begin, int finish)
                {
                    if (begin < 0 || finish <= begin) return;
                    var piece = Slice(whole, begin / (double)subdivisions,
                        finish / (double)subdivisions);
                    if (run.Count > 0 &&
                        (run[^1].P3.DistSq(piece.P0) > 1e-10 ||
                         Tangent(run[^1], 1).Dot(Tangent(piece, 0)) < .96))
                        Flush();
                    run.Add(piece);
                }
                for (int i = 0; i < subdivisions; i++)
                {
                    var sub = Slice(whole, i / (double)subdivisions,
                        (i + 1) / (double)subdivisions);
                    if (SafeSlice(sub, chain, segmentation, width, height))
                    {
                        if (runStart < 0) runStart = i;
                        acceptedPieces++;
                    }
                    else
                    {
                        AddRun(runStart, i);
                        runStart = -1;
                        Flush();
                    }
                }
                AddRun(runStart, subdivisions);
            }
            Flush();
            if (acceptedPieces > 0) safe++;
            else unsafeEdges++;
        }

        var output = new List<RegionPath>(regions.Count);
        for (int i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            if (extras[i] is { Count: > 0 } helpers)
                output.Add(new RegionPath
                {
                    PaletteIndex = region.PaletteIndex,
                    Area = region.Area,
                    Loops = region.Loops, // exact same original curves
                    SeamUnderpaints = helpers
                });
            else output.Add(region);
        }
        stats = new TopologySeamStats(considered, safe, runs, curves, already, unsafeEdges, false, lowContrast);
        return output;
    }

    private static bool Finite(CubicBezier b) =>
        double.IsFinite(b.P0.X) && double.IsFinite(b.P0.Y) &&
        double.IsFinite(b.P1.X) && double.IsFinite(b.P1.Y) &&
        double.IsFinite(b.P2.X) && double.IsFinite(b.P2.Y) &&
        double.IsFinite(b.P3.X) && double.IsFinite(b.P3.Y);

    private static Vec2 Tangent(CubicBezier b, double t)
    {
        double v = 1 - t;
        return ((b.P1 - b.P0) * (3 * v * v) +
               (b.P2 - b.P1) * (6 * v * t) +
               (b.P3 - b.P2) * (3 * t * t)).Normalized();
    }

    private static bool SafeSlice(CubicBezier segment, Chain chain, Segmentation seg, int width, int height)
    {
        // Near a T/Y junction the auxiliary stroke must not protrude into a
        // third region or external silhouette. We do not stroke the endpoints.
        Vec2 start = chain.Points[0], end = chain.Points[^1];
        var t0 = Tangent(segment, 0);
        var t1 = Tangent(segment, 1);
        if (t0.LengthSq < .95 || t1.LengthSq < .95 ||
            t0.Dot(t1) < CurvatureDotFloor) return false;

        foreach (double t in new[] { .15, .5, .85 })
        {
            Vec2 p = segment.Eval(t);
            if (p.DistSq(start) < JunctionGuardSq ||
                p.DistSq(end) < JunctionGuardSq) return false;
            Vec2 tan = Tangent(segment, t);
            if (tan.LengthSq < .95) return false;
            Vec2 intoLeft = new(tan.Y, -tan.X);
            foreach (double offset in new[] { MinSideClearance, OuterSideClearance })
            {
                if (RegionAt(seg, p + intoLeft * offset, width, height) != chain.Left ||
                    RegionAt(seg, p - intoLeft * offset, width, height) != chain.Right)
                    return false;
            }
        }
        return true;
    }

    private static int RegionAt(Segmentation seg, Vec2 pos, int width, int height)
    {
        // The label grid has crack-lattice coordinates: pixel (x,y) has centre
        // at (x + .5, y + .5). Negative/out-of-range indices are void.
        int x = (int)Math.Floor(pos.X), y = (int)Math.Floor(pos.Y);
        return (uint)x < (uint)width && (uint)y < (uint)height
            ? seg.RegionId[y * width + x] : -1;
    }

    private static (CubicBezier Left, CubicBezier Right) Split(CubicBezier c, double t)
    {
        Vec2 a = c.P0 + (c.P1 - c.P0) * t;
        Vec2 b = c.P1 + (c.P2 - c.P1) * t;
        Vec2 d = c.P2 + (c.P3 - c.P2) * t;
        Vec2 e = a + (b - a) * t;
        Vec2 f = b + (d - b) * t;
        Vec2 mid = e + (f - e) * t;
        return (new CubicBezier(c.P0, a, e, mid),
                new CubicBezier(mid, f, d, c.P3));
    }

    private static CubicBezier Slice(CubicBezier c, double t0, double t1)
    {
        if (t0 <= 0 && t1 >= 1) return c;
        if (t0 > 0) c = Split(c, t0).Right;
        double remaining = (t1 - t0) / (1 - t0);
        return remaining >= 1 ? c : Split(c, remaining).Left;
    }
}
