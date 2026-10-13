using Oriel.Cef;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A point in screen coordinates; Electron's <c>Point</c>.</summary>
public readonly record struct Point(int X, int Y);

/// <summary>A display attached to the computer; Electron's <c>Display</c>.</summary>
public sealed partial record Display
{
    /// <summary>Identifies the display while it stays attached.</summary>
    public long Id { get; init; }

    /// <summary>The display's area in screen DIPs.</summary>
    public Rectangle Bounds { get; init; }

    /// <summary>The part of <see cref="Bounds"/> windows may use: without the menu bar, Dock or taskbar.
    /// Electron's <c>workArea</c>.</summary>
    public Rectangle WorkArea { get; init; }

    /// <summary>Physical pixels per DIP, e.g. 2 on a Retina display. Electron's <c>scaleFactor</c>.</summary>
    public double ScaleFactor { get; init; }

    /// <summary>Clockwise rotation in degrees: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    /// <summary>The size of <see cref="Bounds"/>.</summary>
    public (int Width, int Height) Size => (Bounds.Width, Bounds.Height);

    /// <summary>The size of <see cref="WorkArea"/>.</summary>
    public (int Width, int Height) WorkAreaSize => (WorkArea.Width, WorkArea.Height);

    /// <summary>The display's name, e.g. "Built-in Retina Display" (NSScreen's <c>localizedName</c> on macOS).
    /// Electron's <c>label</c>.</summary>
    public string Label { get; init; } = "";

    /// <summary>The display is built into the computer, e.g. a laptop panel. Electron's <c>internal</c>.</summary>
    public bool Internal { get; init; }

    /// <summary>The display shows grayscale only. Electron's <c>monochrome</c>.</summary>
    public bool Monochrome { get; init; }

    /// <summary>The display's color space as Chromium's <c>gfx::ColorSpace::ToString()</c>, e.g.
    /// <c>{primaries:P3, transfer:SRGB, matrix:RGB, range:FULL}</c>. Electron's <c>colorSpace</c>.</summary>
    public string ColorSpace { get; init; } = DisplayColorSpace.SRgb;

    /// <summary>Bits per pixel: 24, or 30 on HDR-capable displays. Electron's <c>colorDepth</c>.</summary>
    public int ColorDepth { get; init; } = 24;

    /// <summary>Bits per color component: 8, or 10 on HDR-capable displays. Electron's <c>depthPerComponent</c>.</summary>
    public int DepthPerComponent { get; init; } = 8;

    /// <summary>Refresh rate in Hz, or 0 when unknown. Electron's <c>displayFrequency</c>.</summary>
    public double DisplayFrequency { get; init; }

    /// <summary>Whether the display takes touch input. Electron's <c>touchSupport</c>.</summary>
    public DisplayFeatureSupport TouchSupport { get; init; }

    /// <summary>Whether the display has an accelerometer. Electron's <c>accelerometerSupport</c>.</summary>
    public DisplayFeatureSupport AccelerometerSupport { get; init; }

    /// <summary>The display is detected by the system. Electron's <c>detected</c>.</summary>
    public bool Detected { get; init; } = true;

    /// <summary>The display's origin in physical pixels; (0, 0) on macOS, where Chromium leaves it unset.
    /// Electron's <c>nativeOrigin</c>.</summary>
    public Point NativeOrigin { get; init; }

    /// <summary>The largest cursor the display supports; (0, 0) where Chromium leaves it unset. Electron's <c>maximumCursorSize</c>.</summary>
    public (int Width, int Height) MaximumCursorSize { get; init; }

    // What Chromium compares for DISPLAY_METRIC_COLOR_SPACE beyond the string: full-precision parameters and HDR output.
    internal string ColorSpacesKey { get; init; } = "";

    // HDR headroom: Chromium throttles changes to it alone.
    internal float HdrMaxLuminanceRelative { get; init; } = 1;
}

