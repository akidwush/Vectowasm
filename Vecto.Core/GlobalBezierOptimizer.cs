namespace Vecto.Core;

/// <summary>
/// Bounded dynamic-programming simplification of already-fitted shared-edge
/// contours. Optimizes across several C/L segments at once, rather than only
/// adjacent pairs. All errors are measured against the unchanged original
/// fitted outline. Endpoint positions and protected structural corners survive.
/// 
/// This is NOT raster-gradient optimization or a copy of ALIV/DiffVG.
/// The work is bounded per chunk to keep CPU-only WebAssembly responsive.
/// </summary>
public static class GlobalBezierOptimizer
{
    private const int MaxChunk = 72;
    private const int MaxMergeSpan = 8;
    private const int SamplesPerPiece = 8;
    private const int CandidateSamples = 48;
    private const double SmoothJoinDot = 0.84;
    private const double SignificantTurnDot = 0.72;
    private const double ArcWindowPixels = 5.0;

    public static List<CubicBezier> Reduce(
        IReadOnlyList<CubicBezier> baseline,
        IReadOnlySet<Vec2>? protectedCorners,
        double maxDeviationPx)
    {
        var original = baseline.ToArray();
        if (original.Length < 2 || !double.IsFinite(maxDeviationPx)
            || maxDeviationPx <= 0) return original.ToList();

        double tolerance = Math.Clamp(maxDeviationPx, 0.15, 0.8);
        var barriers = new bool[original.Length + 1];
        barriers[0] = barriers[^1] = true;
        for (int k = 1; k < original.Length; k++)
        {
            bool pin = protectedCorners is not null &&
                protectedCorners.Contains(original[k - 1].P3);
            barriers[k] = pin || IsStructuralTurn(original, k);
        }

        var output = new List<CubicBezier>(original.Length);
        for (int start = 0; start < original.Length; start += MaxChunk)
        {
            int stop = Math.Min(start + MaxChunk, original.Length);
            output.AddRange(OptimizeChunk(original, start, stop,
                barriers, tolerance));
        }
        return output.Count < original.Length ? output : original.ToList();
    }

    private static List<CubicBezier> OptimizeChunk(
        CubicBezier[] source, int start, int stop,
        bool[] barriers, double tolerance)
    {
        int length = stop - start;
        var dpCount = new int[length + 1];
        var dpError = new double[length + 1];
        var next = new int[length];
        var selected = new CubicBezier[length];

        for (int i = length - 1; i >= 0; i--)
        {
            int absolute = start + i;
            dpCount[i] = 1 + dpCount[i + 1];
            dpError[i] = dpError[i + 1];
            next[i] = i + 1;
            selected[i] = source[absolute];

            // Candidate interval [absolute, end); never cross a genuine
            // corner or T-junction. DP minimizes segment count first and
            // maximum-distance cost second (no arbitrary byte-size metric).
            int far = Math.Min(length, i + MaxMergeSpan);
            for (int j = i + 2; j <= far; j++)
            {
                int end = start + j;
                bool crossesCorner = false;
                for (int k = absolute + 1; k < end; k++)
                {
                    if (barriers[k]) { crossesCorner = true; break; }
                }
                if (crossesCorner) break;

                int proposedCount = 1 + dpCount[j];
                if (proposedCount > dpCount[i]) continue;

                if (!TryFitRange(source, absolute, end, barriers,
                    tolerance, out var candidate, out double error))
                    continue;

                double proposedError = error + dpError[j];
                if (proposedCount < dpCount[i]
                    || (proposedCount == dpCount[i]
                        && proposedError + 1e-9 < dpError[i]))
                {
                    dpCount[i] = proposedCount;
                    dpError[i] = proposedError;
                    selected[i] = candidate;
                    next[i] = j;
                }
            }
        }

        var optimized = new List<CubicBezier>(dpCount[0]);
        for (int i = 0; i < length; i = next[i])
            optimized.Add(selected[i]);
        return optimized;
    }

