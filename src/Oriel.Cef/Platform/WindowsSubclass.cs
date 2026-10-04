using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A thumbnail toolbar button for <see cref="WindowsTaskbar.SetThumbButtons"/>; <paramref name="Flags"/> are
/// Win32's <c>THBF_</c> flags.</summary>
public readonly record struct ThumbButton(uint Id, nint Icon, string? Tooltip, uint Flags);

/// <summary>What the pointer did on a drawn maximize button that answers <c>HTMAXBUTTON</c>.</summary>
public enum MaximizeButtonEvent
{
    /// <summary>The pointer moved onto or over it.</summary>
    Hover,

    /// <summary>The pointer left it.</summary>
    Leave,

    /// <summary>The left button went down on it.</summary>
    Press,

    /// <summary>The left button came up on it.</summary>
    Click,
}

/// <summary>Routes a window's messages that CEF does not surface to callbacks: thumbnail toolbar clicks
/// (<c>WM_COMMAND</c> with <c>THBN_CLICKED</c>) and the user's move and resize drags (<c>WM_MOVING</c>, <c>WM_SIZING</c>).</summary>
/// <remarks>UI thread only: a subclass belongs to the thread that owns the window.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsSubclass
{
    private const uint WmCommand = 0x0111;
    private const uint WmSizing = 0x0214;
    private const uint WmMoving = 0x0216;
    private const uint WmNcDestroy = 0x0082;
    private const int ThbnClicked = 0x1800;
    private static readonly ConcurrentDictionary<nint, Hooks> s_hooks = new();

    private sealed class Hooks
    {
        public bool NoActivate;
        public Func<bool>? MeantHidden;
        public Action<int>? ThumbClick;
        public Func<int, (int Left, int Top, int Right, int Bottom), bool>? Sizing;
        public Func<(int Left, int Top, int Right, int Bottom), bool>? Moving;
        public Func<int, int, bool>? OverMaximize;
        public Action<MaximizeButtonEvent>? MaximizeEvent;
        public Func<int, int, bool>? TransparentAt;
        public bool TrackingLeave;
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

    /// <summary>Makes the top-level window <paramref name="hwnd"/> answer <c>HTMAXBUTTON</c> where
    /// <paramref name="overMaximize"/> (screen pixels) says a drawn maximize button is, so Windows 11 offers snap layouts
    /// there, and reports the pointer and clicks on it, which then no longer reach Chromium.
    /// False if the window could not be subclassed.</summary>
    public static bool RouteMaximizeButton(nint hwnd, Func<int, int, bool> overMaximize, Action<MaximizeButtonEvent> onEvent)
    {
        if (Hook(hwnd) is not { } hooks) return false;
        hooks.OverMaximize = overMaximize;
        hooks.MaximizeEvent = onEvent;
        return true;
    }

    /// <summary>Makes the child window <paramref name="hwnd"/> answer <c>HTTRANSPARENT</c> where <paramref name="at"/>
    /// (screen pixels) says, so the hit test goes on to its parent. False if the window could not be subclassed.</summary>
    /// <remarks>Chromium's page window covers the whole client area, overlay included, and takes the mouse first.</remarks>
    public static bool PassHitTestToParent(nint hwnd, Func<int, int, bool> at)
    {
        if (Hook(hwnd) is not { } hooks) return false;
        hooks.TransparentAt = at;
        return true;
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
                if (message == WmNcHitTest && hooks.TransparentAt is { } transparentAt && transparentAt(ScreenX(lParam), ScreenY(lParam)))
                    return HtTransparent;
                if (hooks.OverMaximize is { } overMaximize && hooks.MaximizeEvent is { } maximizeEvent)
                {
                    if (message == WmNcHitTest && overMaximize(ScreenX(lParam), ScreenY(lParam))) return HtMaxButton;
                    var onButton = wParam == HtMaxButton;
                    switch (message)
                    {
                        case WmNcMouseMove when onButton:
                            if (!hooks.TrackingLeave)
                            {
                                var track = new TrackMouseEventNative { Size = (uint)sizeof(TrackMouseEventNative), Flags = TmeLeave | TmeNonClient, Hwnd = hwnd };
                                hooks.TrackingLeave = TrackMouseEvent(&track) != 0;
                            }
                            maximizeEvent(MaximizeButtonEvent.Hover);
                            return 0;
                        case WmNcMouseMove:
                        case WmNcMouseLeave:
                            hooks.TrackingLeave = false;
                            maximizeEvent(MaximizeButtonEvent.Leave);
                            break;
                        case WmNcLButtonDown or WmNcLButtonDblClk when onButton:
                            maximizeEvent(MaximizeButtonEvent.Press);
                            return 0;
                        case WmNcLButtonUp when onButton:
                            maximizeEvent(MaximizeButtonEvent.Click);
                            return 0;
                    }
                }
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
    private const uint WmNcHitTest = 0x0084, WmNcMouseMove = 0x00A0, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2;
    private const uint WmNcLButtonDblClk = 0x00A3, WmNcMouseLeave = 0x02A2;
    private const nint HtMaxButton = 9, HtTransparent = -1;
    private const uint TmeLeave = 0x2, TmeNonClient = 0x10;

    // Screen coordinates packed into lParam as signed 16-bit values.
    private static int ScreenX(nint lParam) => (short)(lParam & 0xFFFF);
    private static int ScreenY(nint lParam) => (short)((lParam >> 16) & 0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventNative
    {
        public uint Size;
        public uint Flags;
        public nint Hwnd;
        public uint HoverTime;
    }
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
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, RectNative* rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int TrackMouseEvent(TrackMouseEventNative* track);
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint hwnd);
    [DllImport("comctl32.dll")] private static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}