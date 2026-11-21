using System.Globalization;
using System.Text;

namespace Vecto.Core;

/// <summary>
/// SVG 1.1 output in the same shape Vector Magic emits: one path per region, holes as
/// subpaths, 2-decimal coordinates, near-straight cubics collapsed to line commands.
/// Formatting is culture-invariant by construction.
/// </summary>
public static class SvgWriter
{
    public static string Write(VectorDocument doc)
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n");
        sb.Append("<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">\n");
        sb.Append("<svg width=\"").Append(doc.Width).Append("pt\" height=\"").Append(doc.Height)
          .Append("pt\" viewBox=\"0 0 ").Append(doc.Width).Append(' ').Append(doc.Height)
          .Append("\" version=\"1.1\" xmlns=\"http://www.w3.org/2000/svg\">\n");
        // Both modes emit direct, independent SVG paths. Stacked rendering
        // relies on the order of these paths, not an SVG <g> wrapper.
        foreach (var region in doc.Regions)
        {
            var c = doc.Palette[region.PaletteIndex].Color;
            sb.Append("<path fill=\"#")
              .Append(c.R.ToString("x2", CultureInfo.InvariantCulture))
              .Append(c.G.ToString("x2", CultureInfo.InvariantCulture))
              .Append(c.B.ToString("x2", CultureInfo.InvariantCulture))
              .Append("\" opacity=\"1.00\" d=\"");
            foreach (var loop in region.Loops) AppendLoop(sb, loop);
            sb.Append("\" />\n");
            // Optional internal underpaint is emitted AFTER its back region
            // but BEFORE the as-yet-unpainted front region. Main paths do not
            // receive stroke or geometry changes. No global micro-stroke.
            // Batch all safe seam runs with identical width and back colour
            // into ONE compound open-path. Disjoint runs begin with 'M'
            // (never 'Z'), preserving draw order and individual geometry.
            if (region.SeamUnderpaints.Count > 0)
            {
                var byWidth = region.SeamUnderpaints
                    .Where(seam => seam.Curves.Count > 0 && HelperLength(seam.Curves) >= 1.2)
                    .GroupBy(seam => seam.StrokeWidth);
                foreach (var group in byWidth)
                {
                    sb.Append("<path data-seam=\"1\" fill=\"none\" stroke=\"#")
                      .Append(c.R.ToString("x2", CultureInfo.InvariantCulture))
                      .Append(c.G.ToString("x2", CultureInfo.InvariantCulture))
                      .Append(c.B.ToString("x2", CultureInfo.InvariantCulture))
                      .Append("\" stroke-width=\"").Append(Compact(group.Key))
                      .Append("\" stroke-linejoin=\"round\" d=\"");
                    var pen = new Vec2(0, 0);
                    foreach (var seam in group)
                        pen = AppendUnderpaint(sb, seam.Curves, pen);
                    sb.Append("\" />\n");
                }
            }
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    static void AppendLoop(StringBuilder sb, List<CubicBezier> loop)
    {
        if (loop.Count == 0) return;
        sb.Append(" M ").Append(F(loop[0].P0.X)).Append(' ').Append(F(loop[0].P0.Y));
        foreach (var seg in loop)
        {
            if (seg.IsLine())
                sb.Append(" L ").Append(F(seg.P3.X)).Append(' ').Append(F(seg.P3.Y));
            else
                sb.Append(" C ")
                  .Append(F(seg.P1.X)).Append(' ').Append(F(seg.P1.Y)).Append(' ')
                  .Append(F(seg.P2.X)).Append(' ').Append(F(seg.P2.Y)).Append(' ')
                  .Append(F(seg.P3.X)).Append(' ').Append(F(seg.P3.Y));
        }
        sb.Append(" Z");
    }

    // Independent one-decimal quantisation for auxiliary paths ONLY.
    // Visible main paths retain their original two-decimal writer.
    private static double Q(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);
    private static string F1(double v) => Q(v).ToString("0.#", CultureInfo.InvariantCulture);

    private static double Deviation(Vec2 point, Vec2 a, Vec2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-8 ? (point - a).Length :
            Math.Abs(dx * (point.Y - a.Y) - dy * (point.X - a.X)) / len;
    }

    private static double HelperLength(IReadOnlyList<CubicBezier> curves)
    {
        double n = 0;
        foreach (var c in curves) n += (c.P3 - c.P0).Length;
        return n;
    }

    private static bool NearLine(CubicBezier c) =>
        c.IsLine() ||
        (Deviation(c.P1, c.P0, c.P3) <= 0.16 &&
         Deviation(c.P2, c.P0, c.P3) <= 0.16);

    // Coalesce collinear microsegments (<1-1.5px) instead of emitting a
    // redundant node. Do not blindly delete curved detail or bridge a gap.
    private static List<CubicBezier> Simplify(IReadOnlyList<CubicBezier> input)
    {
        var result = new List<CubicBezier>(input.Count);
        foreach (var original in input)
        {
            if ((original.P3 - original.P0).Length < 0.01) continue;
            bool straight = NearLine(original);
            var c = straight ? CubicBezier.Line(original.P0, original.P3) : original;
            if (straight && result.Count > 0 && result[^1].IsLine())
            {
                var prev = result[^1];
                if ((prev.P3 - c.P0).Length < 1e-5 &&
                    (prev.P3 - prev.P0).Dot(c.P3 - c.P0) > 0 &&
                    Deviation(prev.P3, prev.P0, c.P3) <= 0.16)
                {
                    result[^1] = CubicBezier.Line(prev.P0, c.P3);
                    continue;
                }
            }
            result.Add(c);
        }
        return result;
    }

    // Each relative m after the first is measured from the previous run's
    // rounded endpoint. Relative c control points use the segment start.
    private static Vec2 AppendUnderpaint(
        StringBuilder sb, IReadOnlyList<CubicBezier> source, Vec2 pen)
    {
        var curves = Simplify(source);
        if (curves.Count == 0) return pen;
        double sx = Q(curves[0].P0.X), sy = Q(curves[0].P0.Y);
        sb.Append('m').Append(F1(sx - pen.X)).Append(' ').Append(F1(sy - pen.Y));
        pen = new Vec2(sx, sy);
        foreach (var seg in curves)
        {
            double x = Q(seg.P3.X), y = Q(seg.P3.Y);
            if (seg.IsLine())
                sb.Append('l').Append(F1(x - pen.X)).Append(' ').Append(F1(y - pen.Y));
            else
                sb.Append('c')
                  .Append(F1(seg.P1.X - pen.X)).Append(' ')
                  .Append(F1(seg.P1.Y - pen.Y)).Append(' ')
                  .Append(F1(seg.P2.X - pen.X)).Append(' ')
                  .Append(F1(seg.P2.Y - pen.Y)).Append(' ')
                  .Append(F1(x - pen.X)).Append(' ').Append(F1(y - pen.Y));
            pen = new Vec2(x, y);
        }
        return pen;
    }

    static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
