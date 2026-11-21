namespace Vecto.Core;

/// <summary>Result of source-color detail protection BEFORE the first small-region pass.</summary>
public readonly record struct DetailProtectionStats(int ProtectedRegions, int ProtectedPixels);

public sealed class DetailProtectionMask
{
    public required bool[] Pixels { get; init; }
    public DetailProtectionStats Stats { get; init; }
}

/// <summary>
/// Conservative, source-backed detail mask. An isolated bright eye reflection,
/// black pupil or thin real stroke is allowed to survive the legacy size-only
/// AbsorbSmallRegions pass when the source pixels are genuinely close to their
/// own palette paint and perceptually distinct from adjacent paints.
/// No source RGBA or palette color is changed.
/// </summary>
public static class DetailProtectionV2
{
    public static DetailProtectionMask Capture(
        RasterImage image, int[] labels, Lab[] centers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(centers);
        int w = image.Width, h = image.Height;
        if (labels.Length != w * h) throw new ArgumentException("Label dimensions mismatch.");
        var seg = Segmenter.Components(labels, w, h);
        int count = seg.RegionCount;
        var core = new int[count];
        var perimeter = new int[count];
        var minX = Enumerable.Repeat(int.MaxValue, count).ToArray();
        var minY = Enumerable.Repeat(int.MaxValue, count).ToArray();
        var maxX = new int[count];
        var maxY = new int[count];
        var contrast = new bool[count];
        byte[] rgba = image.Pixels;

        void Contact(int region, int other)
        {
            if (region < 0 || region == other) return;
            perimeter[region]++;
            if (other >= 0 &&
                Oklab.DistSq(centers[seg.RegionPalette[region]],
                             centers[seg.RegionPalette[other]]) >= 0.13f * 0.13f)
                contrast[region] = true;
        }

        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, region = seg.RegionId[i];
                if (region < 0) continue;
                int p = i * 4;
                if (rgba[p + 3] == 255 &&
                    Oklab.DistSq(Oklab.FromRgb(rgba[p], rgba[p + 1], rgba[p + 2]),
                                 centers[seg.RegionPalette[region]]) < 0.035f * 0.035f)
                    core[region]++;
                minX[region] = Math.Min(minX[region], x);
                maxX[region] = Math.Max(maxX[region], x);
                minY[region] = Math.Min(minY[region], y);
                maxY[region] = Math.Max(maxY[region], y);
                Contact(region, x == 0 ? -1 : seg.RegionId[i - 1]);
                Contact(region, x + 1 == w ? -1 : seg.RegionId[i + 1]);
                Contact(region, y == 0 ? -1 : seg.RegionId[i - w]);
                Contact(region, y + 1 == h ? -1 : seg.RegionId[i + w]);
            }
        }

        var keep = new bool[count];
        int kept = 0, protectedPixels = 0;
        for (int r = 0; r < count; r++)
        {
            int area = seg.RegionArea[r];
            if (area < 1 || perimeter[r] < 1 || !contrast[r] ||
                core[r] < Math.Ceiling(area * 0.70)) continue;
            int dx = maxX[r] - minX[r] + 1, dy = maxY[r] - minY[r] + 1;
            double aspect = (double)Math.Max(dx, dy) / Math.Max(1, Math.Min(dx, dy));
            double thickness = 2.0 * area / perimeter[r];
            bool tinyDetail = area <= 96;
            bool credibleStroke = area <= 512 && aspect >= 4.0 && thickness <= 1.6;
            if (!tinyDetail && !credibleStroke) continue;
            keep[r] = true;
            kept++;
            protectedPixels += area;
        }
        var mask = new bool[labels.Length];
        if (kept > 0)
            for (int i = 0; i < mask.Length; i++)
                if (seg.RegionId[i] >= 0 && keep[seg.RegionId[i]]) mask[i] = true;
        return new DetailProtectionMask
        {
            Pixels = mask,
            Stats = new DetailProtectionStats(kept, protectedPixels)
        };
    }
}
