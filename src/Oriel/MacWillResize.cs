using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Electron's <c>ElectronNSWindowDelegate windowWillResize:toSize:</c> arithmetic.</summary>
internal static class MacWillResize
{
    // NSWindow's maxSize when unlimited.
    private const double Unlimited = float.MaxValue;

    /// <summary>The size an aspect ratio allows for <paramref name="width"/> × <paramref name="height"/>: the content keeps
    /// the ratio (plus <paramref name="extra"/>), rounded, then clamped to the window's limits while keeping it.</summary>
    internal static (double Width, double Height) Constrain(double width, double height, double aspectRatio, MacResizeGeometry g,
        (int Width, int Height) extra, bool hasFrame)
    {
        // gfx::Size truncates the window and content sizes.
        var titleBarHeight = (int)g.Height - (int)g.ContentHeight;
        double extraWidthPlusFrame = (int)g.Width - (int)g.ContentWidth + extra.Width;
        double extraHeightPlusFrame = titleBarHeight + extra.Height;
        double WidthForHeight(double h) => (h - extraHeightPlusFrame) * aspectRatio + extraWidthPlusFrame;
        double HeightForWidth(double w) => (w - extraWidthPlusFrame) / aspectRatio + extraHeightPlusFrame;

        // roundf: halves away from zero.
        var newWidth = (double)MathF.Round((float)WidthForHeight(height), MidpointRounding.AwayFromZero);
        var newHeight = (double)MathF.Round((float)HeightForWidth(newWidth), MidpointRounding.AwayFromZero);

        var (zeroWidth, zeroHeight) = hasFrame ? (0.0, (double)titleBarHeight) : (0.0, 0.0);
        if (g.MinWidth != zeroWidth || g.MinHeight != zeroHeight)
        {
            var atMinHeight = g.MinHeight > zeroHeight && newHeight <= g.MinHeight;
            newWidth = atMinHeight ? WidthForHeight(g.MinHeight) : Math.Max(newWidth, g.MinWidth);
            var atMinWidth = g.MinWidth > zeroWidth && newWidth <= g.MinWidth;
            newHeight = atMinWidth ? HeightForWidth(g.MinWidth) : Math.Max(newHeight, g.MinHeight);
        }
        if (g.MaxWidth != Unlimited || g.MaxHeight != Unlimited)
        {
            var atMaxHeight = g.MaxHeight < Unlimited && newHeight >= g.MaxHeight;
            newWidth = atMaxHeight ? WidthForHeight(g.MaxHeight) : Math.Min(newWidth, g.MaxWidth);
            var atMaxWidth = g.MaxWidth < Unlimited && newWidth >= g.MaxWidth;
            newHeight = atMaxWidth ? HeightForWidth(g.MaxWidth) : Math.Min(newHeight, g.MaxHeight);
        }
        return (newWidth, newHeight);
    }

    /// <summary>Whether the drag is horizontal: the width moved more than the height.</summary>
    internal static bool Horizontal(double width, double height, MacResizeGeometry g) => Math.Abs(width - g.Width) > Math.Abs(height - g.Height);
}
