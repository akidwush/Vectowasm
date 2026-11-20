namespace Vecto.Core;

/// <summary>Opt-in palette rescue evidence; does not modify source pixels.</summary>
public readonly record struct AccentInkStats(int QualifiedHueBins, int AddedCenters,
    int SampledSolidCorePixels);

/// <summary>
/// Keep small but spatially coherent chromatic pigments that global Oklab
/// k-means underweights (e.g. yellow eyes in an otherwise orange mascot).
///
/// This does not paint or hallucinate shapes: it only adds up to two proven
/// source-color seeds before pixel labelling. Labels, topology, and cubic
/// paths are then calculated by the existing DikaTracer pipeline.
/// </summary>
public static class AccentInkPaletteV1
{
    private const int HueBins = 36;
    private const int ValueGroups = 2;
    private const int LocalChannelTolerance = 18;
    private const int TileSide = 24;
    private const double MinNoveltySq = 0.072 * 0.072;
    private const double MinIndependentSq = 0.055 * 0.055;
    // A new accent must represent a genuinely different hue, not merely a
    // darker/lighter shade of an existing orange or brown pigment.
    private const double MinHueSeparationRad = 0.21; // about 12 degrees

    public static (Lab[] Colors, AccentInkStats Stats) Expand(
        RasterImage image, IReadOnlyList<Lab> centers, int maxColors,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(centers);
        if (maxColors < 1 || maxColors > 64)
            throw new ArgumentOutOfRangeException(nameof(maxColors));
        int w = image.Width, h = image.Height;
        if (w < 4 || h < 4 || centers.Count >= maxColors)
            return (centers.ToArray(), default);

        int tx = (w + TileSide - 1) / TileSide;
        int ty = (h + TileSide - 1) / TileSide;
        int tileCount = checked(tx * ty);
        const int bins = HueBins * ValueGroups;
        var count = new int[bins];
        var sumR = new long[bins];
        var sumG = new long[bins];
        var sumB = new long[bins];
        var strongestTile = new int[bins];
        var tileVotes = new int[checked(bins * tileCount)];
        var pixels = image.Pixels;
        Span<int> offsets = stackalloc int[4] { -4, 4, -4 * w, 4 * w };
        int sampled = 0;
        for (int y = 1; y < h - 1; y += 2)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 1; x < w - 1; x += 2)
            {
                int index = (y * w + x) * 4;
                if (pixels[index + 3] < Segmenter.OpaqueThreshold) continue;
                int r = pixels[index], g = pixels[index + 1], b = pixels[index + 2];
                int hi = Math.Max(r, Math.Max(g, b));
                int lo = Math.Min(r, Math.Min(g, b));
                int delta = hi - lo;
                // Colorful solid interiors only: exclude gray, pale edge
                // fringe, dark noise, and desaturated JPEG backgrounds.
                if (hi < 90 || delta < 42 || delta * 100 < hi * 40) continue;

                bool flat = true;
                foreach (int offset in offsets)
                {
                    int j = index + offset;
                    if (pixels[j + 3] < Segmenter.OpaqueThreshold ||
                        Math.Abs(pixels[j] - r) > LocalChannelTolerance ||
                        Math.Abs(pixels[j + 1] - g) > LocalChannelTolerance ||
                        Math.Abs(pixels[j + 2] - b) > LocalChannelTolerance)
                    {
                        flat = false;
                        break;
                    }
                }
                if (!flat) continue;

                double hue;
                if (hi == r) hue = 60.0 * (g - b) / delta;
                else if (hi == g) hue = 60.0 * (2.0 + (b - r) / (double)delta);
                else hue = 60.0 * (4.0 + (r - g) / (double)delta);
                if (hue < 0) hue += 360;
                int hueSlot = Math.Clamp((int)(hue / 10), 0, HueBins - 1);
                int slot = hueSlot + HueBins * (hi >= 185 ? 1 : 0);
                count[slot]++;
                sumR[slot] += r; sumG[slot] += g; sumB[slot] += b;
                sampled++;
                int tile = (y / TileSide) * tx + x / TileSide;
                int vote = ++tileVotes[slot * tileCount + tile];
                if (vote > strongestTile[slot]) strongestTile[slot] = vote;
            }
        }

        int minSupport = Math.Max(20, sampled / 16000);
        var candidates = new List<(Lab Color, double Score)>();
        int qualified = 0;
        for (int slot = 0; slot < bins; slot++)
        {
            if (count[slot] < minSupport || strongestTile[slot] < 7) continue;
            int r = (int)Math.Round((double)sumR[slot] / count[slot]);
            int g = (int)Math.Round((double)sumG[slot] / count[slot]);
            int b = (int)Math.Round((double)sumB[slot] / count[slot]);
            Lab color = Oklab.FromRgb((byte)r, (byte)g, (byte)b);
            double minDistance = double.PositiveInfinity;
            foreach (var existing in centers)
                minDistance = Math.Min(minDistance, Oklab.DistSq(color, existing));
            if (minDistance < MinNoveltySq) continue;
            // Oklab chroma angle is more robust to luminance than the RGB
            // distance. It suppresses redundant dark-orange/brown seeds
            // that can otherwise outrank a tiny but genuinely yellow eye.
            double hue = Math.Atan2(color.B, color.A);
            double nearestHue = Math.PI;
            foreach (var existing in centers)
            {
                double chromaSq = existing.A * existing.A + existing.B * existing.B;
                if (chromaSq < 0.06 * 0.06) continue; // white, black or gray
                double diff = Math.Abs(hue - Math.Atan2(existing.B, existing.A));
                diff = Math.Min(diff, 2 * Math.PI - diff);
                nearestHue = Math.Min(nearestHue, diff);
            }
            if (nearestHue < MinHueSeparationRad) continue;
            qualified++;
            // Both spatial support and color novelty are required. Prefer
            // distinct, demonstrably missing pigments over fringe-sized bins.
            double novelty = Math.Sqrt(minDistance) - Math.Sqrt(MinNoveltySq);
            candidates.Add((color, count[slot] * novelty * novelty));
        }

        var result = centers.ToList();
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count >= maxColors || result.Count >= centers.Count + 2) break;
            if (result.Any(color =>
                Oklab.DistSq(color, candidate.Color) < MinIndependentSq))
                continue;
            result.Add(candidate.Color);
        }
        return (result.ToArray(),
            new AccentInkStats(qualified, result.Count - centers.Count, sampled));
    }
}
