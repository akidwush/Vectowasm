namespace Vecto.Core;

/// <summary>Measured changes from opt-in, source-aware region cleanup.</summary>
public readonly record struct RegionMergeStats(
    int RegionsBefore, int RegionsAfter, int MergedRegions, int RelabeledPixels,
    int TopologyRejectedRegions = 0);

/// <summary>
/// Reassigns ONLY tiny connected components when their source pixels support
/// one dominant, much larger 4-connected neighbour. Not a global morphology
/// close/open: sharp high-contrast islands, thin strokes, alpha boundaries and
/// large intentional regions are left untouched. Works on label maps BEFORE
/// the boundary graph, so every final shared edge is generated just once.
/// </summary>
public static class RegionMergeRefiner
{
    // Squared Oklab thresholds; Oklab white-to-black distance is roughly one.
    private const double MaxPaletteDistanceSquared = 0.12 * 0.12;
    private const double MaxMeanTargetError = 0.09 * 0.09;
    private const double AllowedExtraError = 0.026 * 0.026;
    private const double SolidCoreError = 0.025 * 0.025;

    public static RegionMergeStats Refine(
        RasterImage image, int[] labels, IReadOnlyList<PaletteEntry> palette,
        int maxArea = 24, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(palette);
        if (labels.Length != image.Width * image.Height)
            throw new ArgumentException("Label map dimensions mismatch.", nameof(labels));
        if (maxArea is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(maxArea), "Allowed range: 1..128.");
        for (int i = 0; i < labels.Length; i++)
            if (labels[i] < -1 || labels[i] >= palette.Count)
                throw new ArgumentOutOfRangeException(nameof(labels), "Unknown palette label.");
        var seg = Segmenter.Components(labels, image.Width, image.Height);
        int rc = seg.RegionCount;
        if (rc == 0 || palette.Count < 2)
            return new RegionMergeStats(rc, rc, 0, 0);

        int w = image.Width, h = image.Height;
        var lab = palette.Select(p => Oklab.FromRgb(p.Color.R, p.Color.G, p.Color.B)).ToArray();
        var eligible = new bool[rc];
        for (int r = 0; r < rc; r++)
            eligible[r] = seg.RegionArea[r] <= maxArea;
        if (!eligible.Any(x => x))
            return new RegionMergeStats(rc, rc, 0, 0);

        // A tiny component can touch more than two colors diagonally even when
        // its direct boundary is dominated by one color. Such pixel-corner
        // junctions must not move: they can alter planar loop topology.
        var atJunction = RegionTopologyGuardV1.MarkJunctionComponents(
            seg.RegionId, w, h, eligible, cancellationToken);
        var topologyRejected = new bool[rc];
        var totalEdges = new int[rc];
        var meanL = new double[rc];
        var meanA = new double[rc];
        var meanB = new double[rc];
        var ownCost = new double[rc];
        var coreCount = new int[rc];
        var minX = Enumerable.Repeat(int.MaxValue, rc).ToArray();
        var minY = Enumerable.Repeat(int.MaxValue, rc).ToArray();
        var maxX = new int[rc];
        var maxY = new int[rc];

        // Count 4-neighbour contacts, including transparency and out-of-bounds
        // in the denominator: a speck on a transparent boundary is NOT a
        // candidate merely because one solid neighbour remains.
        var contacts = new Dictionary<long, int>();
        void Adjacent(int a, int b)
        {
            if (a == b) return;
            if (a >= 0 && eligible[a])
            {
                totalEdges[a]++;
                if (b >= 0)
                {
                    long key = ((long)a << 32) | (uint)b;
                    contacts[key] = contacts.GetValueOrDefault(key) + 1;
                }
            }
            if (b >= 0 && eligible[b])
            {
                totalEdges[b]++;
                if (a >= 0)
                {
                    long key = ((long)b << 32) | (uint)a;
                    contacts[key] = contacts.GetValueOrDefault(key) + 1;
                }
            }
        }

        var pixels = image.Pixels;
        for (int y = 0; y < h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, r = seg.RegionId[i];
                if (r >= 0 && eligible[r])
                {
                    int p = i * 4, l = seg.RegionPalette[r];
                    Lab c = Oklab.FromRgb(pixels[p], pixels[p + 1], pixels[p + 2]);
                    Lab own = lab[l];
                    meanL[r] += c.L;
                    meanA[r] += c.A;
                    meanB[r] += c.B;
                    double error = Oklab.DistSq(c, own);
                    ownCost[r] += error;
                    if (error < SolidCoreError) coreCount[r]++;
                    minX[r] = Math.Min(minX[r], x);
                    maxX[r] = Math.Max(maxX[r], x);
                    minY[r] = Math.Min(minY[r], y);
                    maxY[r] = Math.Max(maxY[r], y);
                }
                if (x + 1 < w) Adjacent(r, seg.RegionId[i + 1]);
                else Adjacent(r, -1);
                if (y + 1 < h) Adjacent(r, seg.RegionId[i + w]);
                else Adjacent(r, -1);
                if (x == 0) Adjacent(r, -1);
                if (y == 0) Adjacent(r, -1);
            }
        }

