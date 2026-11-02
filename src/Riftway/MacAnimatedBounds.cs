using System.Drawing;

namespace Riftway;

// Electron v44's NativeWindowMac::SetBounds, which animates through setFrame:display:animate:.
internal static class MacAnimatedBounds
{
    /// <summary>The frame Electron asks AppKit for; null in full screen, where Electron does nothing. <c>setFrame:</c>
    /// ignores the size limits, so the size is raised to <paramref name="min"/> and, unless either side of
    /// <paramref name="max"/> is 0, lowered to it.</summary>
    internal static Rectangle? Frame(Rectangle bounds, Size min, Size max, bool fullScreen)
    {
        if (fullScreen) return null;
        var width = Math.Max(bounds.Width, min.Width);
        var height = Math.Max(bounds.Height, min.Height);
        if (max.Width != 0 && max.Height != 0)
        {
            width = Math.Min(width, max.Width);
            height = Math.Min(height, max.Height);
        }
        return bounds with { Width = width, Height = height };
    }
}
