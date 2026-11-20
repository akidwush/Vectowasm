namespace Vecto.Core;

/// <summary>
/// Experimental local edge re-projection AFTER the final smoothing pass.
/// Uses the source image's two-colour coverage gradient, but only adjusts
/// well-supported smooth interior points and never moves junction endpoints,
/// structural corners, or their immediate neighbors. Shared-edge chains are
/// adjusted once so adjacent region paths still reuse identical geometry.
/// Default OFF; no pixel resampling, topology changes, global translation,
/// new SVG groups, or palette changes.
/// </summary>
public static class LocalEdgeRefiner
{
    public static int Refine(List<Vec2> points, bool closed,
        IReadOnlyList<int> structuralCorners,
        RasterImage source, Rgba32? left, Rgba32? right,
        double maxShiftPx = 0.20)
    {
        if (left is not { } l || right is not { } r)
            return 0; // Do not infer transparency transitions from RGB.
        double dr = l.R - r.R, dg = l.G - r.G, db = l.B - r.B;
        if (dr * dr + dg * dg + db * db < 65 * 65)
            return 0;

        int n = closed ? points.Count - 1 : points.Count;
        if (n < 7 || !double.IsFinite(maxShiftPx) ||
            maxShiftPx < 0.05 || maxShiftPx > 0.35)
            return 0;

        var locked = new bool[n];
        foreach (int c in structuralCorners)
        {
            if ((uint)c >= (uint)n) continue;
            // At high-curvature sharp tips, even a sub-pixel shift may
            // change the tangent and erase the tip. Protect its shoulders.
            for (int d = -2; d <= 2; d++)
            {
                int k = c + d;
                if (closed) k = (k % n + n) % n;
                if ((uint)k < (uint)n) locked[k] = true;
            }
        }
        if (!closed)
        {
            for (int i = 0; i < Math.Min(3, n); i++)
            {
                locked[i] = true;
                locked[n - 1 - i] = true;
            }
        }

        var initial = points.Take(n).ToArray();
        int moved = 0;
        for (int i = 0; i < n; i++)
        {
            if (locked[i]) continue;
            Vec2 prev = initial[i == 0 ? n - 1 : i - 1];
            Vec2 p = initial[i];
            Vec2 next = initial[i + 1 == n ? 0 : i + 1];
            Vec2 incoming = (p - prev).Normalized();
            Vec2 outgoing = (next - p).Normalized();
            // Avoid reprojecting narrow spikes, cusps or tiny jitter runs.
            if (incoming.LengthSq < .5 || outgoing.LengthSq < .5 ||
                incoming.Dot(outgoing) < .88)
                continue;
            Vec2 tangent = (next - prev).Normalized();
            if (tangent.LengthSq < .8) continue;
            Vec2 normal = new(tangent.Y, -tangent.X);

            double minus = ChainGeometry.CoverLeft(source, left, right, p - normal * .5);
            double middle = ChainGeometry.CoverLeft(source, left, right, p);
            double plus = ChainGeometry.CoverLeft(source, left, right, p + normal * .5);
            if (!double.IsFinite(minus) || !double.IsFinite(middle) ||
                !double.IsFinite(plus))
                continue;

            // Expected coverage goes from RIGHT (0) to LEFT (1).
            // A reliable two-colour transition should bracket 50%.
            double gradient = plus - minus;
            if (gradient < .25 || minus > .48 || plus < .52 ||
                middle < .12 || middle > .88)
                continue;
            double proposed = (.5 - middle) / gradient;
            if (!double.IsFinite(proposed) || Math.Abs(proposed) < .012)
                continue;
            double delta = Math.Clamp(proposed, -maxShiftPx, maxShiftPx);
            Vec2 candidate = p + normal * delta;
            double after = ChainGeometry.CoverLeft(source, left, right, candidate);
            if (!double.IsFinite(after)) continue;
            // Insist on a meaningful coverage improvement after a small
            // regularization for point drift. No change for ambiguous edges.
            if (Math.Abs(after - .5) + .012 * Math.Abs(delta) + .004
                >= Math.Abs(middle - .5))
                continue;
            points[i] = candidate;
            moved++;
        }

        if (closed) points[n] = points[0];
        return moved;
    }
}
