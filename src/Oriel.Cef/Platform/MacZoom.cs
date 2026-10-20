namespace Oriel.Cef.Platform;

/// <summary>Electron v44's <c>windowWillUseStandardFrame:defaultFrame:</c> with <c>zoomToPageWidth</c>
/// (electron_ns_window_delegate.mm).</summary>
public static class MacZoom
{
    /// <summary>The zoomed frame: AppKit's <paramref name="standard"/> one for its y and height, the window never narrower
    /// than now or than <paramref name="preferredWidth"/>, and moved left only as far as it must to stay in
    /// <paramref name="standard"/>.</summary>
    public static (double X, double Y, double Width, double Height) StandardFrame(
        (double X, double Y, double Width, double Height) standard, (double X, double Width) window, double preferredWidth)
    {
        var width = Math.Max(preferredWidth, window.Width);
        var x = window.X + width > standard.X + standard.Width ? standard.X + standard.Width - width : window.X;
        return standard with { X = x, Width = width };
    }
}