    private static bool TryFitRange(
        CubicBezier[] pieces, int start, int stop, bool[] barriers,
        double tolerance, out CubicBezier result, out double maxError)
    {
        result = default;
        maxError = double.PositiveInfinity;
        var data = new Vec2[(stop - start) * SamplesPerPiece + 1];
        for (int c = start; c < stop; c++)
            for (int s = 0; s <= SamplesPerPiece; s++)
                data[(c - start) * SamplesPerPiece + s] =
                    pieces[c].Eval(s / (double)SamplesPerPiece);

        var chord = pieces[stop - 1].P3 - pieces[start].P0;
        double chordLength = chord.Length;
        if (chordLength < 0.2) return false;

        var startTangent = StartDirection(pieces, start, barriers);
        var endTangent = EndDirection(pieces, stop, barriers);
        if (startTangent.LengthSq < 0.8 || endTangent.LengthSq < 0.8)
            return false;

        var u = new double[data.Length];
        double distance = 0;
        for (int i = 1; i < data.Length; i++)
        {
            distance += (data[i] - data[i - 1]).Length;
            u[i] = distance;
        }
        if (distance < 0.3) return false;
        for (int i = 1; i < u.Length; i++) u[i] /= distance;

        if (!LeastSquares(data, u, startTangent, endTangent,
            chordLength, out var candidate)) return false;

        // Bounded Schneider-style Newton reparameterization of the point
        // locations, while retaining monotone order of the original samples.
        for (int iteration = 0; iteration < 6; iteration++)
        {
            var refined = (double[])u.Clone();
            for (int i = 1; i < u.Length - 1; i++)
            {
                double lower = (u[i - 1] + u[i]) * 0.5;
                double upper = (u[i] + u[i + 1]) * 0.5;
                refined[i] = Math.Clamp(Newton(candidate, data[i], u[i]),
                    lower, upper);
            }
            if (!LeastSquares(data, refined, startTangent, endTangent,
                chordLength, out var improved)) break;
            candidate = improved;
            u = refined;
        }

        // Two-way contour distance catches thin spikes, bulges and loops
        // that can be missed by a simple mean squared error.
        var fitted = new Vec2[CandidateSamples + 1];
        for (int i = 0; i <= CandidateSamples; i++)
            fitted[i] = candidate.Eval(i / (double)CandidateSamples);
        double error = Math.Max(MaxDistance(data, fitted, tolerance),
            MaxDistance(fitted, data, tolerance));
        if (error > tolerance * tolerance) return false;

        result = candidate;
        maxError = error;
        return true;
    }

    private static bool LeastSquares(
        Vec2[] data, double[] u, Vec2 startDir, Vec2 endDir,
        double chordLength, out CubicBezier result)
    {
        result = default;
        double m00 = 0, m01 = 0, m11 = 0, r0 = 0, r1 = 0;
        Vec2 first = data[0], last = data[^1];

        for (int i = 1; i < data.Length - 1; i++)
        {
            double t = u[i], mt = 1 - t;
            double b0 = mt * mt * mt, b1 = 3 * mt * mt * t;
            double b2 = 3 * mt * t * t, b3 = t * t * t;
            var a0 = startDir * b1;
            var a1 = endDir * (-b2);
            var delta = data[i] - (first * (b0 + b1) + last * (b2 + b3));
            m00 += a0.Dot(a0);
            m01 += a0.Dot(a1);
            m11 += a1.Dot(a1);
            r0 += a0.Dot(delta);
            r1 += a1.Dot(delta);
        }

        double determinant = m00 * m11 - m01 * m01;
        if (determinant < 1e-10) return false;
        double h0 = (r0 * m11 - r1 * m01) / determinant;
        double h1 = (m00 * r1 - m01 * r0) / determinant;
        if (!double.IsFinite(h0) || !double.IsFinite(h1)
            || h0 <= 0 || h1 <= 0
            || h0 > 2.5 * chordLength || h1 > 2.5 * chordLength)
            return false;
        result = new CubicBezier(first,
            first + startDir * h0,
            last - endDir * h1, last);
        return true;
    }

    private static double Newton(CubicBezier c, Vec2 point, double t)
    {
        double mt = 1 - t;
        var first = (c.P1 - c.P0) * 3;
        var second = (c.P2 - c.P1) * 3;
        var third = (c.P3 - c.P2) * 3;
        Vec2 derivative = first * (mt * mt)
                        + second * (2 * mt * t)
                        + third * (t * t);
        Vec2 acceleration = (second - first) * (2 * mt)
                          + (third - second) * (2 * t);
        var diff = c.Eval(t) - point;
        double den = derivative.Dot(derivative) + diff.Dot(acceleration);
        if (Math.Abs(den) < 1e-10) return t;
        return Math.Clamp(t - diff.Dot(derivative) / den, 0, 1);
    }

