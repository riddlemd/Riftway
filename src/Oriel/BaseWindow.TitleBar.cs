using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

public unsafe partial class BaseWindow
{
    /// <summary>Changes the colours or height of the buttons drawn over a hidden title bar; null fields keep their value.
    /// Electron's <c>win.setTitleBarOverlay()</c>.</summary>
    /// <exception cref="InvalidOperationException">The window was not created with a hidden title bar and an overlay.</exception>
    /// <exception cref="ArgumentException">A colour Chromium's CSS colour parser rejects; Electron's <c>TypeError</c>,
    /// with its message. As in Electron, a valid colour given with an invalid symbol colour is still kept.</exception>
    public void SetTitleBarOverlay(TitleBarOverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TitleBar.HasOverlay(_options.TitleBarStyle, _options.TitleBarOverlay, OperatingSystem.IsMacOS()))
            throw new InvalidOperationException("The title bar overlay is not enabled: create the window with a hidden TitleBarStyle and TitleBarOverlay.");
        uint? color = null, symbol = null;
        if (options.Color is { } c)
            color = CssColor.TryParse(c, out var argb) ? argb : throw new ArgumentException("Could not parse color as CSS color");
        if (options.SymbolColor is { } s)
        {
            if (!CssColor.TryParse(s, out var argb))
            {
                // Electron stores the colour before it rejects the symbol colour, and repaints nothing.
                if (color is { } kept) Command(_ => _titleBarColors.Color = kept);
                throw new ArgumentException("Could not parse symbol color as CSS color");
            }
            symbol = argb;
        }
        Command(_ =>
        {
            if (_titleBarOverlay is not { } overlay) return;
            var (oldColor, oldSymbol) = _titleBarColors;
            _titleBarColors = (color ?? oldColor, symbol ?? oldSymbol);
            overlay.Update(_titleBarColors.Color, _titleBarColors.Symbol, options.Height ?? overlay.Height);
            ApplyDraggableRegions(_window);
        });
    }

    // Not in Electron, whose page reads this through the Window Controls Overlay API (DRIFT W34); kept for tests.
    internal Rectangle GetTitleBarOverlayBounds() => Query(w =>
    {
        if (_titleBarOverlay is not { } overlay) return default;
        var bounds = ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base));
        return new Rectangle(bounds.Width - overlay.Width, 0, overlay.Width, overlay.Height);
    }, default);

    private bool IsFrameless => TitleBar.IsFrameless(_options.Frame, _options.TitleBarStyle, OperatingSystem.IsMacOS());

    private (uint Color, uint Symbol) _titleBarColors;

    private static uint CaptionForeground() => TitleBar.CaptionForeground(TitleBar.DefaultFrame(OperatingSystem.IsWindows() && WindowsWindowStyle.AppsUseDarkTheme()));

    private void AttachTitleBarOverlay(cef_window_t* w, TitleBarOverlayOptions options)
    {
        var (face, text) = OperatingSystem.IsWindows() ? WindowsWindowStyle.ButtonColors() : (0xFFF0F0F0u, 0xFF000000u);
        // Electron ignores a colour it cannot parse here and keeps the default.
        _titleBarColors = (CssColor.TryParse(options.Color, out var color) ? color : face, CssColor.TryParse(options.SymbolColor, out var symbol) ? symbol : text);
        var height = options.Height ?? (OperatingSystem.IsWindows() ? WindowsWindowStyle.CaptionHeight(Hwnd(w)) : TitleBar.FallbackHeight);
        _titleBarOverlay = new TitleBarOverlayView(this, CaptionForeground);
        _titleBarOverlay.Attach(w, _titleBarColors.Color, _titleBarColors.Symbol, height);
        _titleBarOverlay.SetMaximized(w->is_maximized(w) != 0);
        UpdateCaptionButtons();
        if (OperatingSystem.IsWindows())
        {
            var overlay = _titleBarOverlay;
            WindowsSubclass.RouteMaximizeButton(Hwnd(w), overlay.IsOverMaximize, overlay.OnMaximizeEvent);
            PassMaximizeHitTests(w);
        }
    }

    private protected void SetDraggableRegionsOn(cef_window_t* w, IReadOnlyList<DraggableRegion> regions)
    {
        _pageRegions = regions;
        ApplyDraggableRegions(w);
    }

    // CEF hit-tests the regions in window coordinates; the page starts below a menu bar. CEF ignores them in a framed
    // window, as Electron does. The overlay's buttons are cut out, or a drag strip under them would take their clicks.
    private void ApplyDraggableRegions(cef_window_t* w)
    {
        if (w is null || (_pageRegions.Count == 0 && _titleBarOverlay is null)) return;
        var window = ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base));
        var page = ContentBoundsOn(w);
        var moved = TitleBar.ToWindow(_pageRegions, page.X - window.X, page.Y - window.Y);
        if (_titleBarOverlay is { } overlay)
            moved = [.. moved, new DraggableRegion(window.Width - overlay.Width, 0, overlay.Width, overlay.Height, Draggable: false)];
        var native = new cef_draggable_region_t[moved.Length];
        for (var i = 0; i < moved.Length; i++)
            native[i] = new cef_draggable_region_t
            {
                bounds = new cef_rect_t { x = moved[i].X, y = moved[i].Y, width = moved[i].Width, height = moved[i].Height },
                draggable = moved[i].Draggable ? 1 : 0,
            };
        fixed (cef_draggable_region_t* first = native) w->set_draggable_regions(w, (nuint)native.Length, first);
    }

    // Chromium's page window covers the overlay and takes the hit test first; it can be recreated, so this repeats.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void PassMaximizeHitTests(cef_window_t* w)
    {
        if (_titleBarOverlay is not { } overlay) return;
        foreach (var page in WindowsWindowStyle.ChildrenOfClass(Hwnd(w), "Chrome_RenderWidgetHostHWND"))
            WindowsSubclass.PassHitTestToParent(page, overlay.IsOverMaximize);
    }
}
