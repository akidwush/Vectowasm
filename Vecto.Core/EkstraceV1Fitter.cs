using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Vecto.Core;

/// <summary>
/// Ekstracev1: experimental, independent contour-to-cubic reconstruction.
/// Uses the existing shared-edge region graph, with chord-length parameters,
/// least-squares tangent-constrained handles, Newton reparameterization,
/// protected corners, and adaptive max-error splitting.
/// Not Vector Magic source code and not a new raster segmentation pipeline.
/// </summary>
public static class EkstraceV1Fitter
{
    private const int MaxPoints = 4096;
    private const int MaxDepth = 22;
    private const double Epsilon = 1e-12;

    public static List<CubicBezier> FitChain(List<Vec2> points, bool closed,
        List<int> protectedCorners, double toleranceSquared, CancellationToken cancellation = default)
    {
        if (points.Count < 2) return new List<CubicBezier>();
        if (!double.IsFinite(toleranceSquared) || toleranceSquared <= 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceSquared));
        // Bound cost on very long edges and leave unsuitable input with the proven fitter.
        if (points.Count > MaxPoints || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            return BezierFitter.FitChain(points, closed, protectedCorners, toleranceSquared);

        var samples = new List<Vec2>(points);
        if (closed && samples[0].DistSq(samples[^1]) > 1e-12)
            samples.Add(samples[0]);
        if (closed && samples.Count < 5)
            return BezierFitter.FitChain(points, closed, protectedCorners, toleranceSquared);

        int end = samples.Count - 1;
        var corners = new SortedSet<int>(protectedCorners.Where(k => k > 0 && k < end));
        if (closed)
        {
            int n = end;
            var cornerIndices = protectedCorners.Where(k => k >= 0 && k < n).Distinct().OrderBy(k => k).ToArray();
            if (cornerIndices.Length > 0)
            {
                // A true corner makes a safe closed-loop seam (G0 there).
                int pivot = cornerIndices[0];
                var rotated = new List<Vec2>(n + 1);
                for (int i = 0; i < n; i++) rotated.Add(samples[(pivot + i) % n]);
                rotated.Add(rotated[0]);
                samples = rotated;
                corners = new SortedSet<int>(
                    cornerIndices.Select(k => (k - pivot + n) % n).Where(k => k > 0));
                corners.Add(0);
                corners.Add(n);
            }
        }

        var knots = new SortedSet<int> { 0, end };
        knots.UnionWith(corners);
        if (closed && corners.Count == 0)
        {
            // Whole rings have coincident endpoints: seed four spans, not one
            // degenerate zero-chord cubic. Every smooth seam shares its tangent.
            for (int j = 1; j < 4; j++)
            {
                int k = end * j / 4;
                if (k > 0 && k < end) knots.Add(k);
            }
        }

        var output = new List<CubicBezier>();
        var ordered = knots.ToArray();
        for (int i = 0; i + 1 < ordered.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            FitSpan(samples, closed, corners, ordered[i], ordered[i + 1],
                toleranceSquared, output, 0, cancellation);
        }

        if (output.Count == 0 || output.Count > MaxPoints ||
            output.Any(c => !Finite(c.P0) || !Finite(c.P1) || !Finite(c.P2) || !Finite(c.P3)))
            return BezierFitter.FitChain(points, closed, protectedCorners, toleranceSquared);

        // Shared chains must retain the original junction endpoints; for a
        // closed ring the seam may move only to another existing corner.
        if (!closed &&
            (output[0].P0.DistSq(points[0]) > 1e-12 ||
             output[^1].P3.DistSq(points[^1]) > 1e-12))
            return BezierFitter.FitChain(points, closed, protectedCorners, toleranceSquared);
        if (closed && output[0].P0.DistSq(output[^1].P3) > 1e-12)
            return BezierFitter.FitChain(points, closed, protectedCorners, toleranceSquared);
        return output;
    }

    private static bool Finite(Vec2 v) => double.IsFinite(v.X) && double.IsFinite(v.Y);
    private static Vec2 Unit(Vec2 v) => v.LengthSq > Epsilon ? v.Normalized() : new Vec2(1, 0);

