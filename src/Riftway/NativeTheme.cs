using System.Runtime.InteropServices;
using Riftway.Cef;
using Riftway.Cef.Interop;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>Where the app's light or dark appearance comes from. Electron's <c>nativeTheme.themeSource</c> values.</summary>
public enum ThemeSource
{
    /// <summary>Follow the operating system.</summary>
    System,
    /// <summary>Always light.</summary>
    Light,
    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>The app's colour scheme: pages' <c>prefers-color-scheme</c>, Chromium's own UI and (on macOS) native menus and
/// dialogs. Electron's <c>nativeTheme</c>.</summary>
public static unsafe partial class NativeTheme
{
    private static readonly Lock s_gate = new();
    private static ThemeSource s_source;
    private static EventHandler? s_updated;
    private static bool s_polling;
    private static Snapshot s_last;
    // Set on the UI thread once CEF is up: GTK is read only there.
    private static volatile bool s_linuxUi;

    private static readonly TimeSpan s_poll = TimeSpan.FromSeconds(1);

    /// <summary>Overrides the system's light or dark choice for the app's pages and UI, or follows it again.
    /// Electron's <c>nativeTheme.themeSource</c>.</summary>
    /// <remarks>Raises <see cref="Updated"/> when <see cref="ShouldUseDarkColors"/> changes, as Electron's
    /// <c>set_theme_source</c> patch. Set before the app is ready, it applies when it is.</remarks>
    public static ThemeSource ThemeSource
    {
        get
        {
            lock (s_gate) return s_source;
        }
        set
        {
            if (RiftwayApp.CurrentOrNull is not { IsReady: true })
            {
                lock (s_gate) s_source = value;
                return;
            }
            var wasDark = ShouldUseDarkColors;
            lock (s_gate) s_source = value;
            CefThread.InvokeOnUi(() =>
            {
                ApplyEverywhere();
                return true;
            });
            if (ShouldUseDarkColors != wasDark) Raise();
        }
    }

    /// <summary>Whether the app should draw dark: <see cref="ThemeSource"/>, or the system's setting.
    /// Electron's <c>nativeTheme.shouldUseDarkColors</c>.</summary>
    public static bool ShouldUseDarkColors => ThemeSource switch
    {
        ThemeSource.Dark => true,
        ThemeSource.Light => false,
        _ => SystemPrefersDark(),
    };

    /// <summary>Whether the system asks for high contrast (macOS "Increase contrast", Windows contrast themes, a GTK theme
    /// named high contrast on Linux). Electron's <c>nativeTheme.shouldUseHighContrastColors</c>.</summary>
    /// <remarks>On Windows, a contrast theme whose window text contrasts with its window at 7:1 or more.</remarks>
    public static bool ShouldUseHighContrastColors
        => OperatingSystem.IsMacOS() ? MacFlag(MacSetting.IncreaseContrast)
            : OperatingSystem.IsWindows() ? WindowsTheme.ShouldUseHighContrastColors(WindowsVariables())
            : OperatingSystem.IsLinux() && LinuxFlag(dark: false);

    /// <summary>Whether macOS inverts colours, or on Windows whether a dark contrast theme is on.
    /// Electron's <c>nativeTheme.shouldUseInvertedColorScheme</c>.</summary>
    public static bool ShouldUseInvertedColorScheme
        => OperatingSystem.IsMacOS() ? MacFlag(MacSetting.InvertColors)
            : OperatingSystem.IsWindows() && WindowsTheme.ShouldUseInvertedColorScheme(ThemeSource, WindowsVariables());

    /// <summary>Whether Windows forces a contrast theme's colours. Electron's <c>nativeTheme.inForcedColorsMode</c>.</summary>
    public static bool InForcedColorsMode => OperatingSystem.IsWindows() && WindowsVariables().ForcedColors;

    /// <summary>Whether macOS asks for less transparency, or Windows' transparency effects are off.
    /// Electron's <c>nativeTheme.prefersReducedTransparency</c>.</summary>
    public static bool PrefersReducedTransparency
        => OperatingSystem.IsMacOS() ? MacFlag(MacSetting.ReduceTransparency) : OperatingSystem.IsWindows() && WindowsVariables().ReducedTransparency;

    /// <summary>Something above changed: <see cref="ThemeSource"/> was set, or the system's settings changed (at once on
    /// macOS, Windows and Linux; DRIFT Y1). Electron's <c>updated</c> event.</summary>
    public static event EventHandler? Updated
    {
        add
        {
            lock (s_gate)
            {
                s_updated += value;
                // Windows watches from OnReady on, handlers or not, as Chromium's OsSettingsProviderWin.
                if (s_polling || OperatingSystem.IsWindows() || RiftwayApp.CurrentOrNull is not { IsReady: true }) return;
                s_polling = true;
            }
            StartWatching();
        }
        remove
        {
            lock (s_gate) s_updated -= value;
        }
    }

    /// <summary>UI thread: applies the source to a new session's profile.</summary>
    internal static void ApplyTo(cef_request_context_t* context)
        => context->set_chrome_color_scheme(context, Variant(ThemeSource), 0);

    /// <summary>UI thread, once CEF is up: applies a source set before then, and starts polling for early handlers.</summary>
    internal static void OnReady()
    {
        if (OperatingSystem.IsLinux()) ObserveLinux();
        ApplyEverywhere();
        if (OperatingSystem.IsWindows())
        {
            StartWindows();
            return;
        }
        bool start;
        lock (s_gate)
        {
            start = s_updated is not null && !s_polling;
            if (start) s_polling = true;
        }
        if (start) StartWatching();
    }

