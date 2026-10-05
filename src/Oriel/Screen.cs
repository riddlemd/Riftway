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
/// <param name="ChangedMetrics">What changed.</param>
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
        display->convert_point_to_pixels(display, &p);
        NativeObject.Release(display);
        return new Point(p.x, p.y);
    });

    /// <summary>Converts a rectangle in physical screen pixels to DIPs, by the scale of <paramref name="window"/>'s display,
    /// or of the display the rectangle overlaps most. Electron's <c>screen.screenToDipRect()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Rectangle ScreenToDipRect(BrowserWindow? window, Rectangle rect) => OnUi(() => ConvertRect(window, rect, toPixels: false));

    /// <summary>Converts a rectangle in screen DIPs to physical pixels, by the scale of <paramref name="window"/>'s display,
    /// or of the display the rectangle overlaps most. Electron's <c>screen.dipToScreenRect()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    public static Rectangle DipToScreenRect(BrowserWindow? window, Rectangle rect) => OnUi(() => ConvertRect(window, rect, toPixels: true));

    // Both corners through one display, so the rectangle keeps a single scale even where it spans displays.
    private static Rectangle ConvertRect(BrowserWindow? window, Rectangle rect, bool toPixels)
    {
        var key = window?.GetBounds() is { Width: > 0 } bounds ? bounds : rect;
        var match = new cef_rect_t { x = key.X, y = key.Y, width = key.Width, height = key.Height };
        var display = CefNative.cef_display_get_matching_bounds(&match, window is null && !toPixels ? 1 : 0);
        var topLeft = new cef_point_t { x = rect.X, y = rect.Y };
        var bottomRight = new cef_point_t { x = rect.X + rect.Width, y = rect.Y + rect.Height };
        if (toPixels)
        {
            display->convert_point_to_pixels(display, &topLeft);
            display->convert_point_to_pixels(display, &bottomRight);
        }
        else
        {
            display->convert_point_from_pixels(display, &topLeft);
            display->convert_point_from_pixels(display, &bottomRight);
        }
        NativeObject.Release(display);
        return new Rectangle(topLeft.x, topLeft.y, bottomRight.x - topLeft.x, bottomRight.y - topLeft.y);
    }

    // Per display: CEF's screen-wide converters only convert on Windows.
    private static Point FromPixels(Point point)
    {
        var p = new cef_point_t { x = point.X, y = point.Y };
        var display = CefNative.cef_display_get_nearest_point(&p, 1);
        display->convert_point_from_pixels(display, &p);
        NativeObject.Release(display);
        return new Point(p.x, p.y);
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
        return value;
    }

    private static T OnUi<T>(Func<T> func)
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true }) throw new InvalidOperationException("Use Screen after OrielApp.Ready.");
        return CefThread.InvokeOnUi(func);
    }

    // CEF reports no display changes, so macOS and Linux poll while anyone listens. UI thread only past the gate.
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(1);
    private static readonly Lock s_gate = new();
    private static EventHandler<DisplayEventArgs>? s_added;
    private static EventHandler<DisplayEventArgs>? s_removed;
    private static EventHandler<DisplayMetricsChangedEventArgs>? s_metricsChanged;
    private static IReadOnlyList<Display>? s_lastDisplays;
    private static bool s_polling;

    /// <summary>A display was attached. Electron's <c>display-added</c>.</summary>
    /// <remarks>Windows reports changes as Chromium hears them, from display and work-area messages. On macOS and Linux displays are compared once a second while a handler is attached, so events come up to a second late.</remarks>
    public static event EventHandler<DisplayEventArgs>? DisplayAdded
    {
        add { lock (s_gate) s_added += value; StartPolling(); }
        remove { lock (s_gate) s_added -= value; }
    }

    /// <summary>A display was removed. Electron's <c>display-removed</c>.</summary>
    /// <remarks>Polled; see <see cref="DisplayAdded"/>.</remarks>
    public static event EventHandler<DisplayEventArgs>? DisplayRemoved
    {
        add { lock (s_gate) s_removed += value; StartPolling(); }
        remove { lock (s_gate) s_removed -= value; }
    }

    /// <summary>A display's bounds, work area, scale or rotation changed. Electron's <c>display-metrics-changed</c>.</summary>
    /// <remarks>Polled; see <see cref="DisplayAdded"/>.</remarks>
    public static event EventHandler<DisplayMetricsChangedEventArgs>? DisplayMetricsChanged
    {
        add { lock (s_gate) s_metricsChanged += value; StartPolling(); }
        remove { lock (s_gate) s_metricsChanged -= value; }
    }

    private static bool HasListeners
    {
        get
        {
            lock (s_gate) return s_added is not null || s_removed is not null || s_metricsChanged is not null;
        }
    }

    private static void StartPolling()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true })
        {
            // Subscribed before Ready: start once CEF runs.
            if (OrielApp.CurrentOrNull is { } app) app.Ready += (_, _) => StartPolling();
            return;
        }
        CefThread.RunOnUi(() =>
        {
            if (s_polling) return;
            s_polling = true;
            s_lastDisplays = AllDisplays();
            if (ObservesNatively()) return;
            CefThread.PostUiDelayed(Poll, s_pollInterval);
        });
    }

    private static void Poll()
    {
        if (!HasListeners)
        {
            s_polling = false;
            return;
        }
        var now = AllDisplays();
        var changes = DisplayChanges.Between(s_lastDisplays ?? [], now);
        s_lastDisplays = now;
        foreach (var change in changes) Raise(change);
        CefThread.PostUiDelayed(Poll, s_pollInterval);
    }
}
