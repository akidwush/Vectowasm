namespace Vecto.Core;

/// <summary>
/// Statistics for opt-in per-chain inverse pixel-area refinement.
/// No palette, region membership or graph connectivity is modified.
/// </summary>
public readonly record struct InverseAreaStats(
    int EligiblePoints, int MovedPoints, int RejectedPoints,
    int SrgbPoints, int LinearPoints);

/// <summary>
/// Reconstruct a locally straight boundary from anti-aliased pixel-box coverage.
/// Pixel colours are unmixed in both sRGB and exact linear-light RGB; a conservative
/// off-axis residual gate chooses the colour model for each local neighbourhood.
/// Coverage is then inverted through the *exact* unit-square half-plane area
/// formula (not Marching Squares or a 50% bilinear crossing).
///
/// Applied once per existing shared-edge chain BEFORE the Ekstracev1 G1 fitter.
/// Region graph, topology, corners, junction endpoints and Original/V3 are intact.
/// A local bounded objective gate rejects uncertain/noisy geometry.
/// </summary>
public static class EkstraceInverseArea
{
    private const double MinContrastSq = 65 * 65;
    private const double MaxOffAxisResidual = 0.026; // residual / squared palette contrast
    private static readonly double[] Linear = Enumerable.Range(0, 256)
        .Select(i =>
        {
            double c = i / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }).ToArray();

    private readonly record struct Observation(double Offset, double Alpha, double Weight);
    private readonly record struct PixelSample(double Offset, double Along,
        double AlphaSrgb, double ErrSrgb, double AlphaLinear, double ErrLinear);

    public static double SrgbToLinear(byte channel) => Linear[channel];

    /// <summary>
    /// Given coverage of region A and normal pointing into A, return the signed
    /// distance of the PIXEL CENTRE into A from the straight edge.
    /// </summary>
    public static double SignedDistance(double alpha, double nx, double ny)
    {
        if (!double.IsFinite(alpha) || !double.IsFinite(nx) || !double.IsFinite(ny))
            return double.NaN;
        double mag = Math.Sqrt(nx * nx + ny * ny);
        if (mag < 1e-12) return double.NaN;
        double a = Math.Max(Math.Abs(nx), Math.Abs(ny)) / mag;
        double b = Math.Min(Math.Abs(nx), Math.Abs(ny)) / mag;
        alpha = Math.Clamp(alpha, 0, 1);
        double h = (a + b) * .5;
        if (b < 1e-12) return (alpha - .5) * a;
        double t = b / (2 * a);
        if (alpha < t) return Math.Sqrt(2 * alpha * a * b) - h;
        if (alpha > 1 - t) return h - Math.Sqrt(2 * (1 - alpha) * a * b);
        return (alpha - .5) * a;
    }

    /// <summary>Area of a unit pixel box on the positive side of a line.</summary>
    public static double PixelCoverage(double signedDistance, double nx, double ny)
    {
        if (!double.IsFinite(signedDistance) || !double.IsFinite(nx) || !double.IsFinite(ny))
            return double.NaN;
        double mag = Math.Sqrt(nx * nx + ny * ny);
        if (mag < 1e-12) return double.NaN;
        double a = Math.Max(Math.Abs(nx), Math.Abs(ny)) / mag;
        double b = Math.Min(Math.Abs(nx), Math.Abs(ny)) / mag;
        double d = signedDistance;
        double h = (a + b) * .5;
        if (d <= -h) return 0;
        if (d >= h) return 1;
        if (b < 1e-12) return Math.Clamp(.5 + d / a, 0, 1);
        double k = (a - b) * .5;
        if (d < -k) return (d + h) * (d + h) / (2 * a * b);
        if (d > k) return 1 - (h - d) * (h - d) / (2 * a * b);
        return .5 + d / a;
    }

