namespace Riftway.Cef.Platform;

/// <summary>Electron v44's <c>swiz_nsview_swipeWithEvent:</c> (electron_ns_window.mm).</summary>
public static class MacSwipe
{
    /// <summary>The swipe's direction from the event's deltas, in Electron's order; null for anything else.</summary>
    public static string? Direction(double deltaX, double deltaY)
        => deltaY == 1.0 ? "up" : deltaX == -1.0 ? "right" : deltaY == -1.0 ? "down" : deltaX == 1.0 ? "left" : null;
}
