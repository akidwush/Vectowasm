namespace Vecto.Core;

/// <summary>
/// CPU-only complexity-reduction pass on fitted cubic Bezier chains.
/// Merge two consecutive cubics only when a replacement with endpoint
/// tangent constraints remains close in both directions and does not cross
/// a pinned corner. Shared boundary chains are processed once.
/// </summary>
public static class BezierNodeOptimizer
{
    private const int Samples = 16;
    private const double MinJointDot = 0.95;
    private const int Passes = 4;
    private const int MaxOriginalPieces = 8;

    public static List<CubicBezier> Reduce(
        IReadOnlyList<CubicBezier> input,
        IReadOnlySet<Vec2>? protectedCorners,
        double maxDeviationPx)
    {
        var result = input.ToList();
        // Track the source spans to prevent accumulated drift across merges.
        var original = input.ToArray();
        var spanStart = Enumerable.Range(0, input.Count).ToList();
        var spanEnd = Enumerable.Range(0, input.Count).ToList();
        if (result.Count < 2 || !double.IsFinite(maxDeviationPx) ||
            maxDeviationPx <= 0) return result;
        double tolerance = Math.Clamp(maxDeviationPx, 0.1, 1.0);
        for (int pass = 0; pass < Passes; pass++)
        {
            bool any = false;
            for (int i = 0; i < result.Count - 1;)
            {
                var first = result[i];
                var next = result[i + 1];
                if (protectedCorners is not null && protectedCorners.Contains(first.P3))
                {
                    i++;
                    continue;
                }
                int begin = spanStart[i], finish = spanEnd[i + 1];
                if (finish - begin + 1 > MaxOriginalPieces
                    || !TryMerge(first, next, tolerance, out var candidate)
                    || !PreservesOriginal(original, begin, finish, candidate, tolerance))
                {
                    i++;
                    continue;
                }
                result[i] = candidate;
                result.RemoveAt(i + 1);
                spanEnd[i] = finish;
                spanStart.RemoveAt(i + 1);
                spanEnd.RemoveAt(i + 1);
                any = true;
                if (i > 0) i--;
            }
            if (!any) break;
        }
        return result;
    }

    private static bool TryMerge(CubicBezier a, CubicBezier b,
        double tolerance, out CubicBezier merged)
    {
        merged = default;
        if (a.P3.DistSq(b.P0) > 1e-12) return false;
        var incoming = (a.P3 - a.P2).Normalized();
        var outgoing = (b.P1 - b.P0).Normalized();
        if (incoming.LengthSq < 0.9 || outgoing.LengthSq < 0.9 ||
            incoming.Dot(outgoing) < MinJointDot) return false;
        var start = (a.P1 - a.P0).Normalized();
        var end = (b.P3 - b.P2).Normalized();
        if (start.LengthSq < 0.9 || end.LengthSq < 0.9) return false;

        var original = new Vec2[Samples * 2 + 1];
        for (int i = 0; i <= Samples; i++)
            original[i] = a.Eval(i / (double)Samples);
        for (int i = 1; i <= Samples; i++)
            original[Samples + i] = b.Eval(i / (double)Samples);

        var u = new double[original.Length];
        double length = 0;
        for (int i = 1; i < original.Length; i++)
        {
            length += (original[i] - original[i - 1]).Length;
            u[i] = length;
        }
        double chord = (b.P3 - a.P0).Length;
        if (length < 0.5 || chord < 0.1) return false;
        for (int i = 1; i < u.Length; i++) u[i] /= length;

        // Least squares over two scalar handle lengths. Endpoint positions
        // and their tangent directions are fixed, reducing overshoot and
        // preserving continuity with neighboring fitted segments.
        double m00 = 0, m01 = 0, m11 = 0, v0 = 0, v1 = 0;
        for (int i = 1; i < original.Length - 1; i++)
        {
            double t = u[i], mt = 1 - t;
            double b1 = 3 * mt * mt * t;
            double b2 = 3 * mt * t * t;
            var basePoint = a.P0 * (mt * mt * mt + b1)
                          + b.P3 * (b2 + t * t * t);
            var x = start * b1;
            var y = end * (-b2);
            var residual = original[i] - basePoint;
            m00 += x.Dot(x);
            m01 += x.Dot(y);
            m11 += y.Dot(y);
            v0 += x.Dot(residual);
            v1 += y.Dot(residual);
        }
        double determinant = m00 * m11 - m01 * m01;
        if (determinant < 1e-9) return false;
        double alpha = (v0 * m11 - v1 * m01) / determinant;
        double beta = (m00 * v1 - m01 * v0) / determinant;
        if (!double.IsFinite(alpha) || !double.IsFinite(beta) ||
            alpha <= 0 || beta <= 0 ||
            alpha > 3 * chord || beta > 3 * chord) return false;

        var fitted = new CubicBezier(
            a.P0, a.P0 + start * alpha, b.P3 - end * beta, b.P3);

        // Validate both directions, not only MSE: this rejects loops,
        // flattened details, or bulges that would erase a silhouette.
        var reduced = new Vec2[original.Length];
        for (int i = 0; i < reduced.Length; i++)
            reduced[i] = fitted.Eval(i / (double)(reduced.Length - 1));
        double maxSq = tolerance * tolerance;
        if (!CloseTo(original, reduced, maxSq) ||
            !CloseTo(reduced, original, maxSq)) return false;
        merged = fitted;
        return true;
    }


    private static bool PreservesOriginal(
        CubicBezier[] baseline, int first, int last,
        CubicBezier candidate, double tolerance)
    {
        // Every change is compared to the *pre-optimization* geometry,
        // not to an already modified segment. This bounds cumulative drift.
        int pieces = last - first + 1;
        int samples = pieces * Samples;
        var source = new Vec2[samples + 1];
        for (int seg = 0; seg < pieces; seg++)
            for (int i = 0; i <= Samples; i++)
                source[seg * Samples + i] =
                    baseline[first + seg].Eval(i / (double)Samples);

        var merged = new Vec2[samples + 1];
        for (int i = 0; i <= samples; i++)
            merged[i] = candidate.Eval(i / (double)samples);

        double limitSq = tolerance * tolerance;
        return CloseTo(source, merged, limitSq)
            && CloseTo(merged, source, limitSq);
    }

    private static bool CloseTo(Vec2[] from, Vec2[] contour, double limitSq)
    {
        foreach (var p in from)
        {
            double closest = double.PositiveInfinity;
            for (int i = 1; i < contour.Length; i++)
            {
                var start = contour[i - 1];
                var delta = contour[i] - start;
                double length = delta.LengthSq;
                double t = length < 1e-20 ? 0 :
                    Math.Clamp((p - start).Dot(delta) / length, 0, 1);
                double dist = p.DistSq(start + delta * t);
                if (dist < closest) closest = dist;
            }
            if (closest > limitSq) return false;
        }
        return true;
    }
}
