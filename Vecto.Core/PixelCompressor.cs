namespace Vecto.Core;

/// <summary>
/// Conservative in-memory pixel cleanup for large encoded images. Only pixels
/// safely inside locally flat, opaque regions are snapped to a frequently
/// observed source color. Isolated low-contrast defects may be repaired.
/// Pixel dimensions, transparency, high-contrast edges, and the uploaded bytes
/// are never changed. This does not re-encode/compress the original file.
/// </summary>
public static class PixelCompressor
{
    // A displayed KB in the browser is 1,024 bytes.
    public const long ThresholdBytes = 900L * 1024L;
    private const int ColorBins = 4096;
    private const int AnchorLimit = 48;
    private const int FlatTolerance = 26;
    private const int SnapMaxDistanceSq = 625; // <= 25 RGB Euclidean units
    private const int CompetitionMarginSq = 144;

    public static PixelCompressionResult Prepare(RasterImage image, long encodedBytes)
    {
        if (encodedBytes < ThresholdBytes)
            return new PixelCompressionResult(image, false, 0, 0);

        int width = image.Width, height = image.Height;
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

        // Ignore weak histogram modes. On a photograph, no global color may
        // have meaningful support: no forced three-color simplification occurs.
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

        var anchors = new List<(byte R, byte G, byte B)>(AnchorLimit);
        foreach (int b in candidates)
        {
            int n = counts[b];
            byte r = (byte)((sumsR[b] + n / 2) / n);
            byte g = (byte)((sumsG[b] + n / 2) / n);
            byte bl = (byte)((sumsB[b] + n / 2) / n);
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

        return new PixelCompressionResult(
            changed == 0 ? image : new RasterImage(width, height, dst),
            true,
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
        if (deviation < 8 || deviation > 55) return false;
        r = (byte)mr; g = (byte)mg; b = (byte)mb;
        return true;
    }
}

public readonly record struct PixelCompressionResult(
    RasterImage Image,
    bool Applied,
    int ChangedPixels,
    int AnchorColors);
