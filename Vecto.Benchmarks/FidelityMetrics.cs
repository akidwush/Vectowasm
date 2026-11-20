using Vecto.Core;

/// <summary>
/// Image-ground-truth metrics computed at the *original raster dimensions*.
/// Boundary distances use exact Euclidean squared distance transforms between
/// binary foreground contours; no SVG-vs-SVG proxy comparison is used.
/// Binarization is a convenience for graphic artwork, not a general semantic
/// foreground classifier for photographs or arbitrary multicolor illustrations.
/// </summary>
internal static class FidelityMetrics
{
    internal sealed record Result(
        double RgbMae,
        double BlockSsim,
        double? MaskIou,
        double? BoundaryP99,
        double? BoundaryHausdorff,
        int ForegroundReferencePixels,
        int ForegroundOutputPixels);

    public static Rgba32 GuessCornerBackground(RasterImage image)
    {
        int w = image.Width, h = image.Height;
        var colors = new[]
        {
            Composite(image.GetPixel(0, 0), new Rgba32(255, 255, 255, 255)),
            Composite(image.GetPixel(w - 1, 0), new Rgba32(255, 255, 255, 255)),
            Composite(image.GetPixel(0, h - 1), new Rgba32(255, 255, 255, 255)),
            Composite(image.GetPixel(w - 1, h - 1), new Rgba32(255, 255, 255, 255)),
        };
        int best = 0, bestCount = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            int count = 0;
            foreach (var c in colors)
                if (DistanceSq(colors[i], c) <= 20 * 20) count++;
            if (count > bestCount) { best = i; bestCount = count; }
        }
        return colors[best];
    }

    public static Result Compare(
        RasterImage original,
        RasterImage rendered,
        Rgba32? background = null,
        double threshold = 45)
    {
        if (original.Width != rendered.Width ||
            original.Height != rendered.Height)
            throw new ArgumentException("Ground-truth comparison requires exact matching dimensions");

        Rgba32 bg = background ?? GuessCornerBackground(original);
        int w = original.Width, h = original.Height, count = checked(w * h);
        var a = new bool[count];
        var b = new bool[count];
        var lumA = new double[count];
        var lumB = new double[count];
        double mae = 0;
        double minDistSq = threshold * threshold;
        int sourceInk = 0, outputInk = 0, intersection = 0, union = 0;

        for (int i = 0; i < count; i++)
        {
            var source = Composite(original.GetPixel(i % w, i / w), bg);
            var result = Composite(rendered.GetPixel(i % w, i / w), bg);

            // Metrics are relative to the exact source raster, not a vector
            // competitor. Alpha is composited consistently over the selected bg.
            mae += Math.Abs(source.R - result.R)
                 + Math.Abs(source.G - result.G)
                 + Math.Abs(source.B - result.B);
            lumA[i] = .2126 * source.R + .7152 * source.G + .0722 * source.B;
            lumB[i] = .2126 * result.R + .7152 * result.G + .0722 * result.B;

            a[i] = DistanceSq(source, bg) > minDistSq;
            b[i] = DistanceSq(result, bg) > minDistSq;
            if (a[i]) sourceInk++;
            if (b[i]) outputInk++;
            if (a[i] && b[i]) intersection++;
            if (a[i] || b[i]) union++;
        }

        double ssim = BlockSsim(lumA, lumB, w, h);
        if (union == 0)
            return new Result(mae / (count * 3.0), ssim,
                null, null, null, sourceInk, outputInk);

        double iou = (double)intersection / union;
        if (sourceInk == 0 || outputInk == 0)
            return new Result(mae / (count * 3.0), ssim,
                iou, null, null, sourceInk, outputInk);

        var sourceEdge = Contour(a, w, h);
        var outputEdge = Contour(b, w, h);
        var toSource = DistanceField(sourceEdge, w, h);
        var toOutput = DistanceField(outputEdge, w, h);
        var distances = new List<double>();
        for (int i = 0; i < count; i++)
        {
            if (sourceEdge[i]) distances.Add(Math.Sqrt(toOutput[i]));
            if (outputEdge[i]) distances.Add(Math.Sqrt(toSource[i]));
        }
        distances.Sort();
        double? p99 = distances.Count == 0 ? null :
            distances[Math.Max(0, (int)Math.Ceiling(distances.Count * .99) - 1)];
        double? hausdorff = distances.Count == 0 ? null : distances[^1];

        return new Result(
            mae / (count * 3.0), ssim, iou, p99, hausdorff,
            sourceInk, outputInk);
    }

    private static Rgba32 Composite(Rgba32 c, Rgba32 bg)
    {
        int a = c.A;
        if (a == 255) return c;
        return new Rgba32(
            (byte)((c.R * a + bg.R * (255 - a) + 127) / 255),
            (byte)((c.G * a + bg.G * (255 - a) + 127) / 255),
            (byte)((c.B * a + bg.B * (255 - a) + 127) / 255),
            255);
    }

    private static int DistanceSq(Rgba32 a, Rgba32 b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    private static bool[] Contour(bool[] mask, int w, int h)
    {
        var edge = new bool[mask.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!mask[i]) continue;
                edge[i] =
                    x == 0 || x == w - 1 || y == 0 || y == h - 1 ||
                    !mask[i - 1] || !mask[i + 1] ||
                    !mask[i - w] || !mask[i + w];
            }
        return edge;
    }

    // Felzenszwalb-Huttenlocher exact squared Euclidean distance transform.
    // Complexity O(width*height) instead of O(number of contour pixels squared).
    private static double[] DistanceField(bool[] contour, int w, int h)
    {
        const double far = 1e12;
        var intermediate = new double[w * h];
        var result = new double[w * h];
        int cap = Math.Max(w, h);
        var input = new double[cap];
        var output = new double[cap];
        var envelope = new int[cap];
        var intersections = new double[cap + 1];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
                input[x] = contour[row + x] ? 0 : far;
            Transform1D(input, output, w, envelope, intersections);
            Array.Copy(output, 0, intermediate, row, w);
        }
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
                input[y] = intermediate[y * w + x];
            Transform1D(input, output, h, envelope, intersections);
            for (int y = 0; y < h; y++)
                result[y * w + x] = output[y];
        }
        return result;
    }

    private static void Transform1D(
        double[] f, double[] d, int n, int[] v, double[] z)
    {
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            double intersection;
            do
            {
                int vk = v[k];
                intersection = ((f[q] + (double)q * q) -
                    (f[vk] + (double)vk * vk)) / (2.0 * (q - vk));
                if (intersection > z[k]) break;
                k--;
            } while (k >= 0);
            k++;
            v[k] = q;
            z[k] = intersection;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double distance = q - v[k];
            d[q] = distance * distance + f[v[k]];
        }
    }

    // Explicit 8x8 non-overlapping luminance SSIM (population variance).
    // Not interchangeable with skimage's sliding-window SSIM.
    private static double BlockSsim(double[] a, double[] b, int w, int h)
    {
        const double c1 = 6.5025, c2 = 58.5225;
        double total = 0;
        int blocks = 0;
        for (int y = 0; y < h; y += 8)
            for (int x = 0; x < w; x += 8)
            {
                int x1 = Math.Min(w, x + 8), y1 = Math.Min(h, y + 8);
                int n = (x1 - x) * (y1 - y);
                if (n < 2) continue;
                double sa = 0, sb = 0;
                for (int yy = y; yy < y1; yy++)
                    for (int xx = x; xx < x1; xx++)
                    {
                        int i = yy * w + xx;
                        sa += a[i]; sb += b[i];
                    }
                double ma = sa / n, mb = sb / n;
                double va = 0, vb = 0, cov = 0;
                for (int yy = y; yy < y1; yy++)
                    for (int xx = x; xx < x1; xx++)
                    {
                        int i = yy * w + xx;
                        double da = a[i] - ma, db = b[i] - mb;
                        va += da * da;
                        vb += db * db;
                        cov += da * db;
                    }
                va /= n; vb /= n; cov /= n;
                double numerator = (2 * ma * mb + c1) * (2 * cov + c2);
                double denominator = (ma * ma + mb * mb + c1) * (va + vb + c2);
                total += denominator == 0 ? 1 : numerator / denominator;
                blocks++;
            }
        return blocks == 0 ? 1 : total / blocks;
    }
}
