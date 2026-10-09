using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What <see cref="SystemPreferences.GetAnimationSettings"/> returns; Electron's <c>getAnimationSettings()</c> object.</summary>
/// <param name="ShouldRenderRichAnimation">Whether rich animations should be rendered, judged from the session type
/// (e.g. remote desktop) and accessibility settings.</param>
/// <param name="ScrollAnimationsEnabledBySystem">Whether scroll animations (e.g. from Home and End) should run.</param>
/// <param name="PrefersReducedMotion">Whether the user asks for reduced motion.</param>
public sealed record AnimationSettings(bool ShouldRenderRichAnimation, bool ScrollAnimationsEnabledBySystem, bool PrefersReducedMotion);

/// <summary>A device kind for <see cref="SystemPreferences.GetMediaAccessStatus"/>; Electron's <c>mediaType</c>.</summary>
public enum MediaAccessType
{
    /// <summary><c>microphone</c>.</summary>
    Microphone,

    /// <summary><c>camera</c>.</summary>
    Camera,

    /// <summary><c>screen</c>.</summary>
    Screen,
}

/// <summary>The user's consent for a device kind; Electron's <c>getMediaAccessStatus()</c> strings.</summary>
public enum MediaAccessStatus
{
    /// <summary><c>not-determined</c>.</summary>
    NotDetermined,

    /// <summary><c>granted</c>.</summary>
    Granted,

    /// <summary><c>denied</c>: the user turned access off.</summary>
    Denied,

    /// <summary><c>restricted</c>: the system or a policy turned access off.</summary>
    Restricted,

    /// <summary><c>unknown</c>.</summary>
    Unknown,
}

/// <summary>The system accent colour changed.</summary>
/// <param name="NewColor">The new colour as <c>RRGGBBAA</c> hex, as <see cref="SystemPreferences.GetAccentColor"/> returns it.</param>
public sealed record AccentColorChangedEventArgs(string NewColor);

/// <summary>System colours, accent colour, animation settings and device consent; Electron's <c>systemPreferences</c>.</summary>
/// <remarks>Windows only so far; the macOS-only members are not ported. Events are raised on the UI thread, once
/// <see cref="OrielApp.Ready"/> has fired.</remarks>
public static class SystemPreferences
{
    private static readonly Lock s_gate = new();
    private static EventHandler<AccentColorChangedEventArgs>? s_accentChanged;
    private static EventHandler? s_colorChanged;
    private static bool s_started;
    private static uint? s_accent;
    private static string s_currentAccent = "";

    /// <summary>The user's accent colour as <c>RRGGBBAA</c> hex without a <c>#</c>, e.g. <c>"0078D4FF"</c>, or <c>""</c> when
    /// Windows has none. Electron's <c>systemPreferences.getAccentColor()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not Windows.</exception>
    public static string GetAccentColor()
    {
        if (!OperatingSystem.IsWindows()) throw NotWindows();
        EnsureStarted();
        lock (s_gate) return FormatAccent(s_accent);
    }

    /// <summary>A system colour as <c>#RRGGBBAA</c> hex, e.g. <c>"#F0F0F0FF"</c>. Electron's <c>systemPreferences.getColor()</c>.</summary>
    /// <param name="color">One of Electron's Windows names, e.g. <c>"window"</c>, <c>"highlight"</c> or <c>"3d-face"</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="color"/> is not one of the names (<c>Unknown color: …</c>).</exception>
    /// <exception cref="PlatformNotSupportedException">Not Windows.</exception>
    public static string GetColor(string color)
    {
        if (!OperatingSystem.IsWindows()) throw NotWindows();
        if (SysColorIndex(color) is not { } index) throw new ArgumentException("Unknown color: " + color, nameof(color));
        return ToRgbaHex(WindowsSystemPreferences.ColorRefToArgb(WindowsSystemPreferences.SysColor(index)), includeHash: true);
    }

