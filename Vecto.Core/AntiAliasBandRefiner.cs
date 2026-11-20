namespace Vecto.Core;

/// <summary>Diagnostics for the conservative, optional two-sided AA-fringe pass.</summary>
public readonly record struct AntiAliasBandStats(
    int RegionsBefore,
    int RegionsAfter,
    int BandsConsidered,
    int BandsReassigned,
    int PixelsReassigned,
    int ProtectedBands,
    bool RolledBack);

/// <summary>
/// Removes narrow, *varying-color* anti-alias transition bands that form an
/// entire palette region between two stable colors. The old 3x3 fringe pass
/// can leave the center of a multi-pixel band untreated; this pass works on
/// whole connected components and keeps their label edits atomic.
///
/// Never touches transparency, image-frame regions, colored flat cores or
/// broad gradients. Applies only to Blended/non-textured tracing, opt-in.
/// </summary>
public static class AntiAliasBandRefiner
{
    private const double MaxBandThickness = 3.0;
    private const double MinAnchorDistanceSq = 0.15 * 0.15;
    private const double MaxPaletteResidualSq = 0.055 * 0.055;
    private const double MaxRgbResidualSq = 18.0 * 18.0;
    private const double CoreColorDistanceSq = 0.018 * 0.018;
    private const double MinBlendSpan = 0.22;
    private const int MinBandLength = 8;
    private const int MaxBandArea = 8192;

    public static AntiAliasBandStats Refine(
        RasterImage img, int[] labels,
        IReadOnlyList<PaletteEntry> palette,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(palette);
        int w = img.Width, h = img.Height;
        if (w <= 0 || h <= 0 || labels.Length != w * h)
            throw new ArgumentException("Source/label dimensions mismatch.");
        for (int i = 0; i < labels.Length; i++)
            if (labels[i] < -1 || labels[i] >= palette.Count)
                throw new ArgumentOutOfRangeException(nameof(labels), "Invalid palette index.");

        var components = Segmenter.Components(labels, w, h);
        int regionCount = components.RegionCount;
        if (regionCount < 3 || palette.Count < 3)
            return new AntiAliasBandStats(regionCount, regionCount, 0, 0, 0, 0, false);

        var eligible = new bool[regionCount];
        int eligibleCount = 0;
        for (int r = 0; r < regionCount; r++)
        {
            eligible[r] = components.RegionArea[r] >= MinBandLength
                && components.RegionArea[r] <= MaxBandArea;
            if (eligible[r]) eligibleCount++;
        }
        if (eligibleCount == 0)
            return new AntiAliasBandStats(regionCount, regionCount, 0, 0, 0, 0, false);

        var pixelsByRegion = new List<int>[regionCount];
        var borders = new Dictionary<int, int>[regionCount];
        var perimeter = new int[regionCount];
        var minX = new int[regionCount];
        var maxX = new int[regionCount];
        var minY = new int[regionCount];
        var maxY = new int[regionCount];
        var touchesVoid = new bool[regionCount];
        Array.Fill(minX, int.MaxValue);
        Array.Fill(minY, int.MaxValue);

        void Contact(int region, int neighbor)
        {
            if (region < 0 || !eligible[region] || region == neighbor) return;
            perimeter[region]++;
            if (neighbor < 0)
            {
                touchesVoid[region] = true;
                return;
            }
            // Count neighbor *component ids* (not only palette colors), so an
            // apparent strip between disconnected pieces cannot join them.
            var b = borders[region] ??= new Dictionary<int, int>();
            b[neighbor] = b.GetValueOrDefault(neighbor) + 1;
        }

        for (int y = 0; y < h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, r = components.RegionId[i];
                if (r >= 0 && eligible[r])
                {
                    (pixelsByRegion[r] ??= new List<int>()).Add(i);
                    minX[r] = Math.Min(minX[r], x);
                    maxX[r] = Math.Max(maxX[r], x);
                    minY[r] = Math.Min(minY[r], y);
                    maxY[r] = Math.Max(maxY[r], y);
                }
                int right = x + 1 < w ? components.RegionId[i + 1] : -1;
                int below = y + 1 < h ? components.RegionId[i + w] : -1;
                if (x + 1 < w)
                {
                    Contact(r, right);
                    Contact(right, r);
                }
                else Contact(r, -1);
                if (y + 1 < h)
                {
                    Contact(r, below);
                    Contact(below, r);
                }
                else Contact(r, -1);
                if (x == 0) Contact(r, -1);
                if (y == 0) Contact(r, -1);
            }
        }

        var paletteLab = palette.Select(p => Oklab.FromRgb(
            p.Color.R, p.Color.G, p.Color.B)).ToArray();
        // Source map frozen while all candidate decisions are made.
        var sourceLabels = (int[])labels.Clone();
        var planned = new int[labels.Length];
        Array.Fill(planned, -1);

