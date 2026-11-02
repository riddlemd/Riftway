namespace Riftway;

/// <summary>Electron's rules for transparent windows and background materials, apart from the window.</summary>
internal static class WindowTransparency
{
    /// <summary>The colour behind the page: the window's own, else transparent for a transparent window or one with a
    /// material other than <c>none</c> (Electron's <c>IsTranslucent</c>, which counts <c>auto</c>), else none (CEF's
    /// white).</summary>
    internal static string? InitialBackgroundColor(string? backgroundColor, bool transparent, BackgroundMaterial? material)
        => backgroundColor ?? (transparent || material is { } m && m != BackgroundMaterial.None ? "#00000000" : null);

    /// <summary>The colour <c>setBackgroundMaterial</c> gives the window: transparent for a material, white for
    /// <c>none</c>, unchanged for <c>auto</c>.</summary>
    internal static string? MaterialBackgroundColor(BackgroundMaterial material) => material switch
    {
        BackgroundMaterial.Mica or BackgroundMaterial.Acrylic or BackgroundMaterial.Tabbed => "#00000000",
        BackgroundMaterial.None => "#FFFFFFFF",
        _ => null,
    };

    /// <summary>The page's background (Electron's <c>WebContentsPreferences::background_color_</c>): the window's colour
    /// when it has one (its <c>backgroundColor</c>, or transparent for a translucent window, which Electron copies into the
    /// page's preferences over <c>transparent</c>), else transparent when <c>webPreferences.transparent</c> is true, else
    /// none: Chromium's white. A <see cref="WebContentsView"/>'s page passes no window colour.</summary>
    internal static string? PageBackgroundColor(string? windowColor, bool transparentPreference)
        => windowColor ?? (transparentPreference ? "#00000000" : null);

    /// <summary>What the window itself is filled with: its colour, else white under a page made transparent (Electron's
    /// default window colour, where CEF's would follow the theme), else none. A transparent window on Linux stays clear
    /// whatever its colour, as Electron's <c>NativeWindowViews::SetBackgroundColor</c> keeps it there, so only the page
    /// paints the colour.</summary>
    internal static string? WindowFill(string? windowColor, bool transparentPreference, bool clearWindow = false)
        => clearWindow ? "#00000000" : windowColor ?? (transparentPreference ? "#FFFFFFFF" : null);

    /// <summary>Whether CEF creates the window translucent (<c>Widget::InitParams::opacity</c>, as Electron's
    /// <c>NativeWindowViews</c> for a transparent window). Only a libcef with Riftway's CEF patch can; Windows keeps its
    /// own see-through path, which is applied after creation.</summary>
    internal static bool TranslucentWidget(bool transparent, bool cefPatched, bool windows) => transparent && cefPatched && !windows;

    /// <summary>The browser view's colour: the window's when opaque, else transparent, so a translucent colour is
    /// painted once by the page and once by the window, as Electron's root view and page do.</summary>
    internal static uint BrowserViewColor(uint argb) => argb >> 24 == 0xFF ? argb : 0;

    /// <summary>A CSS colour for DevTools' <c>Emulation.setDefaultBackgroundColorOverride</c>: alpha 0 to 1.</summary>
    internal static (int R, int G, int B, double A) PageColor(uint argb)
        => ((int)(argb >> 16) & 0xFF, (int)(argb >> 8) & 0xFF, (int)argb & 0xFF, (argb >> 24) / 255.0);

    /// <summary>What <c>isResizable</c> reports. A transparent window has no sizing border, and Electron then
    /// reports a framed one as not resizable but a frameless one as set.</summary>
    internal static bool ReportsResizable(bool frame, bool transparent, bool resizable) => resizable && !(frame && transparent);

    /// <summary>Electron's maximized test for a transparent window, which cannot really maximize: it fills the work
    /// area and is not minimized.</summary>
    internal static bool FillsWorkArea(Rectangle bounds, Rectangle workArea, bool minimized) => !minimized && bounds == workArea;
}
