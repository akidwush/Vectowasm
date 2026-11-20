namespace Vecto.Core;

/// <summary>
/// A source-driven guard for small, coherent chromatic inks lost by the
/// global automatic palette budget (e.g. teal foliage beside a yellow logo).
/// It never guesses a named object or paints a missing region by itself.
/// Only candidate traces with genuinely improved source-color assignment
/// can replace the original result. The original stays the fallback.
/// </summary>
public static class AutoPaletteInkGuard
{
    private const int HueGroups = 18; // 20 degrees per hue bin
    private const double MissingDistanceSq = 0.12 * 0.12;
    private const int CoreNeighborTolerance = 24;
    public readonly record struct Evidence(Rgba32 SourceColor, int CoreSamples,
        double MissingDistanceSq);

    public static Evidence? FindMissing(RasterImage source, TraceResult baseline,
        CancellationToken ct = default)
    {
        if (source.Width < 10 || source.Height < 10 ||
            baseline.Document.Palette.Count == 0) return null;
        var px = source.Pixels;
        int w = source.Width, h = source.Height;
        var counts = new int[HueGroups];
        var sumR = new long[HueGroups];
        var sumG = new long[HueGroups];
        var sumB = new long[HueGroups];
        int opaqueSamples = 0;

        for (int y = 1; y < h - 1; y += 3)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 1; x < w - 1; x += 3)
            {
                int i = 4 * (y * w + x);
                if (px[i + 3] < 250) continue;
                opaqueSamples++;
                int r = px[i], g = px[i + 1], b = px[i + 2];
                int high = Math.Max(r, Math.Max(g, b));
                int low = Math.Min(r, Math.Min(g, b));
                int delta = high - low;
                if (high < 70 || delta < 50 || 100 * delta < 45 * high)
                    continue;
                // Require a solid interior, not a single anti-alias fringe,
                // one-pixel colored noise, or a JPEG ringing edge.
                bool flat = true;
                for (int k = 0; k < 4; k++)
                {
                    int j = k switch {
                        0 => i - 4, 1 => i + 4, 2 => i - w * 4, _ => i + w * 4
                    };
                    if (px[j + 3] < 250 ||
                        Math.Abs(px[j] - r) > CoreNeighborTolerance ||
                        Math.Abs(px[j + 1] - g) > CoreNeighborTolerance ||
                        Math.Abs(px[j + 2] - b) > CoreNeighborTolerance)
                    {
                        flat = false;
                        break;
                    }
                }
                if (!flat) continue;
                double hue = high == r
                    ? (g - b) * 60.0 / delta
                    : high == g ? 120.0 + (b - r) * 60.0 / delta
                    : 240.0 + (r - g) * 60.0 / delta;
                if (hue < 0) hue += 360;
                int slot = Math.Clamp((int)(hue / 20), 0, HueGroups - 1);
                counts[slot]++;
                sumR[slot] += r;
                sumG[slot] += g;
                sumB[slot] += b;
            }
        }
        int minSupport = Math.Max(14, opaqueSamples / 700);
        var pal = baseline.Document.Palette.Select(c =>
            Oklab.FromRgb(c.Color.R, c.Color.G, c.Color.B)).ToArray();
        Evidence? best = null;
        double bestScore = 0;
        for (int slot = 0; slot < HueGroups; slot++)
        {
            int count = counts[slot];
            if (count < minSupport) continue;
            byte r = (byte)((sumR[slot] + count / 2) / count);
            byte g = (byte)((sumG[slot] + count / 2) / count);
            byte b = (byte)((sumB[slot] + count / 2) / count);
            Lab candidate = Oklab.FromRgb(r, g, b);
            double nearest = double.MaxValue;
            foreach (var color in pal)
                nearest = Math.Min(nearest, Oklab.DistSq(candidate, color));
            if (nearest < MissingDistanceSq) continue;
            // Square-root support prevents an abundant near-duplicate shade
            // from outranking a smaller but genuinely different green ink.
            double score = Math.Sqrt(count) * nearest;
            if (score <= bestScore) continue;
            bestScore = score;
            best = new Evidence(new Rgba32(r, g, b, 255), count, nearest);
        }
        return best;
    }

    public static bool IsImprovement(RasterImage source, TraceResult baseline,
        TraceResult candidate, Evidence ink, CancellationToken ct = default)
    {
        if (candidate.Width != baseline.Width || candidate.Height != baseline.Height ||
            candidate.LabelMap.Length != baseline.LabelMap.Length ||
            candidate.Document.Palette.Count == 0)
            return false;
        Lab target = Oklab.FromRgb(ink.SourceColor.R, ink.SourceColor.G, ink.SourceColor.B);
        double oldDistance = Nearest(target, baseline.Document.Palette);
        double newDistance = Nearest(target, candidate.Document.Palette);
        // The missing ink must actually acquire a distinct display color.
        if (newDistance >= oldDistance * .55 ||
            newDistance >= MissingDistanceSq)
            return false;
        double oldLoss = LabelColorLoss(source, baseline, ct);
        double newLoss = LabelColorLoss(source, candidate, ct);
        // Extra color must not worsen global pixel-color assignment just to
        // rescue a visually small accent. Topology must remain manageable.
        int oldRegions = Math.Max(1, baseline.Document.Regions.Count);
        return double.IsFinite(newLoss) &&
               newLoss < oldLoss * .999 &&
               candidate.Document.Regions.Count <= oldRegions * 1.65 + 8;
    }

    private static double Nearest(Lab target, IReadOnlyList<PaletteEntry> palette)
    {
        double nearest = double.MaxValue;
        foreach (var p in palette)
            nearest = Math.Min(nearest,
                Oklab.DistSq(target, Oklab.FromRgb(p.Color.R, p.Color.G, p.Color.B)));
        return nearest;
    }

    private static double LabelColorLoss(RasterImage source, TraceResult trace,
        CancellationToken ct)
    {
        int w = source.Width, h = source.Height;
        var px = source.Pixels;
        var colors = trace.Document.Palette.Select(c =>
            Oklab.FromRgb(c.Color.R, c.Color.G, c.Color.B)).ToArray();
        double loss = 0;
        int count = 0;
        for (int y = 0; y < h; y += 4)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x += 4)
            {
                int pixel = y * w + x, i = pixel * 4;
                if (px[i + 3] < 128) continue;
                int label = trace.LabelMap[pixel];
                if ((uint)label >= (uint)colors.Length) return double.PositiveInfinity;
                Lab original = Oklab.FromRgb(px[i], px[i + 1], px[i + 2]);
                loss += Oklab.DistSq(original, colors[label]);
                count++;
            }
        }
        return count == 0 ? double.PositiveInfinity : loss / count;
    }
}