    /// <summary>
    /// Colour projection with an explicit unmixing model and rejection score.
    /// Residual is dimensionless squared distance to the two-ink axis.
    /// </summary>
    public static (double Alpha, double RelativeResidual) Project(
        Rgba32 pixel, Rgba32 left, Rgba32 right, bool linearLight)
    {
        double ar = linearLight ? Linear[left.R] : left.R / 255.0;
        double ag = linearLight ? Linear[left.G] : left.G / 255.0;
        double ab = linearLight ? Linear[left.B] : left.B / 255.0;
        double br = linearLight ? Linear[right.R] : right.R / 255.0;
        double bg = linearLight ? Linear[right.G] : right.G / 255.0;
        double bb = linearLight ? Linear[right.B] : right.B / 255.0;
        double pr = linearLight ? Linear[pixel.R] : pixel.R / 255.0;
        double pg = linearLight ? Linear[pixel.G] : pixel.G / 255.0;
        double pb = linearLight ? Linear[pixel.B] : pixel.B / 255.0;
        double dr = ar - br, dg = ag - bg, db = ab - bb;
        double denom = dr * dr + dg * dg + db * db;
        if (denom < 1e-8) return (double.NaN, double.PositiveInfinity);
        double alpha = Math.Clamp(((pr - br) * dr + (pg - bg) * dg +
                                    (pb - bb) * db) / denom, 0, 1);
        double er = pr - (br + alpha * dr);
        double eg = pg - (bg + alpha * dg);
        double eb = pb - (bb + alpha * db);
        return (alpha, (er * er + eg * eg + eb * eb) / denom);
    }

