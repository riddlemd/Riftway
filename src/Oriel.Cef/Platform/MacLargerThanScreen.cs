namespace Oriel.Cef.Platform;

/// <summary>Electron v44's <c>ElectronNSWindow constrainFrameRect:toScreen:</c> with <c>enableLargerThanScreen</c>.</summary>
public static class MacLargerThanScreen
{
    /// <summary>AppKit's <paramref name="constrained"/> frame, keeping the asked size: a framed window stays where AppKit can
    /// move it (not behind the menu bar), a frameless one goes exactly where it was asked.</summary>
    public static (double X, double Y, double Width, double Height) Constrain(
        (double X, double Y, double Width, double Height) asked, (double X, double Y, double Width, double Height) constrained, bool hasFrame)
        => hasFrame ? constrained with { Width = asked.Width, Height = asked.Height } : asked;
}
