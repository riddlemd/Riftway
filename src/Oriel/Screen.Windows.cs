using System.Globalization;
using Oriel.Cef;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

// Windows: display changes come from window messages, as Chromium's ScreenWin hears them, and the Display fields CEF
// does not expose are read from Windows the way ScreenWin reads them.
public static unsafe partial class Screen
{
    // Oriel's window hears a broadcast beside Chromium's SingletonHwnd and may hear it first, while CEF still has the
    // old displays; a second look once the other windows have had the message catches that.
    private static readonly TimeSpan s_settleDelay = TimeSpan.FromMilliseconds(100);
    // UI thread only; dropped whenever a message says displays may have changed.
    private static IReadOnlyList<WindowsMonitor>? s_monitors;
    private static bool s_observing;

    /// <summary>Whether a message makes Chromium re-read the displays: <c>WM_DISPLAYCHANGE</c>, <c>WM_ACTIVATEAPP</c>
    /// activating and <c>WM_SETTINGCHANGE</c> for <c>SPI_SETWORKAREA</c> (<c>ScreenWin::OnWndProc</c>), and
    /// <c>WM_DPICHANGED</c>, on which Chromium's windows call <c>ScreenWin::UpdateDisplayInfos</c>.</summary>
    internal static bool ReadsDisplays(uint message, nint wParam) => message switch
    {
        0x007E => true,
        0x001C => wParam == 1,
        0x001A => wParam == 0x002F,
        0x02E0 => true,
        _ => false,
    };

    // StartWatching's hook: true when window messages (Windows) or XRandR events (Linux) replace the poll.
    private static bool ObservesNatively()
    {
        if (OperatingSystem.IsLinux()) return ObservesXRandR();
        if (!OperatingSystem.IsWindows()) return false;
        EnsureObserver();
        return true;
    }

    private static void EnsureObserver()
    {
        if (s_observing || !OperatingSystem.IsWindows()) return;
        WindowsDisplay.StartObserver(OnObserverMessage);
        s_observing = true;
    }

    private static void OnObserverMessage(uint message, nint wParam)
    {
        if (!ReadsDisplays(message, wParam)) return;
        s_monitors = null;
        if (!s_watching) return;
        if (!HasListeners)
        {
            s_watching = false;
            return;
        }
        Refresh();
        CefThread.PostUiDelayed(Refresh, s_settleDelay);
    }

    // Diffs inside the window procedure as ScreenWin does; Electron emits each event from its own posted task.
    private static void Refresh()
    {
        if (!s_watching) return;
        s_monitors = null;
        var now = AllDisplays();
        var changes = DisplayChanges.Between(s_lastDisplays ?? [], now);
        s_lastDisplays = now;
        foreach (var change in changes) CefThread.PostUi(() => Raise(change));
    }


    // Take's hook: the fields CEF does not expose, from the monitor under the display's centre.
    private static Display WithNativeDetails(Display display, cef_display_t* native)
    {
        if (!OperatingSystem.IsWindows()) return display;
        EnsureObserver();
        var monitors = s_monitors ??= WindowsDisplay.Monitors();
        var centre = new cef_point_t { x = display.Bounds.X + display.Bounds.Width / 2, y = display.Bounds.Y + display.Bounds.Height / 2 };
        native->convert_point_to_pixels(native, &centre);
        var (x, y) = (centre.x, centre.y);
        var monitor = monitors.FirstOrDefault(m => x >= m.Left && x < m.Right && y >= m.Top && y < m.Bottom);
        if (monitor is null) return display;
        // ScreenWin marks only the first internal monitor as internal.
        var firstInternal = monitors.FirstOrDefault(m => DisplayMapping.IsInternalOutputTechnology(m.OutputTechnology));
        var (depth, perComponent) = DisplayMapping.Depths(monitor.HdrEnabled);
        return display with
        {
            Label = monitor.Label,
            Internal = ReferenceEquals(monitor, firstInternal),
            DisplayFrequency = monitor.Frequency,
            ColorDepth = depth,
            DepthPerComponent = perComponent,
            ColorSpace = monitor.HdrEnabled ? DisplayMapping.HdrRaster : DisplayMapping.SdrColorSpace(monitor.IccProfile),
            NativeOrigin = DisplayMapping.NativeOrigin(monitor.Left, monitor.Top, native->get_device_scale_factor(native)),
            ColorSpacesKey = monitor.HdrEnabled ? string.Create(CultureInfo.InvariantCulture, $"{monitor.SdrWhiteLevel} {monitor.MaxLuminance} {string.Join(',', monitor.Primaries ?? [])}") : "",
        };
    }
}
