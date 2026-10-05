using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>Win32 calls behind Electron's Windows-only window state: activation style, enabled state, z-order,
/// snapping, tablet mode and window type.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsWindowOps
{
    private const int GwlExStyle = -20;
    private const uint WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    private const uint GwHwndPrev = 3;

    /// <summary>Sets or clears <c>WS_EX_NOACTIVATE</c>, as Electron's <c>SetFocusable</c>.</summary>
    public static void SetNoActivate(nint hwnd, bool noActivate) => FlipExStyle(hwnd, WsExNoActivate, noActivate);

    /// <summary>Whether the window has <c>WS_EX_NOACTIVATE</c>.</summary>
    public static bool IsNoActivate(nint hwnd) => (GetWindowLongW(hwnd, GwlExStyle) & WsExNoActivate) != 0;

    /// <summary>Adds <c>WS_EX_TOOLWINDOW</c>, Electron's <c>type: 'toolbar'</c>.</summary>
    public static void AddToolWindowStyle(nint hwnd) => FlipExStyle(hwnd, WsExToolWindow, true);

    /// <summary><c>EnableWindow</c>.</summary>
    public static void Enable(nint hwnd, bool enable) => EnableWindow(hwnd, enable ? 1 : 0);

    /// <summary><c>IsWindowEnabled</c>.</summary>
    public static bool IsEnabled(nint hwnd) => IsWindowEnabled(hwnd) != 0;

    /// <summary>Raises the window to the top of its z-order band without activating it (Electron's <c>MoveTop</c>).</summary>
    public static void MoveTop(nint hwnd)
        => SetWindowPos(hwnd, 0 /* HWND_TOP */, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);

    /// <summary>Puts the window just above <paramref name="other"/> (Electron's <c>MoveAbove</c>). False if
    /// <paramref name="other"/> is not a window.</summary>
    public static bool MoveAbove(nint hwnd, nint other)
    {
        if (IsWindow(other) == 0) return false;
        SetWindowPos(hwnd, GetWindow(other, GwHwndPrev), 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
        return true;
    }

    /// <summary>Puts the window just under the taskbar (Electron's <c>MoveBehindTaskBarIfNeeded</c>).</summary>
    public static void MoveBehindTaskbar(nint hwnd)
    {
        fixed (char* name = "Shell_TrayWnd")
        {
            var taskbar = FindWindowW(name, null);
            if (taskbar != 0) SetWindowPos(hwnd, taskbar, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    /// <summary><c>IsWindowArranged</c>, which user32 exports without a header; false where it is missing.</summary>
    public static bool IsArranged(nint hwnd)
    {
        nint user32;
        fixed (char* module = "user32.dll") user32 = GetModuleHandleW(module);
        nint function = 0;
        if (user32 != 0) fixed (byte* name = "IsWindowArranged"u8) function = GetProcAddress(user32, name);
        return function != 0 && ((delegate* unmanaged[Stdcall]<nint, int>)function)(hwnd) != 0;
    }

    /// <summary>Whether the window's view is in touch interaction mode (<c>UIViewSettings.UserInteractionMode</c>), which
    /// Chromium's <c>IsWindows10TabletMode</c> reads; false if WinRT does not answer.</summary>
    public static bool IsTabletMode(nint hwnd)
    {
        const string className = "Windows.UI.ViewManagement.UIViewSettings";
        nint name;
        fixed (char* chars = className)
            if (WindowsCreateString(chars, (uint)className.Length, &name) < 0) return false;
        try
        {
            var iidInterop = new Guid("3694DBF9-8F68-44BE-8FF5-195C98EDE8A6");
            void* interop;
            if (RoGetActivationFactory(name, &iidInterop, &interop) < 0) return false;
            try
            {
                var iidSettings = new Guid("C63657F6-8850-470D-88F8-455E16EA2C26");
                void* settings;
                // IUIViewSettingsInterop::GetForWindow, after IInspectable's six slots.
                if (((delegate* unmanaged[Stdcall]<void*, nint, Guid*, void**, int>)Slot(interop, 6))(interop, hwnd, &iidSettings, &settings) < 0) return false;
                try
                {
                    int mode = 0;
                    // IUIViewSettings::get_UserInteractionMode; 1 is UserInteractionMode_Touch.
                    return ((delegate* unmanaged[Stdcall]<void*, int*, int>)Slot(settings, 6))(settings, &mode) >= 0 && mode == 1;
                }
                finally { Release(settings); }
            }
            finally { Release(interop); }
        }
        finally { WindowsDeleteString(name); }
    }

    /// <summary>The window's outer and client sizes in pixels.</summary>
    public static ((int Left, int Top, int Right, int Bottom) Window, (int Width, int Height) Client) Rects(nint hwnd)
    {
        var window = default(Rect);
        var client = default(Rect);
        GetWindowRect(hwnd, &window);
        GetClientRect(hwnd, &client);
        return ((window.Left, window.Top, window.Right, window.Bottom), (client.Right - client.Left, client.Bottom - client.Top));
    }

    /// <summary>Moves and sizes the window in pixels without activating it or changing its z-order.</summary>
    public static void SetRect(nint hwnd, int left, int top, int right, int bottom)
        => SetWindowPos(hwnd, 0, left, top, right - left, bottom - top, SwpNoActivate | SwpNoZOrder);

    /// <summary>Ends the process at once, as Chromium's <c>TerminateCurrentProcessImmediately</c>.</summary>
    public static void TerminateNow(uint exitCode) => TerminateProcess(GetCurrentProcess(), exitCode);

    private static void FlipExStyle(nint hwnd, uint bits, bool on)
    {
        var style = GetWindowLongW(hwnd, GwlExStyle);
        SetWindowLongW(hwnd, GwlExStyle, on ? style | bits : style & ~bits);
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com) => ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(com, 2))(com);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern uint SetWindowLongW(nint hwnd, int index, uint value);
    [DllImport("user32.dll")] private static extern int EnableWindow(nint hwnd, int enable);
    [DllImport("user32.dll")] private static extern int IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern int IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern int SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint FindWindowW(char* className, char* windowName);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, Rect* rect);
    [DllImport("user32.dll")] private static extern int GetClientRect(nint hwnd, Rect* rect);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
    [DllImport("kernel32.dll")] private static extern nint GetProcAddress(nint module, byte* name);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern int TerminateProcess(nint process, uint exitCode);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* name);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint name);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, void** factory);
}
