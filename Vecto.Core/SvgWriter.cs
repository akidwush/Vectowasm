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
                    .Where(seam => seam.Curves.Count > 0)
                    .GroupBy(seam => seam.StrokeWidth);
                foreach (var group in byWidth)
                {
                    sb.Append("<path data-seam-underpaint=\"1\" fill=\"none\" stroke=\"#")
                      .Append(c.R.ToString("x2", CultureInfo.InvariantCulture))
                      .Append(c.G.ToString("x2", CultureInfo.InvariantCulture))
                      .Append(c.B.ToString("x2", CultureInfo.InvariantCulture))
                      .Append("\" stroke-width=\"").Append(F(group.Key))
                      .Append("\" stroke-linecap=\"butt\" stroke-linejoin=\"round\" d=\"");
                    foreach (var seam in group)
                        AppendUnderpaint(sb, seam.Curves);
                    sb.Append("\" />\\n");
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

    private static void AppendUnderpaint(StringBuilder sb, IReadOnlyList<CubicBezier> curves)
    {
        var first = curves[0];
        sb.Append(" M ").Append(F(first.P0.X)).Append(' ').Append(F(first.P0.Y));
        foreach (var seg in curves)
        {
            if (seg.IsLine())
                sb.Append(" L ").Append(F(seg.P3.X)).Append(' ').Append(F(seg.P3.Y));
            else
                sb.Append(" C ").Append(F(seg.P1.X)).Append(' ').Append(F(seg.P1.Y)).Append(' ')
                  .Append(F(seg.P2.X)).Append(' ').Append(F(seg.P2.Y)).Append(' ')
                  .Append(F(seg.P3.X)).Append(' ').Append(F(seg.P3.Y));
        }
        // Intentionally NO Z: closing the auxiliary path would create a new seam.
    }

    static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
