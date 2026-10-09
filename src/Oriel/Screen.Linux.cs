using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

// Linux: display changes follow the X events XDisplayManager updates on; polling is left for no XRandR 1.3.
public static partial class Screen
{
    // UI thread only.
    private static bool s_xrandrTried;
    private static bool s_xrandr;

    /// <summary>Whether display changes come from XRandR events rather than the poll; for tests.</summary>
    internal static bool UsesXRandR => s_xrandr;

    private static bool ObservesXRandR()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (s_xrandrTried) return s_xrandr;
        s_xrandrTried = true;
        s_xrandr = X11Displays.Start(() => CefThread.PostUi(OnXRandREvent));
        return s_xrandr;
    }

    // Chromium hears the same event on its own connection and updates CEF's displays from a posted task, which may run
    // after this one; the second look catches that.
    private static void OnXRandREvent()
    {
        Check(headroomDue: true);
        CefThread.PostUiDelayed(() => Check(headroomDue: true), s_settleDelay);
    }
}
