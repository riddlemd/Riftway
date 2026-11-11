using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>A page's own mouse on Windows. Chromium's legacy page window (<c>Chrome_RenderWidgetHostHWND</c>, one per page
/// view) receives the mouse messages over the page and hands them to the widget directly (<c>OnMouseRange</c>), past the
/// top-level window's procedure; subclassing it sees each one first.</summary>
public static unsafe class WindowsPageMouse
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmRButtonDblClk = 0x0206;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMButtonDblClk = 0x0209;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;
    private const uint WmXButtonDblClk = 0x020D;
    private const uint WmMouseLeave = 0x02A3;

    /// <summary>What a mouse message says.</summary>
    public enum Kind { Down, Up, Move, Leave }

    /// <summary>The button: a pressed or released one, or for a move the one held (left, then middle, then right, as
    /// Chromium picks); <see cref="None"/> for a move with none held or a leave.</summary>
    public enum Button { Left, Middle, Right, Back, Forward, None }

    /// <summary>A decoded mouse message: client position in pixels, the button, whether Windows marked it a double click,
    /// and the <c>MK_</c> flags (Shift 0x4, Control 0x8).</summary>
    public readonly record struct Message(Kind Kind, Button Button, int ClientX, int ClientY, bool DoubleClick, uint KeyFlags);

    /// <summary>Whether <paramref name="message"/> is one <see cref="Decode"/> reads: buttons, moves and the leave; the
    /// wheel is not, as Electron's before-mouse-event never sees it.</summary>
    public static bool IsPageMouseMessage(uint message) => message is (>= WmMouseMove and <= WmMButtonDblClk) or (>= WmXButtonDown and <= WmXButtonDblClk) or WmMouseLeave;

    /// <summary>Decodes a client mouse message; null for any other.</summary>
    public static Message? Decode(uint message, nint wParam, nint lParam)
    {
        var flags = (uint)(wParam & 0xFFFF);
        var x = (short)(lParam & 0xFFFF);
        var y = (short)((lParam >> 16) & 0xFFFF);
        var xButton = ((wParam >> 16) & 0xFFFF) == 2 ? Button.Forward : Button.Back;
        return message switch
        {
            WmMouseMove => new Message(Kind.Move, Held(flags), x, y, false, flags),
            WmLButtonDown or WmLButtonDblClk => new Message(Kind.Down, Button.Left, x, y, message == WmLButtonDblClk, flags),
            WmRButtonDown or WmRButtonDblClk => new Message(Kind.Down, Button.Right, x, y, message == WmRButtonDblClk, flags),
            WmMButtonDown or WmMButtonDblClk => new Message(Kind.Down, Button.Middle, x, y, message == WmMButtonDblClk, flags),
            WmXButtonDown or WmXButtonDblClk => new Message(Kind.Down, xButton, x, y, message == WmXButtonDblClk, flags),
            WmLButtonUp => new Message(Kind.Up, Button.Left, x, y, false, flags),
            WmRButtonUp => new Message(Kind.Up, Button.Right, x, y, false, flags),
            WmMButtonUp => new Message(Kind.Up, Button.Middle, x, y, false, flags),
            WmXButtonUp => new Message(Kind.Up, xButton, x, y, false, flags),
            WmMouseLeave => new Message(Kind.Leave, Button.None, 0, 0, false, 0),
            _ => null,
        };
    }

    // ui::EventFlags to WebMouseEvent::button: left, then middle, then right.
    private static Button Held(uint flags)
        => (flags & 0x1) != 0 ? Button.Left : (flags & 0x10) != 0 ? Button.Middle : (flags & 0x2) != 0 ? Button.Right : Button.None;

    /// <summary>The page windows of top-level window <paramref name="hwnd"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static List<nint> PageWindows(nint hwnd) => WindowsWindowStyle.ChildrenOfClass(hwnd, "Chrome_RenderWidgetHostHWND");

    /// <summary>The window's rectangle on the screen, in pixels, or null when it is gone.</summary>
    public static (int Left, int Top, int Right, int Bottom)? ScreenRect(nint hwnd)
    {
        Rect r;
        return GetWindowRect(hwnd, &r) != 0 ? (r.Left, r.Top, r.Right, r.Bottom) : null;
    }

    /// <summary>A client point of <paramref name="hwnd"/> on the screen, in pixels.</summary>
    public static (int X, int Y) ClientToScreen(nint hwnd, int x, int y)
    {
        var p = new Point { X = x, Y = y };
        _ = ClientToScreenNative(hwnd, &p);
        return (p.X, p.Y);
    }

    /// <summary>The window's scale: its DPI over 96.</summary>
    public static double Scale(nint hwnd) => GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;

    /// <summary>Whether Alt is down now (mouse messages carry no Alt flag).</summary>
    public static bool AltDown() => (GetKeyState(0x12) & 0x8000) != 0;

    /// <summary>Windows' double-click time and box, which Chromium's click count follows.</summary>
    public static (int TimeMs, int Width, int Height) DoubleClickRules() => ((int)GetDoubleClickTime(), GetSystemMetrics(36), GetSystemMetrics(37));

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, Rect* rect);
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")] private static extern int ClientToScreenNative(nint hwnd, Point* point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
