namespace Vecto.Core;

/// <summary>
/// Metadata for an opt-in attempt to consolidate same-paint shapes while
/// respecting the occlusion dependency graph of StackedShapeBuilder.
/// </summary>
public readonly record struct StackedLayerStats(
    int BaselinePaths,
    int CandidatePaths,
    int OutputPaths,
    int ReorderedRegions,
    int OcclusionDependencies,
    int ComparedPixels,
    int PixelMismatches,
    bool Reverted)
{
    public int PathsSaved => BaselinePaths - OutputPaths;
}

/// <summary>
/// Experimental stacked-shape reasoning: order independent shapes by fill
/// color, NEVER reorder across an established front-of dependency, then
/// pack consecutive equal-fill components into one compound SVG path.
///
/// Does not invent curves, erase genuine holes, or alter any colors. Rendering
/// is compared with the existing Stacked output and rejected on any detected
/// mismatch. The raster probe is a gate, not a guarantee at every resolution.
/// </summary>
internal static class StackedLayerReasoner
{
    private const int MaxOutOfOrderPriority = 16;
    private const int ProbeMaxSide = 512;
    private const int ProbeSupersample = 2;

    public static List<int> Schedule(
        int count,
        HashSet<int>[] frontOf,
        IReadOnlyList<int> priority,
        IReadOnlyList<RegionPath> byRegion,
        CancellationToken ct)
    {
        var indegree = new int[count];
        for (int parent = 0; parent < count; parent++)
            if (frontOf[parent] is { } children)
                foreach (int child in children) indegree[child]++;

        // "ready" means every occlusion parent has already been painted.
        // Rank makes the search deterministic and limits arbitrary reordering.
        var ready = new SortedSet<(int Rank, int Region)>();
        for (int r = 0; r < count; r++)
            if (indegree[r] == 0) ready.Add((priority[r], r));

        var ordered = new List<int>(count);
        int lastColor = -1;
        while (ready.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var earliest = ready.Min;
            var chosen = earliest;
            if (lastColor >= 0)
            {
                // Prefer a same-color independent shape, but never leap over
                // more than 16 original-priority slots. This minimizes
                // unnecessary changes to the original painter order.
                foreach (var item in ready)
                {
                    if (item.Rank > earliest.Rank + MaxOutOfOrderPriority) break;
                    if (byRegion[item.Region].PaletteIndex == lastColor)
                    {
                        chosen = item;
                        break;
                    }
                }
            }
            ready.Remove(chosen);
            ordered.Add(chosen.Region);
            lastColor = byRegion[chosen.Region].PaletteIndex;
            if (frontOf[chosen.Region] is not { } children) continue;
            foreach (int child in children)
                if (--indegree[child] == 0)
                    ready.Add((priority[child], child));
        }
        if (ordered.Count != count)
            throw new InvalidOperationException("Occlusion graph contains a cycle.");
        return ordered;
    }

    public static List<RegionPath> CombineConsecutive(
        IReadOnlyList<int> order, IReadOnlyList<RegionPath> byRegion)
    {
        var result = new List<RegionPath>(order.Count);
        foreach (int id in order)
        {
            RegionPath part = byRegion[id];
            if (result.Count == 0 || result[^1].PaletteIndex != part.PaletteIndex)
            {
                result.Add(part);
                continue;
            }
            RegionPath previous = result[^1];
            var combined = new List<List<CubicBezier>>(
                previous.Loops.Count + part.Loops.Count);
            combined.AddRange(previous.Loops);
            combined.AddRange(part.Loops);
            result[^1] = new RegionPath
            {
                PaletteIndex = part.PaletteIndex,
                Area = checked(previous.Area + part.Area),
                Loops = combined
            };
        }
        return result;
    }

    public static (VectorDocument Document, StackedLayerStats Stats) VerifyOrRevert(
        VectorDocument baseline, VectorDocument candidate,
        int moved, int dependencies, CancellationToken ct)
    {
        int before = baseline.Regions.Count, proposed = candidate.Regions.Count;
        if (proposed >= before)
        {
            return (baseline, new StackedLayerStats(
                before, proposed, before, moved, dependencies, 0, 0, false));
        }

        ct.ThrowIfCancellationRequested();
        double scale = Math.Min(1.0,
            ProbeMaxSide / (double)Math.Max(baseline.Width, baseline.Height));
        var original = Rasterizer.Render(baseline, scale,
            supersample: ProbeSupersample);
        ct.ThrowIfCancellationRequested();
        var optimized = Rasterizer.Render(candidate, scale,
            supersample: ProbeSupersample);
        ct.ThrowIfCancellationRequested();
        if (original.Width != optimized.Width || original.Height != optimized.Height)
            throw new InvalidOperationException("Stacked preview dimensions mismatch.");

        int mismatch = 0;
        for (int i = 0; i < original.Pixels.Length; i += 4)
        {
            if (original.Pixels[i] != optimized.Pixels[i] ||
                original.Pixels[i + 1] != optimized.Pixels[i + 1] ||
                original.Pixels[i + 2] != optimized.Pixels[i + 2] ||
                original.Pixels[i + 3] != optimized.Pixels[i + 3])
                mismatch++;
        }

        bool revert = mismatch > 0;
        int output = revert ? before : proposed;
        return (revert ? baseline : candidate,
            new StackedLayerStats(before, proposed, output,
                moved, dependencies, original.Width * original.Height,
                mismatch, revert));
    }
}
