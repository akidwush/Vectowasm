using System.Globalization;
using System.Text;

namespace Vecto.Core;

/// <summary>
/// Geometry and file facts derived from the actual SVG document, not a UI estimate.
/// This does not modify SVG bytes or toggle any tracing options.
/// </summary>
public sealed class SvgOutputStatistics
{
    public int Width { get; init; }
    public int Height { get; init; }
    public long FileBytes { get; init; }
    public int PathCount { get; init; }
    public int SubpathCount { get; init; }
    public int LineCommands { get; init; }
    public int CubicCommands { get; init; }
    public int MicroLineCommands { get; init; }
    /// <summary>Number of emitted endpoints, not a deduplicated shared-vertex count.</summary>
    public int EmittedAnchors { get; init; }
    public int CubicControlPoints { get; init; }
    public double ApproximateOutlineLength { get; init; }
    public int SmallestRegionPixels { get; init; }
    public int LargestRegionPixels { get; init; }
    public int MinSegmentsPerPath { get; init; }
    public int MaxSegmentsPerPath { get; init; }
    public double AverageSegmentsPerPath { get; init; }
    public ShapeMode ShapeMode { get; init; }
    public IReadOnlyList<SvgOutputColor> Colors { get; init; } = Array.Empty<SvgOutputColor>();

    public int TotalSegments => LineCommands + CubicCommands;
    public string ViewBox => $"0 0 {Width} {Height}";
    public string PointsUnitSize => $"{Width} × {Height} pt";

    public static SvgOutputStatistics Analyze(VectorDocument doc, string svg)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(svg);

        int subpaths = 0, lines = 0, curves = 0, micro = 0, minSegs = int.MaxValue, maxSegs = 0;
        double length = 0;
        int minArea = int.MaxValue, maxArea = 0;
        var pathCounts = new Dictionary<int, int>();
        foreach (var region in doc.Regions)
        {
            pathCounts[region.PaletteIndex] = pathCounts.GetValueOrDefault(region.PaletteIndex) + 1;
            minArea = Math.Min(minArea, region.Area);
            maxArea = Math.Max(maxArea, region.Area);
            int regionSegments = 0;
            foreach (var loop in region.Loops)
            {
                if (loop.Count == 0) continue;
                subpaths++;
                regionSegments += loop.Count;
                foreach (var segment in loop)
                {
                    if (segment.IsLine())
                    {
                        lines++;
                        double l = (segment.P3 - segment.P0).Length;
                        length += l;
                        if (l < 2) micro++;
                    }
                    else
                    {
                        curves++;
                        // 16 subdivisions: approximate arc length, clearly labeled in UI.
                        Vec2 prev = segment.P0;
                        for (int i = 1; i <= 16; i++)
                        {
                            Vec2 next = segment.Eval(i / 16.0);
                            length += (next - prev).Length;
                            prev = next;
                        }
                    }
                }
            }
            minSegs = Math.Min(minSegs, regionSegments);
            maxSegs = Math.Max(maxSegs, regionSegments);
        }

        var colors = pathCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key)
            .Select(pair =>
            {
                var entry = doc.Palette[pair.Key];
                return new SvgOutputColor
                {
                    Hex = $"#{entry.Color.R:x2}{entry.Color.G:x2}{entry.Color.B:x2}",
                    RegionPaths = pair.Value,
                    PalettePixelCount = entry.PixelCount
                };
            }).ToArray();

        return new SvgOutputStatistics
        {
            Width = doc.Width,
            Height = doc.Height,
            FileBytes = Encoding.UTF8.GetByteCount(svg),
            PathCount = doc.Regions.Count,
            SubpathCount = subpaths,
            LineCommands = lines,
            CubicCommands = curves,
            MicroLineCommands = micro,
            EmittedAnchors = lines + curves,
            CubicControlPoints = 2 * curves,
            ApproximateOutlineLength = length,
            SmallestRegionPixels = minArea == int.MaxValue ? 0 : minArea,
            LargestRegionPixels = maxArea,
            MinSegmentsPerPath = minSegs == int.MaxValue ? 0 : minSegs,
            MaxSegmentsPerPath = maxSegs,
            AverageSegmentsPerPath = doc.Regions.Count > 0
                ? (double)(lines + curves) / doc.Regions.Count : 0,
            ShapeMode = doc.ShapeMode,
            Colors = colors
        };
    }
}

public sealed class SvgOutputColor
{
    public required string Hex { get; init; }
    public int RegionPaths { get; init; }
    /// <summary>Palette's labeled input pixels, not the rendered SVG pixel area.</summary>
    public int PalettePixelCount { get; init; }
}
