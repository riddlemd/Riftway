namespace Riftway.Cef.Platform;

/// <summary>Electron v44's <c>window:willPositionSheet:usingRect:</c> (electron_ns_window_delegate.mm).</summary>
public static class MacSheet
{
    /// <summary>Where a sheet's rectangle starts: <paramref name="offsets"/> X from the left, and Y down from the top of a
    /// content view <paramref name="contentHeight"/> tall, in AppKit's bottom-up coordinates.</summary>
    public static (double X, double Y) Position((double X, double Y) offsets, double contentHeight) => (offsets.X, contentHeight - offsets.Y);
}
