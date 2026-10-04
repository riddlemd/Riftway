namespace Oriel;

/// <summary>The caption button symbols.</summary>
internal enum CaptionGlyph
{
    Minimize,
    Maximize,
    Restore,
    Close,
}

/// <summary>Draws the caption button symbols as Windows 11 does: 10-DIP outlines one DIP thick, anti-aliased.</summary>
/// <remarks>Chromium's text stack does not shape the Segoe icon fonts' private-use code points here, so the symbols are
/// drawn rather than typed.</remarks>
internal static class CaptionGlyphs
{
    /// <summary>The symbols' size in DIPs.</summary>
    internal const int Size = 10;

    /// <summary>The scale factors drawn, so the button picks a sharp one at any DPI.</summary>
    internal static readonly float[] Scales = [1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f];

    // Segments in DIPs on the 10x10 box; outlines sit on half-DIP lines so they cover whole pixels at 1x.
    private static readonly (double X0, double Y0, double X1, double Y1)[] s_minimize = [(0, 5, 10, 5)];
    private static readonly (double, double, double, double)[] s_maximize = [(0.5, 0.5, 9.5, 0.5), (9.5, 0.5, 9.5, 9.5), (9.5, 9.5, 0.5, 9.5), (0.5, 9.5, 0.5, 0.5)];
    private static readonly (double, double, double, double)[] s_restore =
    [
        (0.5, 2.5, 7.5, 2.5), (7.5, 2.5, 7.5, 9.5), (7.5, 9.5, 0.5, 9.5), (0.5, 9.5, 0.5, 2.5),
        (2.5, 2.5, 2.5, 0.5), (2.5, 0.5, 9.5, 0.5), (9.5, 0.5, 9.5, 7.5), (9.5, 7.5, 7.5, 7.5),
    ];
    private static readonly (double, double, double, double)[] s_close = [(0, 0, 10, 10), (10, 0, 0, 10)];

    /// <summary>The symbol at <paramref name="scale"/> in <paramref name="argb"/> on transparent, premultiplied BGRA.</summary>
    internal static ImageBitmap Render(CaptionGlyph glyph, float scale, uint argb)
    {
        var segments = glyph switch
        {
            CaptionGlyph.Minimize => s_minimize,
            CaptionGlyph.Maximize => s_maximize,
            CaptionGlyph.Restore => s_restore,
            _ => s_close,
        };
        var side = (int)Math.Round(Size * scale);
        var bgra = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            // Pixel centres, in DIPs.
            var px = (x + 0.5) / scale;
            var py = (y + 0.5) / scale;
            var distance = segments.Min(s => DistanceToSegment(px, py, s.Item1, s.Item2, s.Item3, s.Item4)) * scale;
            // A one-DIP stroke: full inside half its width, fading over one pixel at the edge.
            var coverage = Math.Clamp(0.5 * scale + 0.5 - distance, 0, 1);
            if (coverage <= 0) continue;
            var i = (y * side + x) * 4;
            var alpha = coverage * ((argb >> 24) & 0xFF) / 255.0;
            bgra[i] = (byte)Math.Round((argb & 0xFF) * alpha);
            bgra[i + 1] = (byte)Math.Round(((argb >> 8) & 0xFF) * alpha);
            bgra[i + 2] = (byte)Math.Round(((argb >> 16) & 0xFF) * alpha);
            bgra[i + 3] = (byte)Math.Round(255 * alpha);
        }
        return new ImageBitmap(side, side, bgra);
    }

    internal static double DistanceToSegment(double px, double py, double x0, double y0, double x1, double y1)
    {
        var (dx, dy) = (x1 - x0, y1 - y0);
        var length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(((px - x0) * dx + (py - y0) * dy) / length, 0, 1);
        var (cx, cy) = (x0 + t * dx - px, y0 + t * dy - py);
        return Math.Sqrt(cx * cx + cy * cy);
    }
}