    /// <summary>Whether the user lets desktop apps use <paramref name="mediaType"/>. Always <see cref="MediaAccessStatus.Granted"/>
    /// for <see cref="MediaAccessType.Screen"/>. Electron's <c>systemPreferences.getMediaAccessStatus()</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mediaType"/> is not a defined value.</exception>
    /// <exception cref="PlatformNotSupportedException">Not Windows.</exception>
    public static MediaAccessStatus GetMediaAccessStatus(MediaAccessType mediaType)
    {
        if (!OperatingSystem.IsWindows()) throw NotWindows();
        return mediaType switch
        {
            MediaAccessType.Camera => ToMediaAccessStatus(WindowsSystemPreferences.DeviceAccessStatus(4 /* DeviceClass_VideoCapture */)),
            MediaAccessType.Microphone => ToMediaAccessStatus(WindowsSystemPreferences.DeviceAccessStatus(1 /* DeviceClass_AudioCapture */)),
            MediaAccessType.Screen => ToMediaAccessStatus(1 /* DeviceAccessStatus_Allowed */),
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType), "Invalid media type"),
        };
    }

    /// <summary>The system's animation settings. Electron's <c>systemPreferences.getAnimationSettings()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not Windows.</exception>
    public static AnimationSettings GetAnimationSettings()
    {
        if (!OperatingSystem.IsWindows()) throw NotWindows();
        var animation = WindowsSystemPreferences.ClientAreaAnimation();
        var rich = ShouldRenderRichAnimation(animation, WindowsSystemPreferences.IsCurrentSessionRemote);
        // ScrollAnimationsEnabledBySystem is ShouldRenderRichAnimation on Windows.
        return new AnimationSettings(rich, rich, PrefersReducedMotion(Environment.GetCommandLineArgs(), animation));
    }

    /// <summary>The accent colour changed. Electron's <c>accent-color-changed</c>.</summary>
    /// <remarks>Windows only. Raised when the DWM registry key changes and the colour read back differs from the last one.</remarks>
    public static event EventHandler<AccentColorChangedEventArgs>? AccentColorChanged
    {
        add { lock (s_gate) s_accentChanged += value; EnsureStarted(); }
        remove { lock (s_gate) s_accentChanged -= value; }
    }

    /// <summary>A system colour changed, or high contrast was turned on or off. Electron's <c>color-changed</c>.</summary>
    /// <remarks>Windows only: raised for <c>WM_SYSCOLORCHANGE</c>, and <c>WM_SETTINGCHANGE</c> with <c>SPI_SETHIGHCONTRAST</c>.</remarks>
    public static event EventHandler? ColorChanged
    {
        add { lock (s_gate) s_colorChanged += value; EnsureStarted(); }
        remove { lock (s_gate) s_colorChanged -= value; }
    }

    // Electron's SystemPreferences constructor: the accent baseline and watch at once, the window once the app is ready.
    private static void EnsureStarted()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (s_gate)
        {
            if (s_started) return;
            s_started = true;
            s_accent = WindowsSystemPreferences.AccentColor();
            s_currentAccent = FormatAccent(s_accent);
            // The pool wait keeps the watch alive.
            _ = WindowsSystemPreferences.WatchKey(WindowsSystemPreferences.DwmKey, OnDwmKeyUpdated);
        }
        StartWindow();
    }

    private static void StartWindow()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true })
        {
            // Electron waits for ready too: an earlier window stops global shortcuts firing.
            if (OrielApp.CurrentOrNull is { } app) app.Ready += (_, _) => StartWindow();
            return;
        }
        CefThread.RunOnUi(() =>
        {
            if (OperatingSystem.IsWindows()) _ = WindowsSystemPreferences.CreateWindow(OnWindowMessage);
        });
    }

    private static void OnWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (!IsColorChange(message, wParam)) return;
        EventHandler? handlers;
        lock (s_gate) handlers = s_colorChanged;
        handlers?.Invoke(null, EventArgs.Empty);
    }

    // AccentColorObserver::OnDwmKeyUpdated: re-read the colour, then notify.
    private static void OnDwmKeyUpdated()
    {
        if (!OperatingSystem.IsWindows()) return;
        var accent = WindowsSystemPreferences.AccentColor();
        lock (s_gate) s_accent = accent;
        NotifyAccent();
    }

    /// <summary>Chromium's <c>AccentColorObserver::SetAccentColorForTesting</c>: replaces the cached accent colour until the
    /// DWM key next changes, and notifies as a change would.</summary>
    internal static void SetAccentColorForTesting(uint? argb)
    {
        EnsureStarted();
        lock (s_gate) s_accent = argb;
        NotifyAccent();
    }

    // Electron compares and emits from a fresh UI task, not inside the observer's notification.
    private static void NotifyAccent()
    {
        if (OrielApp.CurrentOrNull is { IsReady: true }) CefThread.PostUi(OnAccentColorChanged);
        else OnAccentColorChanged();
    }

    private static void OnAccentColorChanged()
    {
        EventHandler<AccentColorChangedEventArgs>? handlers;
        string color;
        lock (s_gate)
        {
            if (!TryUpdateAccent(ref s_currentAccent, FormatAccent(s_accent))) return;
            color = s_currentAccent;
            handlers = s_accentChanged;
        }
        handlers?.Invoke(null, new AccentColorChangedEventArgs(color));
    }

    /// <summary>Electron's <c>OnAccentColorChanged</c> test: true, storing <paramref name="next"/>, only when it differs.</summary>
    internal static bool TryUpdateAccent(ref string current, string next)
    {
        if (next == current) return false;
        current = next;
        return true;
    }

    /// <summary>Electron's <c>OnWndProc</c> filter for <c>color-changed</c>.</summary>
    internal static bool IsColorChange(uint message, nint wParam) =>
        message == 0x0015 /* WM_SYSCOLORCHANGE */ || (message == 0x001A /* WM_SETTINGCHANGE */ && wParam == 0x0043 /* SPI_SETHIGHCONTRAST */);

    /// <summary>Electron's <c>GetAccentColor</c>: <c>ToRGBAHex(color, false)</c>, or <c>""</c> without a colour.</summary>
    internal static string FormatAccent(uint? argb) => argb is { } c ? ToRgbaHex(c, includeHash: false) : "";

    /// <summary>Electron's <c>ToRGBAHex</c>: <c>RRGGBBAA</c> in upper case from 0xAARRGGBB.</summary>
    internal static string ToRgbaHex(uint argb, bool includeHash) =>
        $"{(includeHash ? "#" : "")}{(argb >> 16) & 0xFF:X2}{(argb >> 8) & 0xFF:X2}{argb & 0xFF:X2}{argb >> 24:X2}";

    /// <summary>Electron's <c>ConvertDeviceAccessStatus</c> from a WinRT <c>DeviceAccessStatus</c>.</summary>
    internal static MediaAccessStatus ToMediaAccessStatus(int status) => status switch
    {
        0 => MediaAccessStatus.NotDetermined,
        1 => MediaAccessStatus.Granted,
        3 => MediaAccessStatus.Restricted,
        2 => MediaAccessStatus.Denied,
        _ => MediaAccessStatus.Unknown,
    };

    /// <summary>gfx::Animation's Windows <c>ShouldRenderRichAnimationImpl</c>: the "Animation effects" setting, or not
    /// a remote session when that can't be read.</summary>
    internal static bool ShouldRenderRichAnimation(bool? clientAreaAnimation, Func<bool> isRemoteSession) =>
        clientAreaAnimation ?? !isRemoteSession();

    /// <summary>gfx::Animation's <c>PrefersReducedMotion</c>: the force switches first, then the inverse of "Animation
    /// effects", assumed on when it can't be read.</summary>
    internal static bool PrefersReducedMotion(IReadOnlyList<string> args, bool? clientAreaAnimation)
    {
        if (HasSwitch(args, "force-prefers-reduced-motion")) return true;
        if (HasSwitch(args, "force-prefers-no-reduced-motion")) return false;
        return !(clientAreaAnimation ?? true);
    }

    /// <summary>base::CommandLine's <c>HasSwitch</c> on Windows: <c>--</c>, <c>-</c> or <c>/</c> prefixes, case-insensitive
    /// names, <c>=value</c> ignored, nothing after a bare <c>--</c>. The first argument is the program.</summary>
    internal static bool HasSwitch(IReadOnlyList<string> args, string name)
    {
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") return false;
            var prefix = arg.StartsWith("--", StringComparison.Ordinal) ? 2 : arg.StartsWith('-') || arg.StartsWith('/') ? 1 : 0;
            if (prefix == 0) continue;
            var end = arg.IndexOf('=', prefix);
            if (arg.AsSpan(prefix, (end < 0 ? arg.Length : end) - prefix).Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Electron's <c>GetColor</c> lookup: a Windows colour name to its <c>GetSysColor</c> index, or null.</summary>
    internal static int? SysColorIndex(string name) => name switch
    {
        "3d-dark-shadow" => 21, // COLOR_3DDKSHADOW
        "3d-face" => 15, // COLOR_3DFACE
        "3d-highlight" => 20, // COLOR_3DHIGHLIGHT
        "3d-light" => 22, // COLOR_3DLIGHT
        "3d-shadow" => 16, // COLOR_3DSHADOW
        "active-border" => 10, // COLOR_ACTIVEBORDER
        "active-caption" => 2, // COLOR_ACTIVECAPTION
        "active-caption-gradient" => 27, // COLOR_GRADIENTACTIVECAPTION
        "app-workspace" => 12, // COLOR_APPWORKSPACE
        "button-text" => 18, // COLOR_BTNTEXT
        "caption-text" => 9, // COLOR_CAPTIONTEXT
        "desktop" => 1, // COLOR_DESKTOP
        "disabled-text" => 17, // COLOR_GRAYTEXT
        "highlight" => 13, // COLOR_HIGHLIGHT
        "highlight-text" => 14, // COLOR_HIGHLIGHTTEXT
        "hotlight" => 26, // COLOR_HOTLIGHT
        "inactive-border" => 11, // COLOR_INACTIVEBORDER
        "inactive-caption" => 3, // COLOR_INACTIVECAPTION
        "inactive-caption-gradient" => 28, // COLOR_GRADIENTINACTIVECAPTION
        "inactive-caption-text" => 19, // COLOR_INACTIVECAPTIONTEXT
        "info-background" => 24, // COLOR_INFOBK
        "info-text" => 23, // COLOR_INFOTEXT
        "menu" => 4, // COLOR_MENU
        "menu-highlight" => 29, // COLOR_MENUHILIGHT
        "menu-text" => 7, // COLOR_MENUTEXT
        "menubar" => 30, // COLOR_MENUBAR
        "scrollbar" => 0, // COLOR_SCROLLBAR
        "window" => 5, // COLOR_WINDOW
        "window-frame" => 6, // COLOR_WINDOWFRAME
        "window-text" => 8, // COLOR_WINDOWTEXT
        _ => null,
    };

    private static PlatformNotSupportedException NotWindows() => new("systemPreferences is only implemented on Windows so far.");
}
