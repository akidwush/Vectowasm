namespace Vecto.Core;

/// <summary>
/// Digital-topology corner guard for experimental connected-component merging.
/// A 2x2 cell with >=3 distinct regions, or a diagonal checkerboard, contains
/// a shared graph junction/pinch. Edits touching it can change the contour
/// successor order or turn a diagonal touch into an unintended bridge.
/// This class does not rewrite the raster: it only marks components to protect.
/// </summary>
internal static class RegionTopologyGuardV1
{
    public static bool[] MarkJunctionComponents(
        int[] regionId, int width, int height,
        bool[] eligible, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regionId);
        ArgumentNullException.ThrowIfNull(eligible);
        if (width <= 0 || height <= 0 || regionId.Length != width * height)
            throw new ArgumentException("Invalid region raster dimensions.");
        var marked = new bool[eligible.Length];
        for (int y = 0; y + 1 < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = y * width;
            for (int x = 0; x + 1 < width; x++)
            {
                int i = start + x;
                int a = regionId[i], b = regionId[i + 1];
                int c = regionId[i + width], d = regionId[i + width + 1];
                int distinct = 1;
                if (b != a) distinct++;
                if (c != a && c != b) distinct++;
                if (d != a && d != b && d != c) distinct++;
                if (distinct < 3 && !(a == d && b == c && a != b))
                    continue;
                if (a >= 0 && eligible[a]) marked[a] = true;
                if (b >= 0 && eligible[b]) marked[b] = true;
                if (c >= 0 && eligible[c]) marked[c] = true;
                if (d >= 0 && eligible[d]) marked[d] = true;
            }
        }
        return marked;
    }
}
