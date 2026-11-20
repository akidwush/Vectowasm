namespace Vecto.Core;

/// <summary>
/// Diagnostic proof that every pixel crack is represented by exactly one
/// shared edge and that each adjoining region traverses that edge once in the
/// right direction. Runs only with RegionMergeV1 enabled, before curve fitting.
/// It does not change SVG geometry or the production tracing path.
/// </summary>
internal static class BoundaryTopologyValidator
{
    internal readonly record struct Audit(int Cracks, int RegionUses, int ClosedContours);

    public static Audit Verify(
        BoundaryGraph graph, Segmentation segmentation,
        int width, int height, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(segmentation);
        var regionId = segmentation.RegionId;
        if (width <= 0 || height <= 0 || regionId.Length != width * height ||
            graph.RegionLoops.Length != segmentation.RegionCount)
            throw new InvalidOperationException("Invalid graph/segmentation dimensions.");

        int Reg(int x, int y) =>
            (uint)x < (uint)width && (uint)y < (uint)height
                ? regionId[y * width + x] : -1;

        var leftUses = new int[graph.Chains.Count];
        var rightUses = new int[graph.Chains.Count];
        int loops = 0, uses = 0;
        for (int r = 0; r < graph.RegionLoops.Length; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var loop in graph.RegionLoops[r])
            {
                if (loop.Count == 0)
                    throw new InvalidOperationException("Empty region contour in shared-edge graph.");
                Vec2? loopStart = null, prevEnd = null;
                foreach (var use in loop)
                {
                    if ((uint)use.Chain >= (uint)graph.Chains.Count)
                        throw new InvalidOperationException("Invalid shared-edge index.");
                    var chain = graph.Chains[use.Chain];
                    int side = use.Forward ? chain.Left : chain.Right;
                    if (side != r)
                        throw new InvalidOperationException("Shared-edge used by wrong adjoining region.");
                    Vec2 from = use.Forward ? chain.Points[0] : chain.Points[^1];
                    Vec2 to = use.Forward ? chain.Points[^1] : chain.Points[0];
                    if (prevEnd.HasValue && prevEnd.Value != from)
                        throw new InvalidOperationException("Contour gap between shared-edge chains.");
                    loopStart ??= from;
                    prevEnd = to;
                    if (use.Forward) leftUses[use.Chain]++;
                    else rightUses[use.Chain]++;
                    uses++;
                }
                if (prevEnd != loopStart)
                    throw new InvalidOperationException("Open contour in closed region loop.");
                loops++;
            }
        }

        int cracks = 0;
        for (int i = 0; i < graph.Chains.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chain = graph.Chains[i];
            if (chain.Points.Count < 2 || chain.Left == chain.Right ||
                chain.Left < -1 || chain.Right < -1 ||
                chain.Left >= segmentation.RegionCount ||
                chain.Right >= segmentation.RegionCount)
                throw new InvalidOperationException("Invalid shared-edge chain.");
            if (chain.Closed && chain.Points[0] != chain.Points[^1])
                throw new InvalidOperationException("Closed shared-edge chain is not closed.");
            if (leftUses[i] != (chain.Left >= 0 ? 1 : 0) ||
                rightUses[i] != (chain.Right >= 0 ? 1 : 0))
                throw new InvalidOperationException("Missing or duplicate shared-edge traversal.");

            for (int j = 1; j < chain.Points.Count; j++)
            {
                Vec2 from = chain.Points[j - 1], to = chain.Points[j];
                int x = (int)from.X, y = (int)from.Y;
                if (x != from.X || y != from.Y ||
                    to.X != (int)to.X || to.Y != (int)to.Y)
                    throw new InvalidOperationException("Non-lattice crack geometry.");
                int dx = (int)(to.X - from.X), dy = (int)(to.Y - from.Y);
                int left, right;
                if (dx == 1 && dy == 0)
                    (left, right) = (Reg(x, y - 1), Reg(x, y));
                else if (dx == 0 && dy == 1)
                    (left, right) = (Reg(x, y), Reg(x - 1, y));
                else if (dx == -1 && dy == 0)
                    (left, right) = (Reg(x - 1, y), Reg(x - 1, y - 1));
                else if (dx == 0 && dy == -1)
                    (left, right) = (Reg(x - 1, y - 1), Reg(x, y - 1));
                else
                    throw new InvalidOperationException("Crack skips lattice vertex.");

                if (left != chain.Left || right != chain.Right)
                    throw new InvalidOperationException("Shared edge has mismatched region sides.");
                cracks++;
            }
        }
        return new Audit(cracks, uses, loops);
    }
}
