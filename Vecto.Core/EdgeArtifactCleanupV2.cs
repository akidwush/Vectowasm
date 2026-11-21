namespace Vecto.Core;

/// <summary>Per-component diagnostic snapshot BEFORE changes reach BoundaryGraph.</summary>
public readonly record struct EdgeRegionDiagnostic(
    int RegionId, int Area, int Perimeter, double Thickness,
    double AspectRatio, int NeighborColors, double SourceCoreFraction,
    string Decision);

/// <summary>Quality-gated optional two-sided AA / JPEG-ringing cleanup metrics.</summary>
public sealed record EdgeArtifactStats
{
    public int RegionsBefore { get; init; }
    public int RegionsAfter { get; init; }
    public int ThinCandidates { get; init; }
    public int AABandsRemoved { get; init; }
    public int RingingFragmentsRemoved { get; init; }
    public int RelabeledPixels { get; init; }
    public int ProtectedCandidates { get; init; }
    public int TopologyRejections { get; init; }
    public bool RolledBack { get; init; }
    public IReadOnlyList<EdgeRegionDiagnostic> DiagnosticRegionMap { get; init; }
        = Array.Empty<EdgeRegionDiagnostic>();
}

/// <summary>
/// Source-aware V2 label-map cleanup (OFF by default). Two disjoint routes:
/// A) a varying third-color AA strip between two strong paints, including
///    disconnected short fragments; B) thin/isolated A-label ringing islands
///    on B paint whose original pixels really fit B better than A.
/// Guards: source color cores, pre-absorption detail mask, alpha and frame,
/// junction ambiguity, component-count invariant, and verified shared edges.
/// Rollback is atomic. Does not touch the original bitmap or SVG fitting.
/// </summary>
public static class EdgeArtifactCleanupV2
{
    private const int MaxBandArea = 8192;
    private const int MaxRingingArea = 128;
    private const double MaxThickness = 2.35;
    private const double MaxRgbResidualSq = 18.0 * 18.0 * 3;
    private const double MinAnchorDistanceSq = 0.12 * 0.12;

