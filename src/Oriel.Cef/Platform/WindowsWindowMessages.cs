using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>What one window does with the messages Electron's <c>NativeWindowViews::PreHandleMSG</c> handles and CEF
/// does not: user moves (<c>movable</c>), sizing to an aspect ratio, the end of a move or resize, session end, the
/// system context menu and app commands. Fields are read on the window's thread.</summary>
public sealed class WindowsMessageHooks
{
    /// <summary>False blocks the user's moves: <c>WM_MOVING</c> keeps the window where it is.</summary>
    public bool Movable { get; set; } = true;

    /// <summary>Resizes the dragged rectangle (<c>WM_SIZING</c>), given the <c>WMSZ_*</c> edge and the rectangle as
    /// left, top, right, bottom in pixels; null leaves it.</summary>
    public Func<int, (int Left, int Top, int Right, int Bottom), (int Left, int Top, int Right, int Bottom)>? Sizing { get; set; }

    /// <summary>Puts the window just under the taskbar whenever it is activated.</summary>
    public bool BehindTaskbar { get; set; }

    /// <summary>Disables the window again whenever something re-enables it (<c>WM_ENABLE</c>), as after a modal child closes.</summary>
    public bool KeepDisabled { get; set; }

    /// <summary>A user move ended (<c>WM_EXITSIZEMOVE</c> after <c>WM_MOVING</c>).</summary>
    public Action? MoveFinished { get; set; }

    /// <summary>A user resize ended (<c>WM_EXITSIZEMOVE</c> after <c>WM_SIZING</c>).</summary>
    public Action? ResizeFinished { get; set; }

    /// <summary><c>WM_QUERYENDSESSION</c> with its lParam; true refuses the end of the session.</summary>
    public Func<nint, bool>? QueryEndSession { get; set; }

    /// <summary><c>WM_CONTEXTMENU</c> with its screen point in pixels; true keeps the system menu from showing.</summary>
    public Func<int, int, bool>? SystemContextMenu { get; set; }

    /// <summary><c>WM_APPCOMMAND</c> with its lParam.</summary>
    public Action<nint>? AppCommand { get; set; }

    internal bool Moving;
    internal bool Resizing;
}

/// <summary>Routes a window's messages to its <see cref="WindowsMessageHooks"/> through a subclass of its own, so other
/// subclasses of the window keep working.</summary>
/// <remarks>UI thread only: a subclass belongs to the thread that owns the window.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsWindowMessages
{
    private const nuint SubclassId = 0x4F57;
    private const uint WmActivate = 0x0006, WmEnable = 0x000A, WmQueryEndSession = 0x0011, WmEndSession = 0x0016;
    private const uint WmContextMenu = 0x007B, WmNcDestroy = 0x0082;
    private const uint WmSizing = 0x0214, WmMoving = 0x0216, WmExitSizeMove = 0x0232, WmAppCommand = 0x0319;
    private static readonly ConcurrentDictionary<nint, WindowsMessageHooks> s_hooks = new();
    private static int s_sessionEnded;

    /// <summary>Called once per process, with the lParam, for the first <c>WM_ENDSESSION</c> that ends the session.</summary>
    public static Action<nint>? SessionEnded { get; set; }

    /// <summary>Starts routing <paramref name="hwnd"/>'s messages to <paramref name="hooks"/>. False if the window could
    /// not be subclassed.</summary>
    public static bool Attach(nint hwnd, WindowsMessageHooks hooks)
    {
        if (s_hooks.ContainsKey(hwnd))
        {
            s_hooks[hwnd] = hooks;
            return true;
        }
        if (SetWindowSubclass(hwnd, &Proc, SubclassId, 0) == 0) return false;
        s_hooks[hwnd] = hooks;
        return true;
    }

    /// <summary>Sets the window's region to the union of <paramref name="rects"/> (pixels from the window's top left), or
    /// removes it for none, unless it is already that region.</summary>
    public static void ApplyRegion(nint hwnd, (int Left, int Top, int Right, int Bottom)[]? rects)
    {
        if (rects is null || rects.Length == 0)
        {
            SetWindowRgn(hwnd, 0, 1);
            return;
        }
        var region = CreateRectRgn(0, 0, 0, 0);
        foreach (var (left, top, right, bottom) in rects)
        {
            var part = CreateRectRgn(left, top, right, bottom);
            CombineRgn(region, region, part, 2 /* RGN_OR */);
            DeleteObject(part);
        }
        var current = CreateRectRgn(0, 0, 0, 0);
        var same = GetWindowRgn(hwnd, current) is not 0 /* ERROR */ and not 1 /* NULLREGION */ && EqualRgn(current, region) != 0;
        DeleteObject(current);
        // SetWindowRgn owns the region it is given.
        if (same) DeleteObject(region);
        else SetWindowRgn(hwnd, region, 1);
    }

    [UnmanagedCallersOnly]
    private static nint Proc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (!s_hooks.TryGetValue(hwnd, out var hooks)) return DefSubclassProc(hwnd, message, wParam, lParam);
        try
        {
            switch (message)
            {
                case WmSizing:
                    hooks.Resizing = true;
                    if (hooks.Sizing is { } sizing && wParam is >= 1 and <= 8)
                    {
                        var rect = (Rect*)lParam;
                        var (l, t, r, b) = sizing((int)wParam, (rect->Left, rect->Top, rect->Right, rect->Bottom));
                        *rect = new Rect { Left = l, Top = t, Right = r, Bottom = b };
                    }
                    break;
                case WmMoving:
                    hooks.Moving = true;
                    if (!hooks.Movable)
                    {
                        GetWindowRect(hwnd, (Rect*)lParam);
                        return 1;
                    }
                    break;
                case WmExitSizeMove:
                    if (hooks.Resizing)
                    {
                        hooks.Resizing = false;
                        hooks.ResizeFinished?.Invoke();
                    }
                    if (hooks.Moving)
                    {
                        hooks.Moving = false;
                        hooks.MoveFinished?.Invoke();
                    }
                    break;
                case WmQueryEndSession when hooks.QueryEndSession?.Invoke(lParam) == true:
                    return 0;
                case WmEndSession when wParam != 0 && Interlocked.Exchange(ref s_sessionEnded, 1) == 0:
                    SessionEnded?.Invoke(lParam);
                    break;
                case WmContextMenu when hooks.SystemContextMenu?.Invoke((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)) == true:
                    return 0;
                case WmAppCommand:
                    hooks.AppCommand?.Invoke(lParam);
                    break;
                case WmNcDestroy:
                    s_hooks.TryRemove(hwnd, out _);
                    RemoveWindowSubclass(hwnd, &Proc, SubclassId);
                    return DefSubclassProc(hwnd, message, wParam, lParam);
            }
        }
        // An exception must not cross into the window procedure.
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A window message handler threw: {e}"); }

        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == WmActivate && (wParam & 0xFFFF) != 0 && hooks.BehindTaskbar) WindowsWindowOps.MoveBehindTaskbar(hwnd);
        if (message == WmEnable && wParam != 0 && hooks.KeepDisabled) WindowsWindowOps.Enable(hwnd, false);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("comctl32.dll")] private static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, Rect* rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, int redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("gdi32.dll")] private static extern int EqualRgn(nint region1, nint region2);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint handle);
}