        int considered = 0, accepted = 0, protectedCount = 0;
        int pixelBudget = Math.Max(1, labels.Length / 100); // at most 1%
        int changed = 0;
        for (int r = 0; r < regionCount; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!eligible[r]) continue;
            considered++;
            var neighbors = borders[r];
            var members = pixelsByRegion[r];
            if (touchesVoid[r] || neighbors is null || neighbors.Count != 2 ||
                members is null || perimeter[r] <= 0)
            {
                protectedCount++;
                continue;
            }
            var sides = neighbors.OrderBy(pair => pair.Key).ToArray();
            int aRegion = sides[0].Key, bRegion = sides[1].Key;
            int area = components.RegionArea[r];
            int sideA = sides[0].Value, sideB = sides[1].Value;
            int labelA = components.RegionPalette[aRegion];
            int labelB = components.RegionPalette[bRegion];
            int ownLabel = components.RegionPalette[r];
            if (labelA == labelB || labelA == ownLabel || labelB == ownLabel ||
                components.RegionArea[aRegion] <= area * 2 ||
                components.RegionArea[bRegion] <= area * 2 ||
                sideA * 6 < perimeter[r] || sideB * 6 < perimeter[r] ||
                Math.Max(maxX[r] - minX[r] + 1, maxY[r] - minY[r] + 1) < MinBandLength ||
                2.0 * area / perimeter[r] > MaxBandThickness ||
                Oklab.DistSq(paletteLab[labelA], paletteLab[labelB]) < MinAnchorDistanceSq ||
                changed + area > pixelBudget)
            {
                protectedCount++;
                continue;
            }

            // The palette's third color must itself lie close to the line
            // joining the two stable RGB/Oklab anchor colors.
            var labA = paletteLab[labelA];
            var labB = paletteLab[labelB];
            var labC = paletteLab[ownLabel];
            double ul = labB.L - labA.L, ua = labB.A - labA.A, ub = labB.B - labA.B;
            double ul2 = ul * ul + ua * ua + ub * ub;
            double palT = ((labC.L - labA.L) * ul
                + (labC.A - labA.A) * ua
                + (labC.B - labA.B) * ub) / ul2;
            double palDl = labC.L - labA.L - palT * ul;
            double palDa = labC.A - labA.A - palT * ua;
            double palDb = labC.B - labA.B - palT * ub;
            if (palT < 0.05 || palT > 0.95 ||
                palDl * palDl + palDa * palDa + palDb * palDb > MaxPaletteResidualSq)
            {
                protectedCount++;
                continue;
            }

            var ca = palette[labelA].Color;
            var cb = palette[labelB].Color;
            double dr = cb.R - ca.R, dg = cb.G - ca.G, db = cb.B - ca.B;
            double denom = dr * dr + dg * dg + db * db;
            double minT = 1, maxT = 0;
            int sourceCore = 0, supported = 0;
            var assignments = new int[members.Count];
            var src = img.Pixels;
            for (int j = 0; j < members.Count; j++)
            {
                int pixel = members[j] * 4;
                double r0 = src[pixel] - ca.R;
                double g0 = src[pixel + 1] - ca.G;
                double b0 = src[pixel + 2] - ca.B;
                double t = Math.Clamp((r0 * dr + g0 * dg + b0 * db) / denom, 0, 1);
                double er = r0 - t * dr, eg = g0 - t * dg, eb = b0 - t * db;
                if (er * er + eg * eg + eb * eb <= MaxRgbResidualSq * 3 &&
                    t > 0.04 && t < 0.96)
                    supported++;
                minT = Math.Min(minT, t);
                maxT = Math.Max(maxT, t);
                assignments[j] = t < 0.5 ? labelA : labelB;

                var color = Oklab.FromRgb(src[pixel], src[pixel + 1], src[pixel + 2]);
                if (Oklab.DistSq(color, labC) <= CoreColorDistanceSq)
                    sourceCore++;
            }

            // Genuine uniform mid-color fill (even if geometrically narrow)
            // must not be mistaken for antialias. Need a clear source ramp
            // spanning both sides, not just one smooth-colored stroke.
            if (supported * 10 < members.Count * 9 ||
                sourceCore * 4 >= members.Count ||
                maxT - minT < MinBlendSpan ||
                assignments.All(l => l == labelA) ||
                assignments.All(l => l == labelB))
            {
                protectedCount++;
                continue;
            }

            // Simultaneous replacement; every candidate is a whole component,
            // never independently toggled pixels from other components.
            for (int j = 0; j < members.Count; j++)
                planned[members[j]] = assignments[j];
            changed += members.Count;
            accepted++;
        }

        if (accepted == 0)
            return new AntiAliasBandStats(regionCount, regionCount, considered,
                0, 0, protectedCount, false);

        for (int i = 0; i < labels.Length; i++)
            if (planned[i] >= 0) labels[i] = planned[i];

        // Topology guard: the AA band should disappear as one component,
        // without connecting previously separate stable regions or creating
        // new islands. Reject *all* candidates on unexpected changes.
        int after = Segmenter.Components(labels, w, h).RegionCount;
        if (regionCount - after != accepted)
        {
            Array.Copy(sourceLabels, labels, labels.Length);
            return new AntiAliasBandStats(regionCount, regionCount, considered,
                0, 0, protectedCount + accepted, true);
        }
        return new AntiAliasBandStats(regionCount, after, considered,
            accepted, changed, protectedCount, false);
    }
}