    private static Vec2 Tangent(IReadOnlyList<Vec2> p, bool closed, SortedSet<int> corners,
        int at, bool outgoing)
    {
        int end = p.Count - 1;
        Vec2 prev = at == 0 && closed ? p[end - 1] : p[Math.Max(0, at - 1)];
        Vec2 next = at == end && closed ? p[1] : p[Math.Min(end, at + 1)];
        Vec2 incoming = Unit(p[at] - prev);
        Vec2 forward = Unit(next - p[at]);
        if (!closed && at == 0) return forward;
        if (!closed && at == end) return incoming;
        if (corners.Contains(at)) return outgoing ? forward : incoming;
        Vec2 sum = incoming + forward;
        return sum.LengthSq > Epsilon ? sum.Normalized() : (outgoing ? forward : incoming);
    }

    private static double[] ChordParameters(IReadOnlyList<Vec2> p, int first, int last)
    {
        var u = new double[last - first + 1];
        for (int j = 1; j < u.Length; j++)
            u[j] = u[j - 1] + (p[first + j] - p[first + j - 1]).Length;
        double total = u[^1];
        if (total < Epsilon)
        {
            for (int j = 1; j < u.Length; j++) u[j] = j / (double)(u.Length - 1);
        }
        else
        {
            for (int j = 1; j < u.Length; j++) u[j] /= total;
        }
        return u;
    }

    // Nonnegative least-squares for two endpoint handle lengths (Stage 6 G1).
    private static (double Left, double Right) Nonnegative2(
        double a, double b, double c, double r1, double r2)
    {
        double best = double.PositiveInfinity, x = 0, y = 0;
        void Candidate(double xx, double yy)
        {
            if (xx < 0 || yy < 0 || !double.IsFinite(xx) || !double.IsFinite(yy)) return;
            double cost = a * xx * xx + 2 * b * xx * yy + c * yy * yy
                          - 2 * r1 * xx - 2 * r2 * yy;
            if (cost < best) { best = cost; x = xx; y = yy; }
        }
        Candidate(0, 0);
        if (a > Epsilon) Candidate(Math.Max(0, r1 / a), 0);
        if (c > Epsilon) Candidate(0, Math.Max(0, r2 / c));
        double det = a * c - b * b;
        if (det > 1e-13 * a * c)
            Candidate((r1 * c - r2 * b) / det, (r2 * a - r1 * b) / det);
        return (x, y);
    }

    private static CubicBezier FitG1(IReadOnlyList<Vec2> samples, int first, int last,
        IReadOnlyList<double> u, Vec2 startTangent, Vec2 endTangent)
    {
        Vec2 start = samples[first], end = samples[last];
        double a = 0, b = 0, c = 0, r1 = 0, r2 = 0;
        for (int i = first; i <= last; i++)
        {
            double t = u[i - first], v = 1 - t;
            double b0 = v * v * v, b1 = 3 * v * v * t;
            double b2 = 3 * v * t * t, b3 = t * t * t;
            Vec2 residual = samples[i] -
                (start * (b0 + b1) + end * (b2 + b3));
            Vec2 d0 = startTangent * b1;
            Vec2 d1 = endTangent * -b2;
            a += d0.Dot(d0); b += d0.Dot(d1); c += d1.Dot(d1);
            r1 += d0.Dot(residual); r2 += d1.Dot(residual);
        }
        var (alpha, beta) = Nonnegative2(a, b, c, r1, r2);
        double chord = (end - start).Length;
        double maxHandle = Math.Max(0.01, chord * 2);
        alpha = Math.Clamp(alpha, 0, maxHandle);
        beta = Math.Clamp(beta, 0, maxHandle);
        return new CubicBezier(start, start + startTangent * alpha,
            end - endTangent * beta, end);
    }

    private static Vec2 Velocity(CubicBezier c, double t)
    {
        double v = 1 - t;
        return (c.P1 - c.P0) * (3 * v * v) +
               (c.P2 - c.P1) * (6 * v * t) +
               (c.P3 - c.P2) * (3 * t * t);
    }

    private static Vec2 Acceleration(CubicBezier c, double t) =>
        (c.P2 - c.P1 * 2 + c.P0) * (6 * (1 - t)) +
        (c.P3 - c.P2 * 2 + c.P1) * (6 * t);

