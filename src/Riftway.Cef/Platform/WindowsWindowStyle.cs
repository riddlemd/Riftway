using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Win32 window styles behind Electron's Windows window options: opacity,
/// click-through, capture exclusion, frame styles and the restored ("normal") bounds.</summary>
/// <remarks>UI thread only. Rectangles are screen pixels.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsWindowStyle
{
    private const int GwlExStyle = -20;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint LwaAlpha = 0x2;
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Makes the window layered and sets its alpha, 0 to 255, as Electron does on Windows.</summary>
    public static bool SetAlpha(nint hwnd, byte alpha)
    {
        SetExStyle(hwnd, ExStyle(hwnd) | WsExLayered);
        return SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha) != 0;
    }

    /// <summary>The layered window's alpha, or null if the window is not layered with one.</summary>
    public static byte? GetAlpha(nint hwnd)
    {
        if ((ExStyle(hwnd) & WsExLayered) == 0) return null;
        byte alpha;
        uint flags;
        return GetLayeredWindowAttributes(hwnd, null, &alpha, &flags) != 0 && (flags & LwaAlpha) != 0 ? alpha : null;
    }

    /// <summary>Lets mouse input pass through the window to what is below, or stops it. <paramref name="keepLayered"/>
    /// keeps <c>WS_EX_LAYERED</c> when the window has an opacity.</summary>
    public static void SetClickThrough(nint hwnd, bool ignore, bool keepLayered)
    {
        var style = ExStyle(hwnd);
        style = ignore ? style | WsExTransparent | WsExLayered : style & ~(WsExTransparent | WsExLayered);
        if (keepLayered) style |= WsExLayered;
        var becameLayered = (ExStyle(hwnd) & WsExLayered) == 0 && (style & WsExLayered) != 0;
        SetExStyle(hwnd, style);
        // A layered window without attributes is not drawn at all.
        if (becameLayered) SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
    }

    /// <summary>Whether mouse input passes through the window.</summary>
    public static bool IsClickThrough(nint hwnd) => (ExStyle(hwnd) & WsExTransparent) != 0;

    /// <summary>Hides the window from screen capture and recording, or shows it again. False if Windows refused.</summary>
    public static bool SetExcludedFromCapture(nint hwnd, bool exclude)
        => SetWindowDisplayAffinity(hwnd, exclude ? WdaExcludeFromCapture : WdaNone) != 0;

    /// <summary>Whether the window is hidden from capture, from <c>GetWindowDisplayAffinity</c>.</summary>
    public static bool IsExcludedFromCapture(nint hwnd)
    {
        uint affinity;
        return GetWindowDisplayAffinity(hwnd, &affinity) != 0 && affinity != WdaNone;
    }

    /// <summary>The bounds the window has when neither maximized, minimized nor full screen, in screen pixels.</summary>
    /// <remarks>Win32 gives them in workspace coordinates, offset by a taskbar at the top or left; this undoes that.</remarks>
    public static (int X, int Y, int Width, int Height)? GetNormalBounds(nint hwnd)
    {
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        if (GetWindowPlacement(hwnd, &placement) == 0) return null;
        var r = placement.NormalPosition;
        var monitor = MonitorFromRect(&r, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        var (dx, dy) = GetMonitorInfoW(monitor, &info) != 0 ? (info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top) : (0, 0);
        return (r.Left + dx, r.Top + dy, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>The window's bounds in screen pixels.</summary>
    public static (int X, int Y, int Width, int Height) GetBounds(nint hwnd)
    {
        Rect r;
        GetWindowRect(hwnd, &r);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Whether DWM renders the window's non-client area (<c>DWMWA_NCRENDERING_ENABLED</c>).</summary>
    public static bool IsDwmNonClientRendering(nint hwnd)
    {
        int enabled;
        return DwmGetWindowAttribute(hwnd, 1 /* DWMWA_NCRENDERING_ENABLED */, &enabled, sizeof(int)) >= 0 && enabled != 0;
    }

    /// <summary>Puts the window above other non-topmost windows without activating it.</summary>
    public static void BringToTopInactive(nint hwnd)
        => SetWindowPos(hwnd, 0 /* HWND_TOP */, 0, 0, 0, 0, 0x1 /* SWP_NOSIZE */ | 0x2 /* SWP_NOMOVE */ | 0x10 /* SWP_NOACTIVATE */);

    /// <summary>The system's caption height for the window's DPI, in DIPs: caption, sizing frame and padded border, the
    /// height of Windows' own caption buttons.</summary>
    public static int CaptionHeight(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd) is var d and > 0 ? d : 96;
        var pixels = GetSystemMetricsForDpi(4 /* SM_CYCAPTION */, dpi) + GetSystemMetricsForDpi(33 /* SM_CYSIZEFRAME */, dpi)
            + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, dpi);
        return (int)Math.Round(pixels * 96.0 / dpi);
    }

    /// <summary>Whether apps use the dark theme (<c>AppsUseLightTheme</c> is 0), which Chromium's colours follow.</summary>
    public static bool AppsUseDarkTheme()
        => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    /// <summary>The system's button face and button text colours as ARGB, Electron's default overlay colours.</summary>
    public static (uint Face, uint Text) ButtonColors() => (Argb(GetSysColor(15 /* COLOR_BTNFACE */)), Argb(GetSysColor(18 /* COLOR_BTNTEXT */)));

    // COLORREF is 0x00BBGGRR.
    private static uint Argb(uint colorRef) => 0xFF000000 | ((colorRef & 0xFF) << 16) | (colorRef & 0xFF00) | ((colorRef >> 16) & 0xFF);

    /// <summary>Makes <paramref name="owner"/> the window's owner, or none: the window stays above it, minimizes with it
    /// and is not in the taskbar on its own. The top-level form of a parent; <c>GWLP_HWNDPARENT</c>, as Electron's
    /// <c>SetParentWindow</c>: an unchanged owner does nothing, and a visible window is hidden and shown again in its
    /// show state and brought to the top.</summary>
    public static void SetOwner(nint hwnd, nint owner)
    {
        if (GetWindowLongPtrW(hwnd, -8 /* GWLP_HWNDPARENT */) == owner) return;
        SetWindowLongPtrW(hwnd, -8 /* GWLP_HWNDPARENT */, owner);
        if (IsWindowVisible(hwnd) == 0) return;
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        GetWindowPlacement(hwnd, &placement);
        ShowWindow(hwnd, 0 /* SW_HIDE */);
        ShowWindow(hwnd, (int)placement.ShowCmd);
        BringWindowToTop(hwnd);
    }

    /// <summary>The window's owner, or 0.</summary>
    public static nint GetOwner(nint hwnd) => GetWindow(hwnd, 4 /* GW_OWNER */);

    /// <summary>The window's direct children of window class <paramref name="className"/>.</summary>
    public static List<nint> ChildrenOfClass(nint hwnd, string className)
    {
        var found = new List<nint>();
        var name = stackalloc char[256];
        for (var child = FindWindowExW(hwnd, 0, null, null); child != 0; child = FindWindowExW(hwnd, child, null, null))
        {
            var length = GetClassNameW(child, name, 256);
            if (new ReadOnlySpan<char>(name, length).SequenceEqual(className)) found.Add(child);
        }
        return found;
    }

    /// <summary>The top-level window that contains <paramref name="hwnd"/>.</summary>
    public static nint Root(nint hwnd) => GetAncestor(hwnd, 2 /* GA_ROOT */);

    /// <summary>The window's <c>GWL_STYLE</c>.</summary>
    public static uint GetStyle(nint hwnd) => (uint)(long)GetWindowLongPtrW(hwnd, GwlStyle);

    /// <summary>Replaces the window's <c>GWL_STYLE</c> outright, as Electron does for a frameless window before it is
    /// first shown; the frame is recomputed on the next size change.</summary>
    public static void SetStyle(nint hwnd, uint style) => SetWindowLongPtrW(hwnd, GwlStyle, (nint)style);

    /// <summary>Turns <paramref name="flag"/> on or off in the window's <c>GWL_STYLE</c> and has Windows recompute the
    /// frame; Electron's <c>FlipWindowStyle</c>.</summary>
    public static void FlipStyle(nint hwnd, bool on, uint flag)
    {
        var style = GetStyle(hwnd);
        SetStyle(hwnd, on ? style | flag : style & ~flag);
        // Windows caches the frame styles until SWP_FRAMECHANGED.
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x20 /* SWP_FRAMECHANGED */ | 0x2 /* SWP_NOMOVE */ | 0x1 /* SWP_NOSIZE */
            | 0x4 /* SWP_NOZORDER */ | 0x10 /* SWP_NOACTIVATE */ | 0x200 /* SWP_NOOWNERZORDER */);
    }

    private const int GwlStyle = -16;

    private static uint ExStyle(nint hwnd) => (uint)(long)GetWindowLongPtrW(hwnd, GwlExStyle);

    private static void SetExStyle(nint hwnd, uint style) => SetWindowLongPtrW(hwnd, GwlExStyle, (nint)style);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCmd;
        public int MinX, MinY, MaxX, MaxY;
        public Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern int SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int GetLayeredWindowAttributes(nint hwnd, uint* key, byte* alpha, uint* flags);
    [DllImport("user32.dll")] private static extern int SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowDisplayAffinity(nint hwnd, uint* affinity);
    [DllImport("user32.dll")] private static extern int GetWindowPlacement(nint hwnd, WindowPlacement* placement);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(Rect* rect, uint flags);
    [DllImport("user32.dll")] private static extern int GetMonitorInfoW(nint monitor, MonitorInfo* info);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, Rect* rect);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern int ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern int BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern nint FindWindowExW(nint parent, nint after, char* className, char* title);
    [DllImport("user32.dll")] private static extern int GetClassNameW(nint hwnd, char* name, int max);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
}
