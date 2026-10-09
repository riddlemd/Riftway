using System.Runtime.InteropServices;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

// Electron's Linux systemPreferences: getAccentColor from NativeTheme's user colour (the portal's accent-color, set by
// Chromium's DarkModeManagerLinux), accent-color-changed on native theme updates, and gfx::Animation's GTK reading.
public static unsafe partial class SystemPreferences
{
    private static bool s_linuxStarted;

    // NativeTheme::user_color, which Chromium sets only from the portal it found.
    private static (double R, double G, double B)? LinuxPortalAccent()
        => LinuxPortalSettings.Appearance is { Present: true, Accent: var accent } ? accent : null;

    /// <summary>DarkModeManagerLinux's <c>SkColorSetRGB(r * 255, g * 255, b * 255)</c>: each channel truncated, opaque.</summary>
    internal static uint? LinuxAccentArgb((double R, double G, double B)? accent)
        => accent is var (r, g, b) ? 0xFF000000u | (uint)(r * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255) : null;

    /// <summary>gfx::Animation on Linux: every value follows GTK's <c>gtk-enable-animations</c>, taken as on without GTK;
    /// the force switches still decide <c>prefersReducedMotion</c>.</summary>
    internal static AnimationSettings LinuxAnimationSettings(IReadOnlyList<string> args, bool? gtkAnimations)
    {
        var enabled = gtkAnimations ?? true;
        return new AnimationSettings(enabled, enabled, ForcedReducedMotion(args, windows: false) ?? !enabled);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static AnimationSettings LinuxAnimationSettings()
    {
        // Chromium reads GTK only once its LinuxUi exists, which is from Ready on here.
        bool? gtk = OrielApp.CurrentOrNull is { IsReady: true }
            ? CefThread.InvokeOnUi(() => LinuxGtkTheme.IsLoaded ? LinuxGtkTheme.AnimationsEnabled() : (bool?)null)
            : null;
        return LinuxAnimationSettings(Environment.GetCommandLineArgs(), gtk);
    }

    // Electron's constructor: observe NativeTheme for native UI. Its updates come from the portal and from GTK's settings.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void StartLinux()
    {
        lock (s_gate)
        {
            if (s_linuxStarted) return;
            s_linuxStarted = true;
        }
        LinuxPortalSettings.Changed += OnLinuxThemeUpdated;
        ObserveGtk();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void ObserveGtk()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true })
        {
            if (OrielApp.CurrentOrNull is { } app) app.Ready += (_, _) => ObserveGtk();
            return;
        }
        CefThread.RunOnUi(() =>
        {
            if (LinuxGtkTheme.IsLoaded) LinuxGtkTheme.Observe(&OnGtkSetting);
        });
    }

    [UnmanagedCallersOnly]
    private static void OnGtkSetting(nint settings, nint spec, nint data) => OnLinuxThemeUpdated();

    // OnNativeThemeUpdated posts OnNativeThemeUpdatedOnUI, which compares with the last accent reported.
    private static void OnLinuxThemeUpdated()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true }) return;
        try
        {
            CefThread.PostUi(() =>
            {
                lock (s_gate) s_accent = LinuxAccentArgb(LinuxPortalAccent());
                OnAccentColorChanged();
            });
        }
        catch (InvalidOperationException) { }
    }
}