/// <summary>Whether a display has a feature; Electron's <c>'available' | 'unavailable' | 'unknown'</c>.</summary>
public enum DisplayFeatureSupport
{
    /// <summary><c>unknown</c>.</summary>
    Unknown,

    /// <summary><c>available</c>.</summary>
    Available,

    /// <summary><c>unavailable</c>.</summary>
    Unavailable,
}

/// <summary>Which of a display's metrics changed; Electron's <c>changedMetrics</c>.</summary>
[Flags]
public enum DisplayMetrics
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary><see cref="Display.Bounds"/>.</summary>
    Bounds = 1 << 0,

    /// <summary><see cref="Display.WorkArea"/>.</summary>
    WorkArea = 1 << 1,

    /// <summary><see cref="Display.ScaleFactor"/>.</summary>
    ScaleFactor = 1 << 2,

    /// <summary><see cref="Display.Rotation"/>.</summary>
    Rotation = 1 << 3,
}

/// <summary>A display was attached or removed.</summary>
/// <param name="Display">The display.</param>
public sealed record DisplayEventArgs(Display Display);

/// <summary>A display's metrics changed.</summary>
/// <param name="Display">The display as it is now.</param>
/// <param name="ChangedMetrics">What changed: <see cref="DisplayMetrics.None"/> when only the color space or, on macOS,
/// the refresh rate did, which Electron also reports with an empty <c>changedMetrics</c>.</param>
public sealed record DisplayMetricsChangedEventArgs(Display Display, DisplayMetrics ChangedMetrics);

/// <summary>Displays, their sizes and the pointer position; Electron's <c>screen</c>.</summary>
/// <remarks>Use after <see cref="OrielApp.Ready"/>, from any thread. Events are raised on the UI thread.</remarks>
public static unsafe partial class Screen
{
    /// <summary>The display with the menu bar (macOS) or taskbar. Electron's <c>screen.getPrimaryDisplay()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Display GetPrimaryDisplay() => OnUi(() => Take(CefNative.cef_display_get_primary()));

    /// <summary>Every attached display. Electron's <c>screen.getAllDisplays()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static IReadOnlyList<Display> GetAllDisplays() => OnUi(AllDisplays);

