namespace Vecto.Core;

/// <summary>
/// Cheap, deterministic estimate of the fewest useful flat inks in an image.
/// This runs once before tracing. It never modifies pixels, the original Vecto
/// palette builder, region graph, or Bézier geometry.
/// Design references: Inkvec's evidence/description-length palette selection,
/// Kittl's dominant-colour auto count, and interior-only colour sampling.
/// This is an independent implementation, not copied third-party code.
/// </summary>
public static class AutoPaletteEstimator
{
    private const int Buckets = 4096; // 4 bits per channel
    private const int ChannelDelta = 24;
    private const double SimilarInkDistance = 0.105;
    private const double LargeInkDistance = 0.060;
    private const double ResidualScale = 0.045;

    public static AutoPaletteEstimate Analyze(RasterImage image, int maximumColors = 64)
    {
        int max = Math.Clamp(maximumColors, 1, 64);
        int w = image.Width, h = image.Height;
        byte[] px = image.Pixels;

        var counts = new int[Buckets];
        var interior = new int[Buckets];
        var red = new long[Buckets];
        var green = new long[Buckets];
        var blue = new long[Buckets];
        long opaque = 0, flat = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                if (px[p + 3] < 128) continue;
                opaque++;
                int slot = ((px[p] >> 4) << 8)
                         | ((px[p + 1] >> 4) << 4)
                         | (px[p + 2] >> 4);
                counts[slot]++;
                red[slot] += px[p];
                green[slot] += px[p + 1];
                blue[slot] += px[p + 2];

                if (IsInterior(px, p, x, y, w, h))
                {
                    interior[slot]++;
                    flat++;
                }
            }
        }

        if (opaque == 0)
            return new AutoPaletteEstimate(1, 1, 0, 0, 0);

        // If virtually no region is flat (e.g. tiny line-art), use all opaque
        // pixels rather than inventing a palette from an unrepresentative few.
        bool haveInterior = flat >= Math.Max(8L, opaque / 12);
        var modes = new List<Mode>();
        long smallestSample = Math.Max(8L, opaque / 20000);

        for (int slot = 0; slot < Buckets; slot++)
        {
            int count = counts[slot];
            if (count == 0) continue;
            int core = haveInterior ? interior[slot] : count;
            int support = core + (count - core) / 8;
            if (support < smallestSample) continue;

            byte r = (byte)((red[slot] + count / 2) / count);
            byte g = (byte)((green[slot] + count / 2) / count);
            byte b = (byte)((blue[slot] + count / 2) / count);
            modes.Add(new Mode(slot, Oklab.FromRgb(r, g, b), count, core, support));
        }

        // A predictable order is necessary for deterministic palette count.
        modes.Sort(static (a, b) =>
        {
            int c = b.Support.CompareTo(a.Support);
            if (c != 0) return c;
            c = b.Core.CompareTo(a.Core);
            return c != 0 ? c : a.Slot.CompareTo(b.Slot);
        });

        var accepted = new List<Mode>();
        double inkCost = 6.0 * (0.5 * Math.Log(Math.Max(16, opaque))) * 3.0;
        long substantialSupport = Math.Max(16L, opaque / 4000);
        long accentSupport = Math.Max(12L, opaque / 20000);

        foreach (var candidate in modes)
        {
            double minDistSq = double.PositiveInfinity;
            Lab nearestInk = default;
            foreach (var chosen in accepted)
            {
                double distance = Oklab.DistSq(candidate.Color, chosen.Color);
                if (distance < minDistSq)
                {
                    minDistSq = distance;
                    nearestInk = chosen.Color;
                }
            }
            double nearest = Math.Sqrt(minDistSq);
            double chroma = Math.Sqrt(
                (double)candidate.Color.A * candidate.Color.A
                + (double)candidate.Color.B * candidate.Color.B);
            double share = candidate.Support / (double)opaque;
            bool stableInterior = candidate.Core >= 0.35 * candidate.Total;

            // A rare, highly distinct saturated accent may be real artwork.
            // Near-black JPEG shades and thin anti-aliased rims should not
            // consume an extra ink slot merely because they appear often.
            bool substantial = stableInterior && candidate.Support >= substantialSupport;
            bool accent = candidate.Core >= accentSupport
                          && chroma >= 0.10 && nearest >= 0.20;
            if (!substantial && !accent) continue;

            if (accepted.Count > 0)
            {
                bool distinct = nearest >= SimilarInkDistance
                    || (share >= 0.06 && nearest >= LargeInkDistance);
                // A large dark region may be an encoded/photographic shade of
                // the same black ink. Size alone must not split that ink.
                bool similarNearBlack = candidate.Color.L < 0.29f
                    && nearestInk.L < 0.29f && nearest < 0.135;
                if (!distinct || similarNearBlack) continue;

                // MDL-inspired gate: keeping a mode must explain enough
                // pixel-to-ink residual to pay for three new colour parameters.
                double residualGain = 0.5 * candidate.Support
                                      * Math.Pow(nearest / ResidualScale, 2);
                if (residualGain <= inkCost) continue;
            }

            accepted.Add(candidate);
            if (accepted.Count >= max) break;
        }

        int estimated = Math.Max(1, accepted.Count);
        // A single optional spare cluster lets Vecto's existing K-means adapt
        // when lossy or complex edge pixels are present; it is never forced for
        // simple flat artwork. The final SVG can contain fewer actual colours.
        bool lossyEdges = flat * 25 < opaque * 24; // >4% non-flat pixels
        bool complexInput = modes.Count >= estimated * 4;
        int recommended = Math.Min(max,
            estimated + (estimated >= 3 && lossyEdges && complexInput ? 1 : 0));

        return new AutoPaletteEstimate(estimated, recommended, opaque, flat, modes.Count);
    }

    private static bool IsInterior(byte[] px, int p, int x, int y, int w, int h) =>
        (x == 0 || Close(px, p, p - 4))
        && (x == w - 1 || Close(px, p, p + 4))
        && (y == 0 || Close(px, p, p - w * 4))
        && (y == h - 1 || Close(px, p, p + w * 4));

    private static bool Close(byte[] px, int a, int b) =>
        px[b + 3] >= 128
        && Math.Abs((int)px[a] - px[b]) <= ChannelDelta
        && Math.Abs((int)px[a + 1] - px[b + 1]) <= ChannelDelta
        && Math.Abs((int)px[a + 2] - px[b + 2]) <= ChannelDelta
        && Math.Abs((int)px[a + 3] - px[b + 3]) <= ChannelDelta;

    private readonly record struct Mode(int Slot, Lab Color, int Total, int Core, int Support);
}

public readonly record struct AutoPaletteEstimate(
    int EstimatedInkCount,
    int RecommendedMaxColors,
    long OpaquePixels,
    long InteriorPixels,
    int CandidateModes);