    // Maximum squared distance to a polyline, with an early exit on failure.
    private static double MaxDistance(Vec2[] points, Vec2[] polyline,
        double tolerance)
    {
        double limit = tolerance * tolerance;
        double worst = 0;
        foreach (var point in points)
        {
            double best = double.PositiveInfinity;
            for (int j = 1; j < polyline.Length; j++)
            {
                var from = polyline[j - 1];
                var edge = polyline[j] - from;
                double lengthSq = edge.LengthSq;
                double t = lengthSq < 1e-20 ? 0 :
                    Math.Clamp((point - from).Dot(edge) / lengthSq, 0, 1);
                double error = point.DistSq(from + edge * t);
                if (error < best) best = error;
            }
            if (best > worst) worst = best;
            if (worst > limit) return worst;
        }
        return worst;
    }

    private static Vec2 StartDirection(CubicBezier[] source, int k,
        bool[] barriers)
    {
        var outbound = Unit(source[k].P1 - source[k].P0,
            source[k].P3 - source[k].P0);
        if (k == 0 || barriers[k]) return outbound;
        var inbound = Unit(source[k - 1].P3 - source[k - 1].P2,
            source[k - 1].P3 - source[k - 1].P0);
        return SmoothDirection(inbound, outbound);
    }

    private static Vec2 EndDirection(CubicBezier[] source, int k,
        bool[] barriers)
    {
        var inbound = Unit(source[k - 1].P3 - source[k - 1].P2,
            source[k - 1].P3 - source[k - 1].P0);
        if (k == source.Length || barriers[k]) return inbound;
        var outbound = Unit(source[k].P1 - source[k].P0,
            source[k].P3 - source[k].P0);
        return SmoothDirection(inbound, outbound);
    }

    private static Vec2 SmoothDirection(Vec2 inbound, Vec2 outbound)
    {
        if (inbound.Dot(outbound) < SmoothJoinDot) return inbound;
        return (inbound + outbound).Normalized();
    }

    private static Vec2 Unit(Vec2 tangent, Vec2 fallback)
    {
        var dir = tangent.Normalized();
        return dir.LengthSq > 0.5 ? dir : fallback.Normalized();
    }

    // Local 1-pixel staircase turns are not automatically structural
    // corners. Test the direction several pixels along the contour before
    // deciding whether the DP must preserve the join.
    private static bool IsStructuralTurn(CubicBezier[] curves, int joint)
    {
        var prev = Unit(curves[joint - 1].P3 - curves[joint - 1].P2,
            curves[joint - 1].P3 - curves[joint - 1].P0);
        var next = Unit(curves[joint].P1 - curves[joint].P0,
            curves[joint].P3 - curves[joint].P0);
        if (prev.Dot(next) >= SmoothJoinDot) return false;

        var vertex = curves[joint - 1].P3;
        var left = ArcLookback(curves, joint, ArcWindowPixels, false);
        var right = ArcLookback(curves, joint, ArcWindowPixels, true);
        var before = (vertex - left).Normalized();
        var after = (right - vertex).Normalized();
        // If the lookaround is too short to make a reliable decision,
        // retain the joint conservatively.
        if (before.LengthSq < 0.9 || after.LengthSq < 0.9)
            return true;
        return before.Dot(after) < SignificantTurnDot;
    }

    private static Vec2 ArcLookback(CubicBezier[] curves, int joint,
        double distance, bool forward)
    {
        Vec2 pos = curves[joint - 1].P3;
        if (forward)
        {
            for (int s = joint; s < curves.Length; s++)
            {
                Vec2 prev = curves[s].P0;
                for (int k = 1; k <= SamplesPerPiece; k++)
                {
                    var next = curves[s].Eval(k / (double)SamplesPerPiece);
                    double len = (next - prev).Length;
                    if (len >= distance && len > 1e-12)
                        return prev + (next - prev) * (distance / len);
                    distance -= len;
                    pos = next;
                    prev = next;
                }
            }
        }
        else
        {
            for (int s = joint - 1; s >= 0; s--)
            {
                Vec2 prev = curves[s].P3;
                for (int k = SamplesPerPiece - 1; k >= 0; k--)
                {
                    var next = curves[s].Eval(k / (double)SamplesPerPiece);
                    double len = (next - prev).Length;
                    if (len >= distance && len > 1e-12)
                        return prev + (next - prev) * (distance / len);
                    distance -= len;
                    pos = next;
                    prev = next;
                }
            }
        }
        return pos;
    }
}