    /// <summary>System checks made so far; for tests.</summary>
    internal static int Checks { get; private set; }

    private static void StartWatching()
    {
        s_last = Take();
        if (OperatingSystem.IsMacOS()) CefThread.RunOnUi(ObserveMac);
        else if (!OperatingSystem.IsLinux()) CefThread.PostUiDelayed(Poll, s_poll);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void ObserveMac() => MacAppearance.ObserveChanges(Check);

    // UI thread.
    private static void Check()
    {
        Checks++;
        var now = Take();
        if (now == s_last) return;
        s_last = now;
        Raise();
    }

    internal static cef_color_variant_t Variant(ThemeSource source) => source switch
    {
        ThemeSource.Light => cef_color_variant_t.CEF_COLOR_VARIANT_LIGHT,
        ThemeSource.Dark => cef_color_variant_t.CEF_COLOR_VARIANT_DARK,
        _ => cef_color_variant_t.CEF_COLOR_VARIANT_SYSTEM,
    };

    // UI thread.
    private static void ApplyEverywhere()
    {
        var global = CefNative.cef_request_context_get_global_context();
        ApplyTo(global);
        NativeObject.Release(global);
        foreach (var session in Session.Created()) ApplyTo(session.Native);
        if (OperatingSystem.IsMacOS()) MacAppearance.SetDark(ThemeSource switch { ThemeSource.Dark => true, ThemeSource.Light => false, _ => null });
    }

    // Accent: the portal's, which Chromium's NativeTheme also takes as its user colour and notifies on.
    private readonly record struct Snapshot(bool Dark, bool HighContrast, bool Inverted, bool ReducedTransparency, (double, double, double)? Accent);

    private static Snapshot Take()
        => new(ShouldUseDarkColors, ShouldUseHighContrastColors, ShouldUseInvertedColorScheme, PrefersReducedTransparency,
            OperatingSystem.IsLinux() && LinuxPortalSettings.Appearance.Present ? LinuxPortalSettings.Appearance.Accent : null);

    private static void Poll()
    {
        lock (s_gate)
        {
            if (s_updated is null)
            {
                s_polling = false;
                return;
            }
        }
        Check();
        CefThread.PostUiDelayed(Poll, s_poll);
    }

    private static void Raise()
    {
        EventHandler? handlers;
        lock (s_gate) handlers = s_updated;
        handlers?.Invoke(null, EventArgs.Empty);
    }

    private enum MacSetting { Dark, IncreaseContrast, InvertColors, ReduceTransparency }

    // AppKit is read on the main thread, which is CEF's UI thread on macOS.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool MacFlag(MacSetting setting)
    {
        bool Read() => setting switch
        {
            MacSetting.Dark => MacAppearance.IsDark(),
            MacSetting.IncreaseContrast => MacAppearance.IncreaseContrast,
            MacSetting.InvertColors => MacAppearance.InvertColors,
            _ => MacAppearance.ReduceTransparency,
        };
        return RiftwayApp.CurrentOrNull is { IsReady: true } ? CefThread.InvokeOnUi(Read) : Read();
    }

    private static bool SystemPrefersDark()
    {
        if (OperatingSystem.IsMacOS()) return MacFlag(MacSetting.Dark);
        if (OperatingSystem.IsWindows()) return WindowsVariables().Dark;
        if (OperatingSystem.IsLinux()) return LinuxFlag(dark: true);
        return false;
    }

    /// <summary>Chromium 154 on Linux: no GTK UI, no system theme at all (the portal's preference is pushed only into GTK's
    /// settings provider); else the portal's dark or light; else whether GTK's window background is dark.</summary>
    internal static bool LinuxPrefersDark(bool toolkitLoaded, LinuxPortalAppearance portal, Func<bool> backgroundIsDark)
        => toolkitLoaded && (LinuxPortalSettings.PrefersDark(portal.ColorScheme) ?? backgroundIsDark());

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static bool LinuxFlag(bool dark)
    {
        if (!s_linuxUi) return false;
        return CefThread.InvokeOnUi(() =>
        {
            var toolkit = LinuxGtkTheme.IsLoaded;
            return dark
                ? LinuxPrefersDark(toolkit, LinuxPortalSettings.Appearance, () => LinuxGtkTheme.IsDark(LinuxGtkTheme.WindowBackground()))
                : toolkit && LinuxGtkTheme.IsHighContrastThemeName(LinuxGtkTheme.ThemeName());
        });
    }

    // UI thread. The portal's signals and GTK's setting notifications replace polling.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void ObserveLinux()
    {
        s_linuxUi = true;
        LinuxPortalSettings.Changed += () =>
        {
            try { CefThread.PostUi(OnLinuxChanged); }
            catch (InvalidOperationException) { }
        };
        if (LinuxGtkTheme.IsLoaded) LinuxGtkTheme.Observe(&OnGtkSetting);
    }

    [UnmanagedCallersOnly]
    private static void OnGtkSetting(nint settings, nint spec, nint data) => OnLinuxChanged();

    // UI thread.
    private static void OnLinuxChanged()
    {
        lock (s_gate)
        {
            if (!s_polling) return;
            if (s_updated is null)
            {
                s_polling = false;
                return;
            }
        }
        Check();
    }
}
