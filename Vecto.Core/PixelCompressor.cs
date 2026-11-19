namespace Vecto.Core;

/// <summary>
/// Conservative in-memory pixel cleanup for all decoded images. Only pixels
/// safely inside locally flat, opaque regions are snapped to a frequently
/// observed source color. Isolated low-contrast defects may be repaired.
/// Pixel dimensions, transparency, high-contrast edges, and the uploaded bytes
/// are never changed. This does not re-encode/compress the original file.
/// </summary>
public static class PixelCompressor
{
    private const int ColorBins = 4096;
    private const int AnchorLimit = 48;
    private const int FlatTolerance = 20;
    private const int SnapMaxDistanceSq = 324; // <= 18 RGB Euclidean units
    private const int CompetitionMarginSq = 144;

    public static PixelCompressionResult Prepare(RasterImage image)
    {
        // Every image follows this pipeline, independent of encoded byte size.
        // No-op on tiny rasters, gradients and photographs when the evidence
        // for safe flat-color cleanup is insufficient.
        int width = image.Width, height = image.Height;
        if (width < 3 || height < 3)
            return new PixelCompressionResult(image, false, 0, 0);
        byte[] src = image.Pixels;
        var counts = new int[ColorBins];
        var sumsR = new long[ColorBins];
        var sumsG = new long[ColorBins];
        var sumsB = new long[ColorBins];
        int totalFlat = 0;

        // Find real fill colors from uniform, fully opaque regions; ignore
        // anti-aliased boundaries so they do not become false palette entries.
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = (y * width + x) * 4;
                if (!IsFlat(src, i, width, FlatTolerance)) continue;
                int bin = ((src[i] >> 4) << 8) | ((src[i + 1] >> 4) << 4) | (src[i + 2] >> 4);
                counts[bin]++;
                sumsR[bin] += src[i];
                sumsG[bin] += src[i + 1];
                sumsB[bin] += src[i + 2];
                totalFlat++;
            }
        }

        // Confirm that this looks like graphic artwork, not a photo or a
        // smooth gradient. A few stable bins must dominate the flat interiors.
        // We do not force any artwork to a fixed 2/3/16-color palette.
        int minCount = Math.Max(32, totalFlat / 2500);
        var candidates = new List<int>();
        for (int b = 0; b < ColorBins; b++)
            if (counts[b] >= minCount)
                candidates.Add(b);
        candidates.Sort((a, b) =>
        {
            int c = counts[b].CompareTo(counts[a]);
            return c != 0 ? c : a.CompareTo(b);
        });
        long topEight = 0;
        for (int k = 0; k < Math.Min(8, candidates.Count); k++)
            topEight += counts[candidates[k]];
        if (totalFlat < Math.Max(64, width * (long)height / 10)
            || topEight < totalFlat * 55L / 100L)
            return new PixelCompressionResult(image, false, 0, 0);

        var anchors = new List<(byte R, byte G, byte B)>(AnchorLimit);
        foreach (int b in candidates)
        {
            int n = counts[b];
            byte r = (byte)((sumsR[b] + n / 2) / n);
            byte g = (byte)((sumsG[b] + n / 2) / n);
            byte bl = (byte)((sumsB[b] + n / 2) / n);
            // Nearby histogram bins often represent one true print color.
            // Keep the most supported of those bins as its anchor.
            var lab = Oklab.FromRgb(r, g, bl);
            bool duplicate = false;
            foreach (var existing in anchors)
            {
                if (Oklab.DistSq(lab,
                    Oklab.FromRgb(existing.R, existing.G, existing.B)) < 0.0016f)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate) continue;
            anchors.Add((r, g, bl));
            if (anchors.Count == AnchorLimit) break;
        }

        // Work on a copy: the initial preview and original RGBA never change.
        byte[] dst = (byte[])src.Clone();
        int changed = 0;
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = (y * width + x) * 4;
                if (src[i + 3] != 255) continue;

                // Repair isolated low-amplitude noise only if the four axial
                // neighbours agree. High-contrast dots and thin strokes stay.
                if (TryRepairSpeck(src, i, width, out byte rr, out byte gg, out byte bb))
                {
                    if (rr != src[i] || gg != src[i + 1] || bb != src[i + 2])
                    {
                        dst[i] = rr; dst[i + 1] = gg; dst[i + 2] = bb;
                        changed++;
                    }
                    continue;
                }

                // Snap interiors only. Boundary and transparent pixels are not
                // modified, preserving geometry and color transition positions.
                if (anchors.Count == 0 || !IsFlat(src, i, width, FlatTolerance)) continue;
                int best = int.MaxValue, second = int.MaxValue, chosen = -1;
                for (int k = 0; k < anchors.Count; k++)
                {
                    var a = anchors[k];
                    int dr = src[i] - a.R, dg = src[i + 1] - a.G, db = src[i + 2] - a.B;
                    int d = dr * dr + dg * dg + db * db;
                    if (d < best) { second = best; best = d; chosen = k; }
                    else if (d < second) second = d;
                }
                if (chosen < 0 || best > SnapMaxDistanceSq || second - best < CompetitionMarginSq)
                    continue;

                var target = anchors[chosen];
                if (target.R == src[i] && target.G == src[i + 1] && target.B == src[i + 2])
                    continue;
                dst[i] = target.R;
                dst[i + 1] = target.G;
                dst[i + 2] = target.B;
                changed++;
            }
        }

        // Hard quality cap. Even if a future tuning change becomes too
        // aggressive, prefer exact original pixels over unintended drift.
        long sumChannelError = 0;
        int maxChannelError = 0;
        for (int i = 0; i < src.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                int error = Math.Abs(src[i + c] - dst[i + c]);
                sumChannelError += error;
                if (error > maxChannelError) maxChannelError = error;
            }
        }
        if (maxChannelError > 24 ||
            sumChannelError > (long)width * height * 3 * 3)
            return new PixelCompressionResult(image, false, 0, anchors.Count);

        return new PixelCompressionResult(
            changed == 0 ? image : new RasterImage(width, height, dst),
            changed > 0,
            changed,
            anchors.Count);
    }

    private static bool IsFlat(byte[] px, int i, int width, int tolerance)
    {
        if (px[i + 3] != 255) return false;
        int row = width * 4;
        return Close(px, i, i - 4, tolerance)
            && Close(px, i, i + 4, tolerance)
            && Close(px, i, i - row, tolerance)
            && Close(px, i, i + row, tolerance);
    }

    private static bool Close(byte[] px, int a, int b, int tolerance) =>
        px[b + 3] == 255
        && Math.Abs((int)px[a] - px[b]) <= tolerance
        && Math.Abs((int)px[a + 1] - px[b + 1]) <= tolerance
        && Math.Abs((int)px[a + 2] - px[b + 2]) <= tolerance;

    private static bool TryRepairSpeck(byte[] px, int i, int width,
        out byte r, out byte g, out byte b)
    {
        int step = width * 4;
        int a = i - 4, c = i + 4, u = i - step, d = i + step;
        r = g = b = 0;
        if (px[a + 3] != 255 || px[c + 3] != 255
            || px[u + 3] != 255 || px[d + 3] != 255)
            return false;
        if (!Close(px, a, c, 12) || !Close(px, a, u, 12)
            || !Close(px, a, d, 12))
            return false;
        int mr = (px[a] + px[c] + px[u] + px[d] + 2) / 4;
        int mg = (px[a + 1] + px[c + 1] + px[u + 1] + px[d + 1] + 2) / 4;
        int mb = (px[a + 2] + px[c + 2] + px[u + 2] + px[d + 2] + 2) / 4;
        int deviation = Math.Max(Math.Abs(px[i] - mr),
            Math.Max(Math.Abs(px[i + 1] - mg), Math.Abs(px[i + 2] - mb)));
        // Do not erase isolated black highlights, white holes or red accents.
        if (deviation < 8 || deviation > 24) return false;
        r = (byte)mr; g = (byte)mg; b = (byte)mb;
        return true;
    }
}

public readonly record struct PixelCompressionResult(
    RasterImage Image,
    bool Applied,
    int ChangedPixels,
    int AnchorColors);
