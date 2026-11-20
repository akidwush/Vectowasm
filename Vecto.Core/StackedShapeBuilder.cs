namespace Vecto.Core;

/// <summary>
/// Reconstructs opaque nested silhouettes from Vecto's already-fitted planar
/// paths. The shape behind loses its cutout hole only when foreground region
/// components fully cover it; genuine transparent holes are retained.
/// </summary>
internal static class StackedShapeBuilder
{
    public static VectorDocument Build(
        VectorDocument planar, Segmentation seg, BoundaryGraph graph,
        IReadOnlyList<int> regionOrder, CancellationToken ct,
        bool stackedLayerReasoningV1 = false,
        Action<StackedLayerStats>? onLayerAudit = null)
    {
        int count = seg.RegionCount;
        VectorDocument Wrap(IEnumerable<RegionPath> output) => new()
        {
            Width = planar.Width, Height = planar.Height, ShapeMode = ShapeMode.Stacked,
            Palette = planar.Palette, Regions = output.ToList()
        };
        if (count < 1 || planar.Regions.Count != count || regionOrder.Count != count)
            return Wrap(planar.Regions);

        var byRegion = new RegionPath[count];
        for (int i = 0; i < count; i++) byRegion[regionOrder[i]] = planar.Regions[i];
        for (int i = 0; i < count; i++)
            if (byRegion[i].Loops.Count != graph.RegionLoops[i].Count)
                return Wrap(planar.Regions);

        // Region adjacency is built from the original shared-edge graph,
        // not fitted geometry, so topology stays exact.
        int voidNode = count;
        var adjacency = new HashSet<int>[count + 1];
        foreach (var chain in graph.Chains)
        {
            int a = chain.Left < 0 ? voidNode : chain.Left;
            int b = chain.Right < 0 ? voidNode : chain.Right;
            if (a == b) continue;
            (adjacency[a] ??= new HashSet<int>()).Add(b);
            (adjacency[b] ??= new HashSet<int>()).Add(a);
        }

        var holesToFill = new HashSet<int>[count];
        var frontOf = new HashSet<int>[count];
        var seen = new int[count + 1];
        var queue = new Queue<int>();
        var contained = new List<int>();
        int token = 0;

        for (int parent = 0; parent < count; parent++)
        {
            ct.ThrowIfCancellationRequested();
            var loops = byRegion[parent].Loops;
            if (loops.Count < 2) continue;

            // The largest closed contour is outer; opposite winding is hole.
            double largest = 0;
            foreach (var loop in loops)
            {
                double area = SignedArea(loop);
                if (Math.Abs(area) > Math.Abs(largest)) largest = area;
            }
            if (Math.Abs(largest) <= 0.1) continue;

            for (int h = 0; h < loops.Count; h++)
            {
                if (SignedArea(loops[h]) * largest >= -0.01) continue;
                int neighbour = -1;
                foreach (var use in graph.RegionLoops[parent][h])
                {
                    var edge = graph.Chains[use.Chain];
                    if (edge.Left == parent && edge.Right != parent)
                    {
                        neighbour = edge.Right;
                        break;
                    }
                    if (edge.Right == parent && edge.Left != parent)
                    {
                        neighbour = edge.Left;
                        break;
                    }
                }
                if (neighbour < 0) continue;

                // Flood the adjacency graph while excluding the parent region.
                // Everything reachable belongs inside this hole. A void pixel
                // means transparent artwork, so leave the hole intact.
                token++;
                queue.Clear();
                contained.Clear();
                queue.Enqueue(neighbour);
                seen[neighbour] = token;
                bool hasVoid = false;
                while (queue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int r = queue.Dequeue();
                    if (r == voidNode) { hasVoid = true; break; }
                    contained.Add(r);
                    if (adjacency[r] is not { } neighbours) continue;
                    foreach (int next in neighbours)
                    {
                        if (next == parent || seen[next] == token) continue;
                        seen[next] = token;
                        queue.Enqueue(next);
                    }
                }
                if (hasVoid || contained.Count == 0) continue;

                (holesToFill[parent] ??= new HashSet<int>()).Add(h);
                var front = frontOf[parent] ??= new HashSet<int>();
                foreach (int r in contained)
                    if (r != parent) front.Add(r);
            }
        }

        // A topological painter order is essential: a thin outline region may
        // be smaller than the filled region inside, unlike area-based sorting.
        var indegree = new int[count];
        foreach (var dependencies in frontOf)
            if (dependencies is not null)
                foreach (int child in dependencies) indegree[child]++;

        var originalPriority = new int[count];
        for (int i = 0; i < count; i++)
            originalPriority[regionOrder[i]] = i;
        var ready = new PriorityQueue<int, int>();
        for (int r = 0; r < count; r++)
            if (indegree[r] == 0) ready.Enqueue(r, originalPriority[r]);

        var ordered = new List<int>(count);
        while (ready.TryDequeue(out int r, out _))
        {
            ordered.Add(r);
            if (frontOf[r] is not { } children) continue;
            foreach (int child in children)
                if (--indegree[child] == 0) ready.Enqueue(child, originalPriority[child]);
        }

        // Unusual intersecting contours may create a cycle. Prefer unchanged
        // cutout geometry to an incorrect stacked drawing.
        if (ordered.Count != count) return Wrap(planar.Regions);

        var result = new List<RegionPath>(count);
        foreach (int r in ordered)
        {
            var source = byRegion[r];
            if (holesToFill[r] is not { Count: > 0 } holes)
            {
                result.Add(source);
                continue;
            }
            var loops = new List<List<CubicBezier>>(source.Loops.Count - holes.Count);
            for (int h = 0; h < source.Loops.Count; h++)
                if (!holes.Contains(h)) loops.Add(source.Loops[h]);
            result.Add(new RegionPath
            {
                PaletteIndex = source.PaletteIndex,
                Area = source.Area,
                Loops = loops
            });
        }
        var baseline = Wrap(result);
        if (!stackedLayerReasoningV1)
            return baseline;

        // Region ids and original painter order are retained so that the
        // experiment cannot accidentally identify shapes only by palette.
        // The established front-of DAG is the authority for overlap order.
        var renderedByRegion = new RegionPath[count];
        for (int i = 0; i < ordered.Count; i++)
            renderedByRegion[ordered[i]] = result[i];

        var candidateOrder = StackedLayerReasoner.Schedule(
            count, frontOf, originalPriority, renderedByRegion, ct);
        var candidateRegions = StackedLayerReasoner.CombineConsecutive(
            candidateOrder, renderedByRegion);
        var candidateDoc = Wrap(candidateRegions);
        int moved = 0;
        for (int i = 0; i < count; i++)
            if (ordered[i] != candidateOrder[i]) moved++;
        int dependencyCount = frontOf.Sum(x => x?.Count ?? 0);
        // Fail closed if compound winding or reordering changes any tested
        // raster pixel. The original Stacked document is kept as fallback.
        var (verified, audit) = StackedLayerReasoner.VerifyOrRevert(
            baseline, candidateDoc, moved, dependencyCount, ct);
        onLayerAudit?.Invoke(audit);
        return verified;
    }

    private static double SignedArea(IReadOnlyList<CubicBezier> loop)
    {
        if (loop.Count == 0) return 0;
        double area2 = 0;
        Vec2 last = loop[0].P0;
        foreach (var curve in loop)
        {
            for (int i = 1; i <= 4; i++)
            {
                Vec2 point = curve.Eval(i / 4.0);
                area2 += last.X * point.Y - point.X * last.Y;
                last = point;
            }
        }
        return area2 / 2;
    }
}