    private static void RefineParameters(IReadOnlyList<Vec2> p, int first,
        double[] u, CubicBezier curve)
    {
        var previous = (double[])u.Clone();
        for (int j = 1; j < u.Length - 1; j++)
        {
            double t = previous[j];
            Vec2 target = p[first + j];
            for (int iteration = 0; iteration < 10; iteration++)
            {
                Vec2 displacement = curve.Eval(t) - target;
                Vec2 v = Velocity(curve, t);
                double denominator = v.LengthSq + displacement.Dot(Acceleration(curve, t));
                if (Math.Abs(denominator) < Epsilon) break;
                double trial = Math.Clamp(t - Math.Clamp(displacement.Dot(v) / denominator, -0.15, 0.15), 0, 1);
                if (curve.Eval(trial).DistSq(target) > curve.Eval(t).DistSq(target)) break;
                if (Math.Abs(trial - t) < 1e-10) break;
                t = trial;
            }
            double low = u[j - 1] + 1e-9;
            double high = previous[j + 1] - 1e-9;
            if (low < high) u[j] = Math.Clamp(t, low, high);
        }
        u[0] = 0;
        u[^1] = 1;
    }

    private static double DistanceToSegmentSq(Vec2 p, Vec2 a, Vec2 b)
    {
        Vec2 d = b - a;
        if (d.LengthSq < Epsilon) return p.DistSq(a);
        double t = Math.Clamp((p - a).Dot(d) / d.LengthSq, 0, 1);
        return p.DistSq(a + d * t);
    }

    private static bool IsStraight(IReadOnlyList<Vec2> p, int first, int last, double tolSq)
    {
        if (p[first].DistSq(p[last]) < Epsilon) return false;
        for (int i = first + 1; i < last; i++)
            if (DistanceToSegmentSq(p[i], p[first], p[last]) > tolSq * 0.5)
                return false;
        return true;
    }

    private static (double Worst, int Split) SampleError(IReadOnlyList<Vec2> p, int first,
        int last, IReadOnlyList<double> u, CubicBezier curve)
    {
        double worst = -1;
        int split = (first + last) / 2;
        for (int i = first + 1; i < last; i++)
        {
            double dist = curve.Eval(u[i - first]).DistSq(p[i]);
            if (dist > worst) { worst = dist; split = i; }
        }
        return (Math.Max(0, worst), split);
    }

    private static bool RespectsSource(IReadOnlyList<Vec2> p, int first, int last,
        CubicBezier curve, double tolSq)
    {
        // Samples on the curve must stay close to the original polyline too.
        // This guards against overshoot between source vertices.
        double limit = Math.Max(tolSq * 4, 0.25);
        for (int j = 1; j < 16; j++)
        {
            Vec2 pos = curve.Eval(j / 16.0);
            double nearest = double.PositiveInfinity;
            for (int i = first; i < last; i++)
                nearest = Math.Min(nearest, DistanceToSegmentSq(pos, p[i], p[i + 1]));
            if (nearest > limit) return false;
        }
        return true;
    }

    private static void FitSpan(IReadOnlyList<Vec2> p, bool closed, SortedSet<int> corners,
        int first, int last, double tolSq, List<CubicBezier> output, int depth,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (last <= first) return;
        if (last == first + 1)
        {
            output.Add(CubicBezier.Line(p[first], p[last]));
            return;
        }
        if (depth >= MaxDepth)
        {
            for (int i = first; i < last; i++)
                output.Add(CubicBezier.Line(p[i], p[i + 1]));
            return;
        }
        if (IsStraight(p, first, last, tolSq))
        {
            output.Add(CubicBezier.Line(p[first], p[last]));
            return;
        }

        Vec2 t0 = Tangent(p, closed, corners, first, true);
        Vec2 t1 = Tangent(p, closed, corners, last, false);
        var u = ChordParameters(p, first, last);
        CubicBezier curve = FitG1(p, first, last, u, t0, t1);
        var (worst, split) = SampleError(p, first, last, u, curve);
        for (int pass = 0; pass < 3 && worst > tolSq; pass++)
        {
            RefineParameters(p, first, u, curve);
            curve = FitG1(p, first, last, u, t0, t1);
            (worst, split) = SampleError(p, first, last, u, curve);
        }
        if (worst <= tolSq && RespectsSource(p, first, last, curve, tolSq) &&
            Finite(curve.P1) && Finite(curve.P2))
        {
            output.Add(curve);
            return;
        }
        split = Math.Clamp(split, first + 1, last - 1);
        FitSpan(p, closed, corners, first, split, tolSq, output, depth + 1, cancellation);
        FitSpan(p, closed, corners, split, last, tolSq, output, depth + 1, cancellation);
    }
}
