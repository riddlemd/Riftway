using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A thumbnail toolbar button for <see cref="WindowsTaskbar.SetThumbButtons"/>; <paramref name="Flags"/> are
/// Win32's <c>THBF_</c> flags.</summary>
public readonly record struct ThumbButton(uint Id, nint Icon, string? Tooltip, uint Flags);

/// <summary>Routes a window's messages that CEF does not surface to callbacks: thumbnail toolbar clicks
/// (<c>WM_COMMAND</c> with <c>THBN_CLICKED</c>) and the user's move and resize drags (<c>WM_MOVING</c>, <c>WM_SIZING</c>);
/// and forwards mouse moves to a click-through window's page.</summary>
/// <remarks>UI thread only: a subclass belongs to the thread that owns the window.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsSubclass
{
    private const uint WmCommand = 0x0111;
    private const uint WmSizing = 0x0214;
    private const uint WmMoving = 0x0216;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmMouseMove = 0x0200;
    private const uint WmMouseLeave = 0x02A3;
    private const int WhMouseLl = 14;
    private const int ThbnClicked = 0x1800;
    private static readonly ConcurrentDictionary<nint, Hooks> s_hooks = new();
    // Top-level windows forwarding mouse moves, to their page window; Electron's forwarding_windows_.
    private static readonly ConcurrentDictionary<nint, nint> s_forwarding = new();
    private static nint s_mouseHook;

    private sealed class Hooks
    {
        public bool NoActivate;
        public bool SwallowMouseLeave;
        public Func<bool>? MeantHidden;
        public Action<int>? ThumbClick;
        public Func<int, (int Left, int Top, int Right, int Bottom), bool>? Sizing;
        public Func<(int Left, int Top, int Right, int Bottom), bool>? Moving;
    }

    /// <summary>Calls <paramref name="onClick"/> with the button id whenever a thumbnail button of
    /// <paramref name="hwnd"/> is clicked. False if the window could not be subclassed.</summary>
    public static bool RouteThumbButtonClicks(nint hwnd, Action<int> onClick)
        => Hook(hwnd) is { } hooks && (hooks.ThumbClick = onClick) is not null;

    /// <summary>Calls <paramref name="onSizing"/> with the <c>WMSZ_</c> edge and the proposed screen rectangle while the
    /// user drags a window edge; returning true keeps the window's current size. False if the window could not be subclassed.</summary>
    public static bool RouteSizing(nint hwnd, Func<int, (int Left, int Top, int Right, int Bottom), bool> onSizing)
        => Hook(hwnd) is { } hooks && (hooks.Sizing = onSizing) is not null;

    /// <summary>Calls <paramref name="onMoving"/> with the proposed screen rectangle while the user drags the window;
    /// returning true keeps it where it is. False if the window could not be subclassed.</summary>
    public static bool RouteMoving(nint hwnd, Func<(int Left, int Top, int Right, int Bottom), bool> onMoving)
        => Hook(hwnd) is { } hooks && (hooks.Moving = onMoving) is not null;

    /// <summary>Starts or stops posting the mouse moves over the top-level window <paramref name="hwnd"/> to its page
    /// window (Chromium's <c>Chrome_RenderWidgetHostHWND</c>), as Electron's <c>SetForwardMouseMessages</c> does for a
    /// window that ignores the mouse. False if the window has no page window or it could not be subclassed.</summary>
    /// <remarks>A process-wide low-level mouse hook sees the moves, installed while any window forwards; the installing
    /// thread must pump messages, as the UI thread does. The page window swallows <c>WM_MOUSELEAVE</c> meanwhile.</remarks>
    public static bool ForwardMouseMoves(nint hwnd, bool forward)
    {
        if (!forward)
        {
            StopForwarding(hwnd);
            return true;
        }
        if (s_forwarding.ContainsKey(hwnd)) return true;
        // Chromium creates its legacy page window only while it needs one; without it the top-level window takes the mouse.
        var page = PageWindow(hwnd) is var child and not 0 ? child : hwnd;
        if (Hook(page) is not { } hooks) return false;
        hooks.SwallowMouseLeave = true;
        s_forwarding[hwnd] = page;
        if (s_mouseHook == 0) s_mouseHook = SetWindowsHookExW(WhMouseLl, &MouseHookProc, 0, 0);
        return s_mouseHook != 0;
    }

    /// <summary>Whether <paramref name="hwnd"/> forwards mouse moves to its page window.</summary>
    public static bool IsForwardingMouseMoves(nint hwnd) => s_forwarding.ContainsKey(hwnd);

    private static void StopForwarding(nint hwnd)
    {
        if (!s_forwarding.TryRemove(hwnd, out var page)) return;
        if (s_hooks.TryGetValue(page, out var hooks)) hooks.SwallowMouseLeave = false;
        if (s_forwarding.IsEmpty && s_mouseHook != 0)
        {
            UnhookWindowsHookEx(s_mouseHook);
            s_mouseHook = 0;
        }
    }

    private static nint PageWindow(nint hwnd)
    {
        fixed (char* name = "Chrome_RenderWidgetHostHWND") return FindWindowExW(hwnd, 0, name, null);
    }

    // Electron's MouseHookProc: the page window ignores the mouse while click-through, so moves inside it are posted.
    [UnmanagedCallersOnly]
    private static nint MouseHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam == WmMouseMove)
        {
            var point = ((MouseHookNative*)lParam)->Point;
            foreach (var (_, page) in s_forwarding)
            {
                RectNative client;
                var p = point;
                GetClientRect(page, &client);
                ScreenToClient(page, &p);
                if (p.X >= client.Left && p.X < client.Right && p.Y >= client.Top && p.Y < client.Bottom)
                    PostMessageW(page, WmMouseMove, 0, (nint)(uint)((p.X & 0xFFFF) | ((p.Y & 0xFFFF) << 16)));
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>Runs <paramref name="action"/> and leaves the foreground where it was: a top-level window
    /// <paramref name="hwnd"/> that was not in front does not stay there. False if the window could not be subclassed.</summary>
    /// <remarks>A show is kept from activating through <c>SWP_NOACTIVATE</c>. Chromium then activates the window anyway,
    /// even disabled and under a foreground lock, so the previous foreground window is put back.</remarks>
    public static bool WithoutActivation(nint hwnd, Action action)
    {
        if (Hook(hwnd) is not { } hooks) return false;
        var before = GetForegroundWindow();
        hooks.NoActivate = true;
        try { action(); }
        finally
        {
            hooks.NoActivate = false;
            if (before != hwnd && before != 0 && GetForegroundWindow() == hwnd) SetForegroundWindow(before);
        }
        return true;
    }

    /// <summary>Hands activation back whenever <paramref name="hwnd"/> is activated while <paramref name="meantHidden"/>
    /// says it should be hidden, as CEF activates a window created hidden. False if the window could not be subclassed.</summary>
    /// <remarks>A hidden foreground window would take the keyboard from the window the user was typing in. The intent
    /// decides, not <c>IsWindowVisible</c>: a normal show activates the window before it counts as visible.</remarks>
    public static bool RefuseActivationWhileHidden(nint hwnd, Func<bool> meantHidden)
        => Hook(hwnd) is { } hooks && (hooks.MeantHidden = meantHidden) is not null;

    private static Hooks? Hook(nint hwnd)
    {
        if (s_hooks.TryGetValue(hwnd, out var hooks)) return hooks;
        if (SetWindowSubclass(hwnd, &Proc, 1, 0) == 0) return null;
        return s_hooks[hwnd] = new Hooks();
    }

    [UnmanagedCallersOnly]
    private static nint Proc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (s_hooks.TryGetValue(hwnd, out var hooks))
        {
            // An exception must not cross into the window procedure.
            try
            {
                // WM_ACTIVATE names the window losing activation; it gets it back once the activation has settled.
                if (message == WmActivate && (wParam & 0xFFFF) != 0 && lParam != 0 && hooks.MeantHidden is { } meantHidden && meantHidden())
                {
                    var previous = lParam;
                    CefThread.PostUi(() =>
                    {
                        if (GetForegroundWindow() == hwnd) SetForegroundWindow(previous);
                    });
                }
                // Electron's SubclassProc: Chromium would take the leave as the pointer leaving the page.
                if (message == WmMouseLeave && hooks.SwallowMouseLeave) return 0;
                if (message == WmWindowPosChanging && hooks.NoActivate)
                    ((WindowPosNative*)lParam)->Flags |= SwpNoActivate;
                if (message == WmCommand && ((wParam >> 16) & 0xFFFF) == ThbnClicked && hooks.ThumbClick is { } onClick)
                {
                    onClick((int)(wParam & 0xFFFF));
                    return 0;
                }
                // Electron's rule: a cancelled drag puts the current window rectangle back and reports the message handled.
                if (message == WmSizing && hooks.Sizing is { } onSizing && onSizing((int)wParam, Read(lParam)))
                {
                    GetWindowRect(hwnd, (RectNative*)lParam);
                    return 1;
                }
                if (message == WmMoving && hooks.Moving is { } onMoving && onMoving(Read(lParam)))
                {
                    GetWindowRect(hwnd, (RectNative*)lParam);
                    return 1;
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] A window message handler threw: {e}"); }
        }
        if (message == WmNcDestroy)
        {
            s_hooks.TryRemove(hwnd, out _);
            foreach (var (top, page) in s_forwarding)
                if (top == hwnd || page == hwnd) StopForwarding(top);
            RemoveWindowSubclass(hwnd, &Proc, 1);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static (int, int, int, int) Read(nint lParam)
    {
        var r = (RectNative*)lParam;
        return (r->Left, r->Top, r->Right, r->Bottom);
    }

    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmActivate = 0x0006;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosNative
    {
        public nint Hwnd;
        public nint InsertAfter;
        public int X, Y, Cx, Cy;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookNative
    {
        public PointNative Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, RectNative* rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetClientRect(nint hwnd, RectNative* rect);
    [DllImport("user32.dll")] private static extern int ScreenToClient(nint hwnd, PointNative* point);
    [DllImport("user32.dll")] private static extern int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint FindWindowExW(nint parent, nint after, char* className, char* title);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern int UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint hwnd);
    [DllImport("comctl32.dll")] private static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}