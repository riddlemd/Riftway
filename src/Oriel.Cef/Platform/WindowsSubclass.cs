using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A thumbnail toolbar button for <see cref="WindowsTaskbar.SetThumbButtons"/>; <paramref name="Flags"/> are
/// Win32's <c>THBF_</c> flags and <paramref name="Mask"/> its <c>THB_</c> flags, which say which fields the shell takes.</summary>
public readonly record struct ThumbButton(uint Id, nint Icon, string? Tooltip, uint Flags, uint Mask);

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

/// <summary>Routes a window's messages that CEF does not surface to callbacks: every message to an observer, thumbnail
/// toolbar clicks (<c>WM_COMMAND</c> with <c>THBN_CLICKED</c>), <c>TaskbarCreated</c>, the user's move and resize drags
/// (<c>WM_MOVING</c>, <c>WM_SIZING</c>) and the other messages of <see cref="WindowsWindowMessages"/>;
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
    private static uint s_taskbarCreated;

    private sealed class Hooks
    {
        public bool NoActivate;
        public bool SwallowMouseLeave;
        public Func<bool>? MeantHidden;
        public Action<uint, nint, nint>? Message;
        public Func<int, bool>? ThumbClick;
        public Action? TaskbarCreated;
        public Func<int, (int Left, int Top, int Right, int Bottom), bool>? Sizing;
        public Func<(int Left, int Top, int Right, int Bottom), bool>? Moving;
        public Func<int, int, bool>? OverMaximize;
        public Action<MaximizeButtonEvent>? MaximizeEvent;
        public Func<int, int, bool>? TransparentAt;
        public bool TrackingLeave;
        public WindowsMessageHooks? Messages;
    }

    /// <summary>Routes <paramref name="hwnd"/>'s <c>PreHandleMSG</c> messages to <paramref name="messages"/> through this
    /// subclass. False if the window could not be subclassed.</summary>
    internal static bool RouteMessages(nint hwnd, WindowsMessageHooks messages)
        => Hook(hwnd) is { } hooks && (hooks.Messages = messages) is not null;
    /// <summary>Calls <paramref name="onMessage"/> with every message <paramref name="hwnd"/> receives, before anything
    /// else handles it; the message then goes on as usual. False if the window could not be subclassed.</summary>
    public static bool RouteMessages(nint hwnd, Action<uint, nint, nint> onMessage)
        => Hook(hwnd) is { } hooks && (hooks.Message = onMessage) is not null;

    /// <summary>Calls <paramref name="onClick"/> with the button id whenever a thumbnail button of
    /// <paramref name="hwnd"/> is clicked; returning true marks the click handled, false passes it on.
    /// False if the window could not be subclassed.</summary>
    public static bool RouteThumbButtonClicks(nint hwnd, Func<int, bool> onClick)
        => Hook(hwnd) is { } hooks && (hooks.ThumbClick = onClick) is not null;

    /// <summary>Calls <paramref name="onCreated"/> when Explorer broadcasts <c>TaskbarCreated</c> (the taskbar came
    /// back, losing its thumbnail toolbars), and marks the message handled. False if the window could not be subclassed.</summary>
    public static bool RouteTaskbarCreated(nint hwnd, Action onCreated)
    {
        if (Hook(hwnd) is not { } hooks) return false;
        if (s_taskbarCreated == 0)
            fixed (char* name = "TaskbarCreated") s_taskbarCreated = RegisterWindowMessageW(name);
        hooks.TaskbarCreated = onCreated;
        return true;
    }

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
                // Electron's PreHandleMSG: observers see every message first and it goes on whatever they do.
                if (hooks.Message is { } onMessage)
                {
                    try { onMessage(message, wParam, lParam); }
                    catch (Exception e) { Console.Error.WriteLine($"[Oriel] A window message hook threw: {e}"); }
                }
                if (message == s_taskbarCreated && s_taskbarCreated != 0 && hooks.TaskbarCreated is { } onTaskbarCreated)
                {
                    onTaskbarCreated();
                    return 0;
                }
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
                // Electron's SubclassProc: Chromium would take the leave as the pointer leaving the page.
                if (message == WmMouseLeave && hooks.SwallowMouseLeave) return 0;
                if (message == WmWindowPosChanging && hooks.NoActivate)
                    ((WindowPosNative*)lParam)->Flags |= SwpNoActivate;
                // Electron's HandleThumbarButtonEvent: an id it does not know goes on to Chromium.
                if (message == WmCommand && ((wParam >> 16) & 0xFFFF) == ThbnClicked && hooks.ThumbClick is { } onClick
                    && onClick((int)(wParam & 0xFFFF)))
                    return 0;
                // Electron's PreHandleMSG order: will-resize sees the user's raw rectangle, and Chromium fits it to the
                // aspect ratio only afterwards; a cancelled drag puts the current window rectangle back, handled.
                if (message == WmSizing)
                {
                    if (hooks.Messages is { } m) m.Resizing = true;
                    if (hooks.Sizing is { } onSizing && onSizing((int)wParam, Read(lParam)))
                    {
                        GetWindowRect(hwnd, (RectNative*)lParam);
                        return 1;
                    }
                    if (hooks.Messages?.Sizing is { } fit && wParam is >= 1 and <= 8)
                    {
                        var rect = (RectNative*)lParam;
                        var (l, t, r, b) = fit((int)wParam, Read(lParam));
                        *rect = new RectNative { Left = l, Top = t, Right = r, Bottom = b };
                    }
                }
                // will-move fires even for a window that is not movable, before the move is blocked.
                if (message == WmMoving)
                {
                    if (hooks.Messages is { } m) m.Moving = true;
                    var prevented = hooks.Moving is { } onMoving && onMoving(Read(lParam));
                    if (prevented || hooks.Messages is { Movable: false })
                    {
                        GetWindowRect(hwnd, (RectNative*)lParam);
                        return 1;
                    }
                }
                if (hooks.Messages is { } messages && WindowsWindowMessages.PreHandle(messages, hwnd, message, wParam, lParam) is { } handled)
                    return handled;
            }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] A window message handler threw: {e}"); }
        }
        if (message == WmNcDestroy)
        {
            s_hooks.TryRemove(hwnd, out _);
            foreach (var (top, page) in s_forwarding)
                if (top == hwnd || page == hwnd) StopForwarding(top);
            RemoveWindowSubclass(hwnd, &Proc, 1);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (hooks?.Messages is { } after)
        {
            try { WindowsWindowMessages.PostHandle(after, hwnd, message, wParam); }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] A window message handler threw: {e}"); }
        }
        return result;
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
    [DllImport("user32.dll")] private static extern int TrackMouseEvent(TrackMouseEventNative* track);
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint RegisterWindowMessageW(char* name);
    [DllImport("comctl32.dll")] private static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}