    public static InverseAreaStats Refine(List<Vec2> points, bool closed,
        IReadOnlyList<int> protectedCorners, RasterImage source,
        Rgba32? left, Rgba32? right, double maxShiftPx = .25,
        CancellationToken cancellation = default)
    {
        if (left is not { } l || right is not { } r) return default;
        // Both ink sides must be actually opaque; do not infer transparency
        // or thin centre-lines from this two-colour half-plane model.
        if (l.A < 250 || r.A < 250 || !double.IsFinite(maxShiftPx) ||
            maxShiftPx <= 0 || maxShiftPx > .35) return default;
        double dr = l.R - r.R, dg = l.G - r.G, db = l.B - r.B;
        if (dr * dr + dg * dg + db * db < MinContrastSq) return default;
        int n = closed ? points.Count - 1 : points.Count;
        if (n < 7 || n > 4096) return default;

        var pin = new bool[n];
        foreach (int corner in protectedCorners)
        {
            if ((uint)corner >= (uint)n) continue;
            for (int step = -2; step <= 2; step++)
            {
                int k = corner + step;
                if (closed) k = (k % n + n) % n;
                if ((uint)k < (uint)n) pin[k] = true;
            }
        }
        if (!closed)
        {
            for (int i = 0; i < Math.Min(3, n); i++)
            {
                pin[i] = true;
                pin[n - 1 - i] = true;
            }
        }

        // Geometry decisions must use a single immutable snapshot; one moved
        // point must not move its neighbour's reference frame.
        var original = points.Take(n).ToArray();
        int eligible = 0, moved = 0, rejected = 0, srgb = 0, linear = 0;
        for (int i = 0; i < n; i++)
        {
            if (pin[i]) continue;
            if ((i & 127) == 0) cancellation.ThrowIfCancellationRequested();
            Vec2 p = original[i];
            Vec2 prev = original[i == 0 ? n - 1 : i - 1];
            Vec2 next = original[i + 1 == n ? 0 : i + 1];
            Vec2 incoming = (p - prev).Normalized();
            Vec2 outgoing = (next - p).Normalized();
            if (incoming.LengthSq < .9 || outgoing.LengthSq < .9 ||
                incoming.Dot(outgoing) < .92) continue;
            Vec2 tangent = (next - prev).Normalized();
            if (tangent.LengthSq < .9) continue;
            Vec2 normal = new(tangent.Y, -tangent.X); // into left region
            eligible++;

            var samples = new List<PixelSample>(16);
            int x0 = (int)Math.Floor(p.X);
            int y0 = (int)Math.Floor(p.Y);
            for (int y = y0 - 2; y <= y0 + 2; y++)
            for (int x = x0 - 2; x <= x0 + 2; x++)
            {
                if ((uint)x >= (uint)source.Width || (uint)y >= (uint)source.Height) continue;
                Vec2 relative = new Vec2(x + .5 - p.X, y + .5 - p.Y);
                double along = relative.Dot(tangent), off = relative.Dot(normal);
                if (Math.Abs(along) > 1.5 || Math.Abs(off) > 1.6) continue;
                Rgba32 c = source.GetPixel(x, y);
                if (c.A < 250) continue;
                var s = Project(c, l, r, false);
                var t = Project(c, l, r, true);
                if (s.RelativeResidual > MaxOffAxisResidual &&
                    t.RelativeResidual > MaxOffAxisResidual) continue;
                samples.Add(new PixelSample(off, along, s.Alpha, s.RelativeResidual,
                                            t.Alpha, t.RelativeResidual));
            }
            if (samples.Count < 4) { rejected++; continue; }

            // Choose one colour-space interpretation per local boundary rather
            // than switching per pixel. Grayscale blends often cannot tell the
            // models apart: default to sRGB when evidence is ambiguous.
            double scoreS = 0, scoreL = 0;
            int mixedS = 0, mixedL = 0;
            foreach (var sample in samples)
            {
                if (sample.AlphaSrgb > .04 && sample.AlphaSrgb < .96 &&
                    sample.ErrSrgb <= MaxOffAxisResidual)
                { scoreS += sample.ErrSrgb; mixedS++; }
                if (sample.AlphaLinear > .04 && sample.AlphaLinear < .96 &&
                    sample.ErrLinear <= MaxOffAxisResidual)
                { scoreL += sample.ErrLinear; mixedL++; }
            }
            bool useLinear = mixedL >= 2 && mixedS >= 2 &&
                scoreS / mixedS - scoreL / mixedL > .002 &&
                scoreL / mixedL < .75 * (scoreS / mixedS);
            var observations = new List<Observation>(16);
            double positive = 0, negative = 1, weightedInverse = 0, sumWeights = 0;
            foreach (var sample in samples)
            {
                double alpha = useLinear ? sample.AlphaLinear : sample.AlphaSrgb;
                double error = useLinear ? sample.ErrLinear : sample.ErrSrgb;
                if (!double.IsFinite(alpha) || error > MaxOffAxisResidual) continue;
                if (sample.Offset > .35) positive = Math.Max(positive, alpha);
                if (sample.Offset < -.35) negative = Math.Min(negative, alpha);
                if (alpha <= .04 || alpha >= .96) continue;
                double weight = Math.Max(.05, 4 * alpha * (1 - alpha)) /
                                (1 + 12 * error);
                observations.Add(new Observation(sample.Offset, alpha, weight));
                weightedInverse += weight * (sample.Offset -
                    SignedDistance(alpha, normal.X, normal.Y));
                sumWeights += weight;
            }
            if (observations.Count < 2 || positive < negative + .25 ||
                sumWeights <= 0)
            { rejected++; continue; }
            // Outlier guard: a triple junction, JPEG halo or strong bend often
            // gives mutually inconsistent inferred offsets.
            double mean = weightedInverse / sumWeights, variance = 0;
            foreach (var sample in observations)
            {
                double offset = sample.Offset - SignedDistance(
                    sample.Alpha, normal.X, normal.Y);
                variance += sample.Weight * (offset - mean) * (offset - mean);
            }
            variance /= sumWeights;
            if (!double.IsFinite(mean) || variance > .045)
            { rejected++; continue; }

            double Objective(double shift)
            {
                double score = 0;
                foreach (var sample in observations)
                {
                    double predicted = PixelCoverage(sample.Offset - shift,
                        normal.X, normal.Y);
                    double residual = predicted - sample.Alpha;
                    score += sample.Weight * residual * residual;
                }
                return score / sumWeights + .003 * shift * shift;
            }
            // Bounded one-dimensional optimization. Analytic inverse gives
            // an initial position; exact pixel-box forward model validates it.
            double lo = Math.Max(-maxShiftPx, mean - .15);
            double hi = Math.Min(maxShiftPx, mean + .15);
            if (hi <= lo + .001) { rejected++; continue; }
            const double golden = .6180339887498949;
            double u = hi - (hi - lo) * golden;
            double v = lo + (hi - lo) * golden;
            double fu = Objective(u), fv = Objective(v);
            for (int k = 0; k < 17; k++)
            {
                if (fu < fv) { hi = v; v = u; fv = fu; u = hi - (hi - lo) * golden; fu = Objective(u); }
                else { lo = u; u = v; fu = fv; v = lo + (hi - lo) * golden; fv = Objective(v); }
            }
            double shift = Math.Clamp((lo + hi) * .5, -maxShiftPx, maxShiftPx);
            double before = Objective(0), after = Objective(shift);
            if (Math.Abs(shift) < .008 || before < .00015 ||
                after + .000025 >= before * .85)
            { rejected++; continue; }
            Vec2 candidate = p + normal * shift;
            if ((candidate - prev).LengthSq < .04 || (next - candidate).LengthSq < .04 ||
                (candidate - prev).Dot(next - candidate) <= 0)
            { rejected++; continue; }
            points[i] = candidate;
            moved++;
            if (useLinear) linear++; else srgb++;
        }
        if (closed) points[n] = points[0];
        return new InverseAreaStats(eligible, moved, rejected, srgb, linear);
    }
}
