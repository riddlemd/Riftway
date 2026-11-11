using System.Runtime.Versioning;
using Riftway.Cef;
using Riftway.Cef.Interop;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>The user's own mouse for <see cref="WebContents.BeforeMouseEvent"/> on Windows: each page window of the page's
/// top-level window is subclassed, and a message goes to the page whose view the page window covers.</summary>
public sealed unsafe partial class WebContents
{
    private readonly ClickCounter _clicks = new();
    private (int X, int Y)? _lastMove;

    // UI thread. Routes the mouse of every page window of this page's top-level window; repeated after navigations.
    [SupportedOSPlatform("windows")]
    private void HookUserMouse()
    {
        if (_beforeMouseEvent is null || BaseWindow.HostOf(this) is not { IsDestroyed: false } window) return;
        var top = window.GetNativeWindowHandle();
        if (top == 0) return;
        foreach (var pageWindow in WindowsPageMouse.PageWindows(top))
        {
            var hwnd = pageWindow;
            WindowsSubclass.RouteMouse(hwnd, (message, wParam, lParam) => RouteUserMouse(window, hwnd, message, wParam, lParam));
        }
    }

    // UI thread. True to swallow the message: a BeforeMouseEvent listener of the page under it cancelled it.
    [SupportedOSPlatform("windows")]
    private static bool RouteUserMouse(BaseWindow window, nint pageWindow, uint message, nint wParam, nint lParam)
    {
        if (window.IsDestroyed || WindowsPageMouse.Decode(message, wParam, lParam) is not { } decoded) return false;
        if (WindowsPageMouse.ScreenRect(pageWindow) is not { } rect) return false;
        foreach (var page in window.HostedPages())
            if (page._beforeMouseEvent is not null && page.Covers(rect))
                return page.OnBeforeMouseEvent(page.FromUserMouse(pageWindow, decoded));
        return false;
    }

    // UI thread. Whether this page's view sits where the page window does: their centres within a pixel or two.
    private bool Covers((int Left, int Top, int Right, int Bottom) rect)
    {
        if (_browser is null || BaseWindow.HostOf(this) is not { } window) return false;
        NativeObject.AddRef(_browser);
        var view = CefNative.cef_browser_view_get_for_browser(_browser);
        if (view is null) return false;
        var dip = view->@base.get_bounds_in_screen(&view->@base);
        NativeObject.Release(view);
        var px = Screen.DipToScreenRect(window, new Rectangle(dip.x, dip.y, dip.width, dip.height));
        var cx = (rect.Left + rect.Right) / 2.0 - (px.X + px.Width / 2.0);
        var cy = (rect.Top + rect.Bottom) / 2.0 - (px.Y + px.Height / 2.0);
        return Math.Abs(cx) <= 2 && Math.Abs(cy) <= 2;
    }

    // UI thread. Electron's MouseInputEvent from Chromium's WebMouseEvent for the message: the position in the page in
    // DIPs, on the screen in DIPs, the button, the click count (0 for moves) and the movement since the last move.
    [SupportedOSPlatform("windows")]
    private MouseInputEvent FromUserMouse(nint pageWindow, WindowsPageMouse.Message m)
    {
        var scale = WindowsPageMouse.Scale(pageWindow);
        var x = (int)Math.Floor(m.ClientX / scale);
        var y = (int)Math.Floor(m.ClientY / scale);
        var (sx, sy) = WindowsPageMouse.ClientToScreen(pageWindow, m.ClientX, m.ClientY);
        var global = Screen.ScreenToDipPoint(new Point(sx, sy));
        var type = m.Kind switch
        {
            WindowsPageMouse.Kind.Down => MouseInputEventType.MouseDown,
            WindowsPageMouse.Kind.Up => MouseInputEventType.MouseUp,
            WindowsPageMouse.Kind.Leave => MouseInputEventType.MouseLeave,
            _ => MouseInputEventType.MouseMove,
        };
        var button = m.Button switch
        {
            WindowsPageMouse.Button.Left => MouseButton.Left,
            WindowsPageMouse.Button.Middle => MouseButton.Middle,
            WindowsPageMouse.Button.Right => MouseButton.Right,
            WindowsPageMouse.Button.Back => MouseButton.Back,
            WindowsPageMouse.Button.Forward => MouseButton.Forward,
            _ => MouseButton.None,
        };
        var modifiers = ((m.KeyFlags & 0x4) != 0 ? InputModifiers.Shift : 0) | ((m.KeyFlags & 0x8) != 0 ? InputModifiers.Control : 0)
            | (WindowsPageMouse.AltDown() ? InputModifiers.Alt : 0);
        var (movementX, movementY) = (0, 0);
        if (type == MouseInputEventType.MouseMove)
        {
            if (_lastMove is var (lx, ly)) (movementX, movementY) = (x - lx, y - ly);
            _lastMove = (x, y);
        }
        else if (type == MouseInputEventType.MouseLeave) _lastMove = null;
        var rules = WindowsPageMouse.DoubleClickRules();
        var clicks = type switch
        {
            MouseInputEventType.MouseDown => _clicks.Down(button, m.ClientX, m.ClientY, Environment.TickCount64, rules.TimeMs, rules.Width, rules.Height),
            MouseInputEventType.MouseUp => _clicks.Up(button),
            _ => 0,
        };
        if (type == MouseInputEventType.MouseLeave) { x = y = 0; global = default; }
        return new MouseInputEvent(type, x, y)
        {
            Button = button, ClickCount = clicks, Modifiers = modifiers,
            GlobalX = global.X, GlobalY = global.Y, MovementX = movementX, MovementY = movementY,
        };
    }
}

/// <summary>Chromium's click count (<c>ui::MouseEvent::GetRepeatCount</c>): a press of the same button within the double-click
/// time and half the double-click box of the last press counts on from it; a release carries its press's count.</summary>
internal sealed class ClickCounter
{
    private MouseButton _button;
    private long _time;
    private int _x, _y, _count;

    internal int Down(MouseButton button, int x, int y, long timeMs, int doubleClickMs, int boxWidth, int boxHeight)
    {
        var repeat = _count > 0 && button == _button && timeMs - _time <= doubleClickMs
            && Math.Abs(x - _x) <= boxWidth / 2 && Math.Abs(y - _y) <= boxHeight / 2;
        _count = repeat ? _count + 1 : 1;
        (_button, _time, _x, _y) = (button, timeMs, x, y);
        return _count;
    }

    internal int Up(MouseButton button) => button == _button && _count > 0 ? _count : 1;
}
