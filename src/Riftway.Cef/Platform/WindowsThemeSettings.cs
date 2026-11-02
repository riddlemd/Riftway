using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Riftway.Cef.Platform;

/// <summary>Which of Windows' theme notifications arrived: the registry keys and window messages Chromium's
/// <c>OsSettingsProviderWin</c> and <c>AccentColorObserver</c> watch.</summary>
public enum WindowsThemeChange
{
    /// <summary>Something under <c>HKCU\...\Themes\Personalize</c> changed (dark apps, transparency).</summary>
    Personalize,
    /// <summary>Something under <c>HKCU\Software\Microsoft\ColorFiltering</c> changed (the invert filter).</summary>
    ColorFiltering,
    /// <summary>Something under <c>HKCU\Software\Microsoft\Windows\DWM</c> changed (the accent colour).</summary>
    Dwm,
    /// <summary><c>WM_SETTINGCHANGE</c> with <c>SPI_SETHIGHCONTRAST</c>.</summary>
    HighContrast,
    /// <summary><c>WM_SYSCOLORCHANGE</c>.</summary>
    SysColor,
}

/// <summary>Reads Windows' theme settings and reports their changes as they happen: registry notifications
/// (<c>RegNotifyChangeKeyValue</c>, as <c>base::win::RegKey::StartWatching</c>) and the messages Chromium's
/// <c>gfx::SingletonHwnd</c> forwards.</summary>
/// <remarks>UI thread only.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsThemeSettings
{
    private const string PersonalizePath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ColorFilteringPath = @"Software\Microsoft\ColorFiltering";
    private const string DwmPath = @"SOFTWARE\Microsoft\Windows\DWM";

    private const uint WmSettingChange = 0x001A;
    private const uint WmSysColorChange = 0x0015;
    private const nint SpiSetHighContrast = 0x0043;

    private static Action<WindowsThemeChange>? s_changed;
    private static string s_prefix = "";
    private static KeyWatch? s_personalize, s_colorFiltering, s_dwm;

    /// <summary>The hidden window that receives the system's broadcasts, or 0 before <see cref="Start"/>.</summary>
    public static nint Window { get; private set; }

    /// <summary>Starts reporting changes to <paramref name="changed"/>, on the UI thread, which must pump messages.</summary>
    public static void Start(Action<WindowsThemeChange> changed)
    {
        s_changed = changed;
        Window = CreateWindow();
        OpenKeys();
    }

    /// <summary>A key path under <c>HKCU</c> that the watched keys are read below instead of <c>HKCU</c> itself, or ""
    /// for the real ones; for tests, which must not change the user's theme. Re-opens and re-watches the keys.</summary>
    public static string KeyPrefix
    {
        get => s_prefix;
        set
        {
            s_prefix = value;
            if (s_changed is not null) OpenKeys();
        }
    }

    /// <summary>Windows' <c>AppsUseLightTheme</c> and <c>EnableTransparency</c> (both 1 when unset), as
    /// <c>OsSettingsProviderWin::UpdateForThemesRegkey</c>.</summary>
    public static (bool AppsUseLight, bool Transparency) ReadPersonalize()
    {
        using var key = Open(PersonalizePath);
        return (Dword(key, "AppsUseLightTheme", 1) != 0, Dword(key, "EnableTransparency", 1) != 0);
    }

    /// <summary>Whether the colour filter is on and set to invert, as <c>UpdateForColorFilteringRegkey</c>.</summary>
    public static bool ReadInvertFilter()
    {
        using var key = Open(ColorFilteringPath);
        return Dword(key, "Active", 0) == 1 && Dword(key, "FilterType", 0) == 1;
    }

    /// <summary>DWM's <c>AccentColor</c> (a COLORREF, 0x00BBGGRR), or null when unset.</summary>
    public static uint? ReadAccentColor()
    {
        using var key = Open(DwmPath);
        return key?.GetValue("AccentColor") is int value ? (uint)value : null;
    }

    /// <summary>Whether a contrast theme is on (<c>SPI_GETHIGHCONTRAST</c>, <c>HCF_HIGHCONTRASTON</c>).</summary>
    public static bool ReadHighContrast()
    {
        var info = new HighContrastInfo { Size = (uint)sizeof(HighContrastInfo) };
        return SystemParametersInfoW(0x0042, info.Size, &info, 0) && (info.Flags & 0x1) != 0;
    }

    /// <summary><c>COLOR_WINDOW</c> and <c>COLOR_WINDOWTEXT</c> as COLORREFs (0x00BBGGRR).</summary>
    public static (uint Window, uint WindowText) ReadWindowColors() => (GetSysColor(5), GetSysColor(8));

    /// <summary><c>GetCaretBlinkTime</c>; <c>INFINITE</c> when the caret does not blink.</summary>
    public static uint ReadCaretBlinkTime() => GetCaretBlinkTime();

    private static RegistryKey? Open(string path) => Registry.CurrentUser.OpenSubKey(s_prefix + path);

    // ReadValueDW leaves the default for a missing value or one of another type.
    private static uint Dword(RegistryKey? key, string name, uint fallback) => key?.GetValue(name) is int value ? (uint)value : fallback;

    private static void OpenKeys()
    {
        s_personalize?.Dispose();
        s_colorFiltering?.Dispose();
        s_dwm?.Dispose();
        s_personalize = KeyWatch.Open(s_prefix + PersonalizePath, WindowsThemeChange.Personalize);
        s_colorFiltering = KeyWatch.Open(s_prefix + ColorFilteringPath, WindowsThemeChange.ColorFiltering);
        s_dwm = KeyWatch.Open(s_prefix + DwmPath, WindowsThemeChange.Dwm);
    }

    private static void Report(WindowsThemeChange change)
    {
        try { s_changed?.Invoke(change); }
        // An exception must not cross into the window procedure or CEF's task runner.
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Handling a theme change threw: {e}"); }
    }

    // One-shot like base::win::RegKey::Watcher: re-armed after each signal is handled.
    private sealed class KeyWatch : IDisposable
    {
        // REG_NOTIFY_CHANGE_NAME | _ATTRIBUTES | _LAST_SET | _SECURITY | REG_NOTIFY_THREAD_AGNOSTIC, as base::win::RegKey.
        private const uint Filter = 0x1 | 0x2 | 0x4 | 0x8 | 0x10000000;
        private readonly RegistryKey _key;
        private readonly WindowsThemeChange _change;
        private readonly AutoResetEvent _signal = new(false);
        private RegisteredWaitHandle? _wait;
        private bool _disposed;

        private KeyWatch(RegistryKey key, WindowsThemeChange change) => (_key, _change) = (key, change);

        public static KeyWatch? Open(string path, WindowsThemeChange change)
        {
            var key = Registry.CurrentUser.OpenSubKey(path);
            if (key is null) return null;
            var watch = new KeyWatch(key, change);
            return watch.Arm() ? watch : null;
        }

        private bool Arm()
        {
            if (RegNotifyChangeKeyValue(_key.Handle.DangerousGetHandle(), 1, Filter, _signal.SafeWaitHandle.DangerousGetHandle(), 1) != 0)
            {
                Dispose();
                return false;
            }
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
            {
                try { CefThread.PostUi(Signaled); }
                catch (InvalidOperationException) { } // CEF has shut down.
            }, null, Timeout.Infinite, executeOnlyOnce: true);
            return true;
        }

        private void Signaled()
        {
            if (_disposed) return;
            Report(_change);
            if (!_disposed) Arm();
        }

        public void Dispose()
        {
            _disposed = true;
            _wait?.Unregister(null);
            // Closing the key ends its pending notification, which signals the event once more: Signaled ignores it.
            _key.Dispose();
        }
    }

    private static nint CreateWindow()
    {
        const string ClassName = "RiftwayThemeObserver";
        fixed (char* name = ClassName)
        {
            var cls = new WndClass
            {
                Size = (uint)sizeof(WndClass),
                WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                Instance = GetModuleHandleW(null),
                ClassName = name,
            };
            RegisterClassExW(&cls);
            // A hidden top-level window, not a message-only one: only top-level windows get the system's broadcasts.
            var hwnd = CreateWindowExW(0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */, name, name,
                0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, cls.Instance, 0);
            return hwnd != 0 ? hwnd : throw new InvalidOperationException("The theme observer window could not be created.");
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmSysColorChange) Report(WindowsThemeChange.SysColor);
        else if (message == WmSettingChange && wParam == SpiSetHighContrast) Report(WindowsThemeChange.HighContrast);
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClass
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint IconSmall;
    }

    [DllImport("advapi32.dll")] private static extern int RegNotifyChangeKeyValue(nint key, int watchSubtree, uint filter, nint evt, int asynchronous);
    [DllImport("user32.dll")] private static extern ushort RegisterClassExW(WndClass* cls);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern uint GetCaretBlinkTime();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, void* data, uint winIni);
}