    /// <summary>The display nearest <paramref name="point"/>, in screen DIPs. Electron's <c>screen.getDisplayNearestPoint()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Display GetDisplayNearestPoint(Point point) => OnUi(() =>
    {
        var p = new cef_point_t { x = point.X, y = point.Y };
        return Take(CefNative.cef_display_get_nearest_point(&p, 0));
    });

    /// <summary>The display that <paramref name="rect"/>, in screen DIPs, overlaps most. Electron's <c>screen.getDisplayMatching()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Display GetDisplayMatching(Rectangle rect) => OnUi(() =>
    {
        var r = new cef_rect_t { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
        return Take(CefNative.cef_display_get_matching_bounds(&r, 0));
    });

    /// <summary>The mouse pointer's position in screen DIPs. Electron's <c>screen.getCursorScreenPoint()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>, or no X display (Linux).</exception>
    public static Point GetCursorScreenPoint() => OnUi(() =>
    {
        var (x, y) = Pointer.Position(out var isPixels);
        return isPixels ? FromPixels(new Point(x, y)) : new Point(x, y);
    });

    /// <summary>Converts a point in physical screen pixels to DIPs, by the scale of the display it is on.
    /// Electron's <c>screen.screenToDipPoint()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Point ScreenToDipPoint(Point point) => OnUi(() => FromPixels(point));

    /// <summary>Converts a point in screen DIPs to physical pixels, by the scale of the display it is on.
    /// Electron's <c>screen.dipToScreenPoint()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Point DipToScreenPoint(Point point) => OnUi(() =>
    {
        var p = new cef_point_t { x = point.X, y = point.Y };
        var display = CefNative.cef_display_get_nearest_point(&p, 0);
        p = ToPixels(display, p);
        NativeObject.Release(display);
        return new Point(p.x, p.y);
    });

    /// <summary>Converts a rectangle in physical screen pixels to DIPs, by the scale of <paramref name="window"/>'s display,
    /// or of the display the rectangle overlaps most. Electron's <c>screen.screenToDipRect()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Rectangle ScreenToDipRect(BaseWindow? window, Rectangle rect) => OnUi(() => ConvertRect(window, rect, toPixels: false));

    /// <summary>Converts a rectangle in screen DIPs to physical pixels, by the scale of <paramref name="window"/>'s display,
    /// or of the display the rectangle overlaps most. Electron's <c>screen.dipToScreenRect()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Rectangle DipToScreenRect(BaseWindow? window, Rectangle rect) => OnUi(() => ConvertRect(window, rect, toPixels: true));

    // Both corners through one display, so the rectangle keeps a single scale even where it spans displays.
    private static Rectangle ConvertRect(BaseWindow? window, Rectangle rect, bool toPixels)
    {
        var key = window?.GetBounds() is { Width: > 0 } bounds ? bounds : rect;
        var match = new cef_rect_t { x = key.X, y = key.Y, width = key.Width, height = key.Height };
        var display = CefNative.cef_display_get_matching_bounds(&match, window is null && !toPixels ? 1 : 0);
        var topLeft = new cef_point_t { x = rect.X, y = rect.Y };
        var bottomRight = new cef_point_t { x = rect.X + rect.Width, y = rect.Y + rect.Height };
        topLeft = toPixels ? ToPixels(display, topLeft) : FromPixels(display, topLeft);
        bottomRight = toPixels ? ToPixels(display, bottomRight) : FromPixels(display, bottomRight);
        NativeObject.Release(display);
        return new Rectangle(topLeft.x, topLeft.y, bottomRight.x - topLeft.x, bottomRight.y - topLeft.y);
    }

    private static Point FromPixels(Point point)
    {
        var p = new cef_point_t { x = point.X, y = point.Y };
        var display = CefNative.cef_display_get_nearest_point(&p, 1);
        p = FromPixels(display, p);
        NativeObject.Release(display);
        return new Point(p.x, p.y);
    }

    // A display's own converters only scale. On Windows Chromium lays mixed-DPI displays out in DIPs its own way, so a
    // display's DIP origin need not be its pixel origin over its scale: the origin goes through the screen converters,
    // which convert only on Windows, and the offset from it through the display's scale.
    internal static cef_point_t ToPixels(cef_display_t* display, cef_point_t point)
    {
        if (!OperatingSystem.IsWindows())
        {
            display->convert_point_to_pixels(display, &point);
            return point;
        }
        var (origin, pixelOrigin) = Origins(display);
        var offset = new cef_point_t { x = point.x - origin.x, y = point.y - origin.y };
        display->convert_point_to_pixels(display, &offset);
        return new cef_point_t { x = pixelOrigin.x + offset.x, y = pixelOrigin.y + offset.y };
    }

    internal static cef_point_t FromPixels(cef_display_t* display, cef_point_t point)
    {
        if (!OperatingSystem.IsWindows())
        {
            display->convert_point_from_pixels(display, &point);
            return point;
        }
        var (origin, pixelOrigin) = Origins(display);
        var offset = new cef_point_t { x = point.x - pixelOrigin.x, y = point.y - pixelOrigin.y };
        display->convert_point_from_pixels(display, &offset);
        return new cef_point_t { x = origin.x + offset.x, y = origin.y + offset.y };
    }

    private static (cef_point_t Dip, cef_point_t Pixels) Origins(cef_display_t* display)
    {
        var bounds = display->get_bounds(display);
        var origin = new cef_point_t { x = bounds.x, y = bounds.y };
        return (origin, CefNative.cef_display_convert_screen_point_to_pixels(&origin));
    }

    private static IReadOnlyList<Display> AllDisplays()
    {
        var count = CefNative.cef_display_get_count();
        var natives = new cef_display_t*[(int)count];
        fixed (cef_display_t** p = natives) CefNative.cef_display_get_alls(&count, p);
        var displays = new List<Display>((int)count);
        for (var i = 0; i < (int)count; i++) displays.Add(Take(natives[i]));
        return displays;
    }

    // Reads the display and releases the reference CEF handed over.
    private static Display Take(cef_display_t* display)
    {
        static Rectangle ToRectangle(cef_rect_t r) => new(r.x, r.y, r.width, r.height);
        var value = new Display
        {
            Id = display->get_id(display),
            Bounds = ToRectangle(display->get_bounds(display)),
            WorkArea = ToRectangle(display->get_work_area(display)),
            ScaleFactor = Math.Round(display->get_device_scale_factor(display), 4),
            Rotation = display->get_rotation(display),
        };
        value = WithNativeDetails(value, display);
        NativeObject.Release(display);
        return OperatingSystem.IsMacOS() ? WithMacDetails(value) : value;
    }

    // Chromium's BuildDisplayForScreen: CEF's display id is the screen's CGDirectDisplayID.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static Display WithMacDetails(Display display)
    {
        if (MacScreen.Describe((uint)display.Id) is not { } info) return display;
        // Intel laptop panels with little headroom stay SDR there (crbug.com/1402882).
        var hdr = info.MaxPotentialEdr > 1f
            && !(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
                 && info.Builtin && info.MaxPotentialEdr <= 2f);
        var (colorSpace, key) = DisplayColorSpace.FromIccProfile(info.IccProfile);
        return display with
        {
            Label = info.Label,
            Internal = info.Builtin,
            Monochrome = info.Monochrome,
            ColorSpace = colorSpace,
            ColorSpacesKey = hdr ? key + "|hdr" : key,
            HdrMaxLuminanceRelative = hdr ? MathF.Max(1.0625f, info.MaxEdr) : 1f,
            ColorDepth = hdr ? 30 : 24,
            DepthPerComponent = hdr ? 10 : 8,
            DisplayFrequency = info.RefreshRate,
        };
    }

    private static T OnUi<T>(Func<T> func)
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true }) throw new InvalidOperationException("Use Screen after OrielApp.Ready.");
        return CefThread.InvokeOnUi(func);
    }

    // CEF reports no display changes. On macOS they follow the notifications Chromium's ScreenMac observes, on Windows the
    // window messages ScreenWin hears, on Linux the X events XDisplayManager hears; Linux without XRandR 1.3 polls while
    // anyone listens. UI thread only past the gate.
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(1);
    // ScreenMac's kMinimumHdrHeadroomUpdateInterval.
    private static readonly TimeSpan s_headroomInterval = TimeSpan.FromSeconds(
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 ? 1 / 2.0 : 1 / 12.0);
    private static readonly Lock s_gate = new();
    private static EventHandler<DisplayEventArgs>? s_added;
    private static EventHandler<DisplayEventArgs>? s_removed;
    private static EventHandler<DisplayMetricsChangedEventArgs>? s_metricsChanged;
    private static IReadOnlyList<Display>? s_lastDisplays;
    private static bool s_watching;
    private static bool s_headroomPending;

    /// <summary>How many times displays were compared; lets tests see a notification arrive.</summary>
    internal static int ChecksRun { get; private set; }

    /// <summary>A display was attached. Electron's <c>display-added</c>.</summary>
    /// <remarks>On macOS this follows <c>NSApplicationDidChangeScreenParametersNotification</c>, as in Electron; Windows
    /// reports changes as Chromium hears them, from display and work-area messages; Linux from XRandR <c>RRNotify</c> and
    /// <c>_NET_WORKAREA</c> events. Without an X display or XRandR 1.3, Linux compares displays once a second while a
    /// handler is attached, so events come up to a second late.</remarks>
    public static event EventHandler<DisplayEventArgs>? DisplayAdded
    {
        add { lock (s_gate) s_added += value; StartWatching(); }
        remove { lock (s_gate) s_added -= value; }
    }

    /// <summary>A display was removed. Electron's <c>display-removed</c>.</summary>
    /// <remarks>See <see cref="DisplayAdded"/> for when it is raised.</remarks>
    public static event EventHandler<DisplayEventArgs>? DisplayRemoved
    {
        add { lock (s_gate) s_removed += value; StartWatching(); }
        remove { lock (s_gate) s_removed -= value; }
    }

    /// <summary>A display's bounds, work area, scale, rotation, color space or (macOS) refresh rate changed.
    /// Electron's <c>display-metrics-changed</c>.</summary>
    /// <remarks>See <see cref="DisplayAdded"/> for when it is raised.</remarks>
    public static event EventHandler<DisplayMetricsChangedEventArgs>? DisplayMetricsChanged
    {
        add { lock (s_gate) s_metricsChanged += value; StartWatching(); }
        remove { lock (s_gate) s_metricsChanged -= value; }
    }

    private static bool HasListeners
    {
        get
        {
            lock (s_gate) return s_added is not null || s_removed is not null || s_metricsChanged is not null;
        }
    }

    private static void StartWatching()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true })
        {
            // Subscribed before Ready: start once CEF runs.
            if (OrielApp.CurrentOrNull is { } app) app.Ready += (_, _) => StartWatching();
            return;
        }
        CefThread.RunOnUi(() =>
        {
            if (s_watching) return;
            s_watching = true;
            s_lastDisplays = AllDisplays();
            if (OperatingSystem.IsMacOS())
                // Posted: Chromium's own observer of the same notification must refresh CEF's displays first.
                MacScreen.ObserveChanges(() => CefThread.PostUi(() => Check(headroomDue: false)));
            else if (!ObservesNatively())
                CefThread.PostUiDelayed(Poll, s_pollInterval);
        });
    }

    private static void Poll()
    {
        Check(headroomDue: true);
        if (s_watching) CefThread.PostUiDelayed(Poll, s_pollInterval);
    }

    // ScreenMac::OnNSScreensMayHaveChanged and DisplayChangeNotifier::NotifyDisplaysChanged.
    private static void Check(bool headroomDue)
    {
        ChecksRun++;
        if (!s_watching) return;
        if (!HasListeners)
        {
            s_watching = false;
            s_lastDisplays = null;
            return;
        }
        var before = s_lastDisplays ?? [];
        var now = AllDisplays();
        if (!headroomDue && OnlyHeadroomDiffers(before, now))
        {
            // Keep the old snapshot until the throttled check, as ScreenMac keeps its old displays.
            if (s_headroomPending) return;
            s_headroomPending = true;
            CefThread.PostUiDelayed(() =>
            {
                s_headroomPending = false;
                Check(headroomDue: true);
            }, s_headroomInterval);
            return;
        }
        s_lastDisplays = now;
        foreach (var change in DisplayChanges.Between(before, now, compareRefreshRate: OperatingSystem.IsMacOS())) Raise(change);
    }

    /// <summary>Replaces the snapshot the next check compares against; lets tests stage a change. UI thread.</summary>
    internal static void EditSnapshot(Func<IReadOnlyList<Display>, IReadOnlyList<Display>> edit) => s_lastDisplays = edit(s_lastDisplays ?? []);

    /// <summary>ScreenMac's test: the same displays, in order, equal but for HDR headroom, which differs.</summary>
    internal static bool OnlyHeadroomDiffers(IReadOnlyList<Display> before, IReadOnlyList<Display> after)
    {
        if (before.Count != after.Count) return false;
        var differs = false;
        for (var i = 0; i < before.Count; i++)
        {
            if (before[i] with { HdrMaxLuminanceRelative = after[i].HdrMaxLuminanceRelative } != after[i]) return false;
            differs |= before[i].HdrMaxLuminanceRelative != after[i].HdrMaxLuminanceRelative;
        }
        return differs;
    }

}