    public static EdgeArtifactStats Refine(
        RasterImage image, int[] labels, IReadOnlyList<PaletteEntry> palette,
        bool[] protectedPixels, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(protectedPixels);
        int w = image.Width, h = image.Height;
        if (labels.Length != w * h || protectedPixels.Length != labels.Length)
            throw new ArgumentException("Invalid V2 raster/mask dimensions.");
        var seg = Segmenter.Components(labels, w, h);
        int count = seg.RegionCount;
        if (count == 0 || palette.Count < 2)
            return new EdgeArtifactStats { RegionsBefore = count, RegionsAfter = count };

        var labs = palette.Select(x =>
            Oklab.FromRgb(x.Color.R, x.Color.G, x.Color.B)).ToArray();
        var members = new List<int>[count];
        var perimeter = new int[count];
        var minX = Enumerable.Repeat(int.MaxValue, count).ToArray();
        var minY = Enumerable.Repeat(int.MaxValue, count).ToArray();
        var maxX = new int[count];
        var maxY = new int[count];
        var voidTouch = new bool[count];
        var locked = new bool[count];
        var borderColors = new Dictionary<int, int>[count];
        byte[] src = image.Pixels;

        void Contact(int r, int to)
        {
            if (r < 0 || r == to) return;
            perimeter[r]++;
            if (to < 0) { voidTouch[r] = true; return; }
            int color = seg.RegionPalette[to];
            var map = borderColors[r] ??= new Dictionary<int, int>();
            map[color] = map.GetValueOrDefault(color) + 1;
        }
        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, r = seg.RegionId[i];
                if (r < 0 || seg.RegionArea[r] > MaxBandArea) continue;
                (members[r] ??= new List<int>()).Add(i);
                locked[r] |= protectedPixels[i] || src[i * 4 + 3] != 255;
                minX[r] = Math.Min(minX[r], x);
                maxX[r] = Math.Max(maxX[r], x);
                minY[r] = Math.Min(minY[r], y);
                maxY[r] = Math.Max(maxY[r], y);
                Contact(r, x > 0 ? seg.RegionId[i - 1] : -1);
                Contact(r, x + 1 < w ? seg.RegionId[i + 1] : -1);
                Contact(r, y > 0 ? seg.RegionId[i - w] : -1);
                Contact(r, y + 1 < h ? seg.RegionId[i + w] : -1);
            }
        }
        // Only the label map is staged. The original source and mask are immutable.
        int[] baseline = (int[])labels.Clone();
        int[] proposed = Enumerable.Repeat(-2, labels.Length).ToArray();
        var metrics = new List<EdgeRegionDiagnostic>(128);
        int considered = 0, aa = 0, ringing = 0, changed = 0, protectedCount = 0;
        int budget = Math.Max(1, labels.Length / 50); // <= 2% total
        for (int r = 0; r < count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var pixels = members[r];
            if (pixels is null || pixels.Count < 2 || perimeter[r] < 1) continue;
            int area = seg.RegionArea[r];
            int dx = maxX[r] - minX[r] + 1, dy = maxY[r] - minY[r] + 1;
            double thickness = 2.0 * area / perimeter[r];
            double aspect = (double)Math.Max(dx, dy) / Math.Max(1, Math.Min(dx, dy));
            if (thickness > MaxThickness ||
                (area > 4 && aspect < 2.5) ||
                (area > 4 && Math.Max(dx, dy) < 4))
                continue;
            considered++;
            int ownLabel = seg.RegionPalette[r];
            var contacts = borderColors[r] ?? new Dictionary<int, int>();
            var sides = contacts.Where(kv => kv.Key != ownLabel)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToArray();
            int ownCore = 0;
            foreach (int i in pixels)
            {
                int p = i * 4;
                if (Oklab.DistSq(Oklab.FromRgb(src[p], src[p+1], src[p+2]),
                                 labs[ownLabel]) < 0.018f * 0.018f) ownCore++;
            }
            double coreFraction = (double)ownCore / area;
            string decision = "no-safe-target";
            if (locked[r] || voidTouch[r] || sides.Length == 0 || coreFraction >= 0.40)
            {
                protectedCount++;
                decision = "protected-detail/alpha/core";
            }
            else if (changed + area > budget)
            {
                decision = "pixel-budget";
            }
            else if (sides.Length >= 2 &&
                     sides[0].Value >= perimeter[r] * .13 &&
                     sides[1].Value >= perimeter[r] * .13 &&
                     sides.Skip(2).Sum(kv => kv.Value) <= perimeter[r] * .12 &&
                     Oklab.DistSq(labs[sides[0].Key], labs[sides[1].Key]) >=
                         MinAnchorDistanceSq)
            {
                // Third palette paint between A and B. Both source RGB and
                // the candidate palette must be compatible with a two-ink ramp.
                int a = sides[0].Key, b = sides[1].Key;
                Lab A = labs[a], B = labs[b], C = labs[ownLabel];
                double ql = B.L - A.L, qa = B.A - A.A, qb = B.B - A.B;
                double sq = ql*ql + qa*qa + qb*qb;
                double tC = ((C.L-A.L)*ql+(C.A-A.A)*qa+(C.B-A.B)*qb)/sq;
                double rL = C.L-A.L-tC*ql, rA = C.A-A.A-tC*qa, rB = C.B-A.B-tC*qb;
                if (tC > .04 && tC < .96 &&
                    rL*rL+rA*rA+rB*rB < .055*.055)
                {
                    var ca = palette[a].Color; var cb = palette[b].Color;
                    double dr = cb.R - ca.R, dg = cb.G - ca.G, db = cb.B - ca.B;
                    double denom = dr*dr + dg*dg + db*db;
                    int evidence = 0, onA = 0, onB = 0;
                    double tMin = 1, tMax = 0;
                    var assigned = new int[area];
                    for (int j = 0; j < area; j++)
                    {
                        int p = pixels[j] * 4;
                        double x = src[p]-ca.R, y = src[p+1]-ca.G, z = src[p+2]-ca.B;
                        double t = Math.Clamp((x*dr+y*dg+z*db)/denom, 0, 1);
                        double er = x-t*dr, eg = y-t*dg, eb = z-t*db;
                        if (t > .03 && t < .97 &&
                            er*er+eg*eg+eb*eb <= MaxRgbResidualSq) evidence++;
                        tMin = Math.Min(tMin,t); tMax = Math.Max(tMax,t);
                        assigned[j] = t < .5 ? a : b;
                        if (assigned[j] == a) onA++; else onB++;
                    }
                    if (evidence >= Math.Ceiling(area*.8) &&
                        tMax-tMin >= .15 && onA > 0 && onB > 0)
                    {
                        for (int j = 0; j < area; j++)
                            proposed[pixels[j]] = assigned[j];
                        aa++; changed += area;
                        decision = "aa-band-relabel";
                    }
                    else decision = "insufficient-blend-evidence";
                }
                else decision = "real-third-paint";
            }
            else if (area <= MaxRingingArea &&
                     sides.Length >= 1 && sides[0].Value >= perimeter[r] * .68 &&
                     sides.Skip(1).Sum(kv => kv.Value) <= perimeter[r] * .15)
            {
                // Detached A-label specks/slivers next to B. Protect any source
                // genuinely supporting A, even when its component is narrow.
                int target = sides[0].Key;
                int better = 0;
                double oldCost = 0, newCost = 0;
                foreach (int i in pixels)
                {
                    int p = i*4;
                    Lab color = Oklab.FromRgb(src[p],src[p+1],src[p+2]);
                    double eo = Oklab.DistSq(color,labs[ownLabel]);
                    double en = Oklab.DistSq(color,labs[target]);
                    oldCost += eo; newCost += en;
                    if (en + .0004 < eo && en < .08*.08) better++;
                }
                if (better >= Math.Ceiling(area*.75) &&
                    newCost < oldCost * .72)
                {
                    foreach (int i in pixels) proposed[i] = target;
                    ringing++; changed += area;
                    decision = "ringing-fragment-relabel";
                }
                else decision = "source-supports-original";
            }
            if (metrics.Count < 256)
                metrics.Add(new EdgeRegionDiagnostic(r,area,perimeter,
                    Math.Round(thickness,3),Math.Round(aspect,3),
                    contacts.Count, Math.Round(coreFraction,3),decision));
        }

        var diagnostics = metrics.ToArray();
        if (changed == 0)
            return new EdgeArtifactStats
            {
                RegionsBefore=count, RegionsAfter=count,
                ThinCandidates=considered, ProtectedCandidates=protectedCount,
                DiagnosticRegionMap=diagnostics
            };
        for (int i=0; i<labels.Length; i++)
            if (proposed[i]>=0) labels[i]=proposed[i];

        bool badTopology = false;
        int after = count;
        try
        {
            var updated = Segmenter.Components(labels,w,h);
            after = updated.RegionCount;
            if (count-after != aa+ringing)
                badTopology = true;
            else
            {
                var graph = new BoundaryTracer(updated.RegionId,w,h,updated.RegionCount).Run(ct);
                BoundaryTopologyValidator.Verify(graph,updated,w,h,ct);
            }
        }
        catch (InvalidOperationException) { badTopology = true; }
        if (badTopology)
        {
            Array.Copy(baseline,labels,labels.Length);
            return new EdgeArtifactStats
            {
                RegionsBefore=count, RegionsAfter=count, ThinCandidates=considered,
                ProtectedCandidates=protectedCount, TopologyRejections=aa+ringing,
                RolledBack=true, DiagnosticRegionMap=diagnostics
            };
        }
        return new EdgeArtifactStats
        {
            RegionsBefore=count,RegionsAfter=after, ThinCandidates=considered,
            AABandsRemoved=aa, RingingFragmentsRemoved=ringing,
            RelabeledPixels=changed,ProtectedCandidates=protectedCount,
            DiagnosticRegionMap=diagnostics
        };
    }
}