        // For every small region choose ONE large neighbour by score; do not
        // allow small-to-small chains or iterative label swaps.
        var selected = new int[rc];
        var strongestBorder = new int[rc];
        var bestTargetError = new double[rc];
        Array.Fill(selected, -1);
        Array.Fill(bestTargetError, double.PositiveInfinity);
        foreach (var (edge, border) in contacts)
        {
            int r = (int)(edge >> 32), neighbor = (int)(edge & 0xffffffff);
            if (r == neighbor || !eligible[r] || seg.RegionArea[neighbor] <= maxArea)
                continue;
            int area = seg.RegionArea[r];
            if (seg.RegionArea[neighbor] < area * 3 || border * 5 < totalEdges[r] * 3)
                continue; // >=60% border dominance and a stable target
            int ownLabel = seg.RegionPalette[r], targetLabel = seg.RegionPalette[neighbor];
            if (ownLabel == targetLabel) continue;
            Lab own = lab[ownLabel], target = lab[targetLabel];
            if (Oklab.DistSq(own, target) > MaxPaletteDistanceSquared)
                continue;

            double ownMeanSq = ownCost[r] / area;
            double ml = meanL[r] / area, ma = meanA[r] / area, mb = meanB[r] / area;
            // E[|pixel-target|²] from the known E[|pixel-own|²],
            // without scanning the same pixels for each possible neighbour.
            double dl = target.L - own.L, da = target.A - own.A, db = target.B - own.B;
            double targetMeanSq = ownMeanSq
                + dl * dl + da * da + db * db
                - 2 * ((ml - own.L) * dl + (ma - own.A) * da + (mb - own.B) * db);
            if (targetMeanSq > MaxMeanTargetError
                || targetMeanSq > ownMeanSq + AllowedExtraError)
                continue;
            // Protect real flat details even when they're tiny (pupil,
            // highlights, outlined letters, or coloured punctuation).
            if (coreCount[r] * 2 >= area
                && targetMeanSq > ownMeanSq + SolidCoreError / 2)
                continue;
            int width = maxX[r] - minX[r] + 1, height = maxY[r] - minY[r] + 1;
            if (Math.Min(width, height) <= 2 && Math.Max(width, height) >= 6
                && targetMeanSq > ownMeanSq - SolidCoreError)
                continue; // preserve long 1-2 px strokes

            // Only erase an *internal* isolated mislabeled island enclosed by
            // one single stable component. If the island borders a third
            // color, transparency, the image frame or a 2x2 junction, merging
            // it can change another region's outline/holes. Keep it intact.
            if (atJunction[r] || border != totalEdges[r])
            {
                topologyRejected[r] = true;
                continue;
            }

            if (border > strongestBorder[r]
                || (border == strongestBorder[r]
                    && (targetMeanSq < bestTargetError[r] - 1e-10
                        || (Math.Abs(targetMeanSq - bestTargetError[r]) <= 1e-10
                            && neighbor < selected[r]))))
            {
                selected[r] = neighbor;
                strongestBorder[r] = border;
                bestTargetError[r] = targetMeanSq;
            }
        }

        // Keep global source-pixel drift tightly bounded; if over budget,
        // the most border-supported, best evidenced candidates win.
        int budget = Math.Max(1, labels.Length / 1000); // <=0.1% of source pixels
        var candidates = Enumerable.Range(0, rc)
            .Where(r => selected[r] >= 0)
            .OrderByDescending(r => (double)strongestBorder[r] / totalEdges[r])
            .ThenBy(r => bestTargetError[r])
            .ThenBy(r => r)
            .ToArray();
        var toLabel = new int[rc];
        Array.Fill(toLabel, -1);
        int relabeled = 0;
        foreach (int r in candidates)
        {
            int area = seg.RegionArea[r];
            if (relabeled + area > budget) continue;
            toLabel[r] = seg.RegionPalette[selected[r]];
            relabeled += area;
        }
        int rejected = topologyRejected.Count(x => x);
        if (relabeled == 0) return new RegionMergeStats(rc, rc, 0, 0, rejected);

        int changed = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            int r = seg.RegionId[i];
            if (r >= 0 && toLabel[r] >= 0)
            {
                labels[i] = toLabel[r];
                changed++;
            }
        }
        int after = Segmenter.Components(labels, w, h).RegionCount;
        // One accepted source component must disappear into exactly one
        // 4-connected target component. Any stronger count change indicates
        // a bridge or unintended graph-topology edit. Restore ALL changes.
        int accepted = toLabel.Count(x => x >= 0);
        if (rc - after != accepted)
        {
            for (int i = 0; i < labels.Length; i++)
            {
                int r = seg.RegionId[i];
                if (r >= 0 && toLabel[r] >= 0)
                    labels[i] = seg.RegionPalette[r];
            }
            return new RegionMergeStats(rc, rc, 0, 0, rejected + accepted);
        }
        return new RegionMergeStats(rc, after, accepted, changed, rejected);
    }
}
