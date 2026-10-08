using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A click or move on a tray icon. Electron's <c>event</c>, <c>bounds</c> and <c>position</c> arguments.</summary>
public sealed class TrayEventArgs(Rectangle bounds, Point position, bool shift, bool ctrl, bool alt, bool meta) : EventArgs
{
    /// <summary>The icon's bounds on screen in DIPs; empty when the shell cannot place it, and for the pointer events
    /// (move, enter, leave), which Electron gives no bounds. Electron's <c>bounds</c>.</summary>
    public Rectangle Bounds { get; } = bounds;

    /// <summary>Where the pointer was, in screen DIPs; empty for the double, middle and right clicks, which Electron gives
    /// no position. Electron's <c>position</c>.</summary>
    public Point Position { get; } = position;

    /// <summary>Electron's <c>event.shiftKey</c>.</summary>
    public bool ShiftKey { get; } = shift;

    /// <summary>Electron's <c>event.ctrlKey</c>.</summary>
    public bool CtrlKey { get; } = ctrl;

    /// <summary>Electron's <c>event.altKey</c>.</summary>
    public bool AltKey { get; } = alt;

    /// <summary>Electron's <c>event.metaKey</c>: the Windows key.</summary>
    public bool MetaKey { get; } = meta;
}

/// <summary>The icon a balloon shows. Electron's <c>iconType</c>.</summary>
public enum BalloonIconType
{
    /// <summary>Electron's <c>none</c>.</summary>
    None,

    /// <summary>Electron's <c>info</c>.</summary>
    Info,

    /// <summary>Electron's <c>warning</c>.</summary>
    Warning,

    /// <summary>Electron's <c>error</c>.</summary>
    Error,

    /// <summary><see cref="DisplayBalloonOptions.Icon"/>. Electron's <c>custom</c>.</summary>
    Custom,
}

/// <summary>A balloon notification from the tray icon; Electron's <c>options</c> for <c>tray.displayBalloon()</c>.</summary>
public sealed record DisplayBalloonOptions
{
    /// <summary>The icon for <see cref="BalloonIconType.Custom"/>. Electron's <c>icon</c>.</summary>
    public NativeImage? Icon { get; init; }

    /// <summary>Electron's <c>iconType</c>; <see cref="BalloonIconType.Custom"/> when only <see cref="Icon"/> is given.</summary>
    public BalloonIconType? IconType { get; init; }

    /// <summary>Electron's <c>title</c>.</summary>
    public string Title { get; init; } = "";

    /// <summary>Electron's <c>content</c>.</summary>
    public string Content { get; init; } = "";

    /// <summary>Use the large version of the icon. Electron's <c>largeIcon</c>.</summary>
    public bool LargeIcon { get; init; } = true;

    /// <summary>Electron's <c>noSound</c>.</summary>
    public bool NoSound { get; init; }

    /// <summary>Do not show it while the user is busy (full screen, presenting). Electron's <c>respectQuietTime</c>.</summary>
    public bool RespectQuietTime { get; init; }
}

/// <summary>An icon in the notification area (Windows), menu bar (macOS) or system tray (Linux). Electron's <c>Tray</c>.</summary>
/// <remarks>Windows only for now: macOS and Linux throw <see cref="PlatformNotSupportedException"/>. Create it once the app
/// is ready; its events are raised on the UI thread.</remarks>
public sealed class Tray
{
    private readonly ITrayBackend _backend;
    private readonly TrayMenu _menu = new();
    private Menu? _contextMenu;
    private string _title = "";
    private bool _ignoreDoubleClick;
    private bool _destroyed;

    /// <summary>Puts <paramref name="image"/> in the tray; <paramref name="guid"/> gives it an identity that lasts across
    /// runs (Windows ties it to the executable's path). Electron's <c>new Tray(image, guid)</c>.</summary>
    public Tray(NativeImage image, Guid? guid = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        _backend = OperatingSystem.IsWindows() ? new WindowsTrayBackend(this) : new UnsupportedTrayBackend();
        CefThread.InvokeOnUi(() =>
        {
            _backend.Create(image, guid);
            return true;
        });
    }

    /// <summary>Puts a PNG or JPEG file in the tray. Electron's <c>new Tray(path, guid)</c>.</summary>
    public Tray(string imagePath, Guid? guid = null) : this(NativeImage.CreateFromBuffer(File.ReadAllBytes(imagePath)), guid) { }

    /// <summary>A left click. Electron's <c>'click'</c>.</summary>
    public event EventHandler<TrayEventArgs>? Clicked;

    /// <summary>A right click when there is no context menu. Electron's <c>'right-click'</c>.</summary>
    public event EventHandler<TrayEventArgs>? RightClicked;

    /// <summary>A double click. Electron's <c>'double-click'</c>.</summary>
    public event EventHandler<TrayEventArgs>? DoubleClicked;

    /// <summary>A middle click. Electron's <c>'middle-click'</c>.</summary>
    public event EventHandler<TrayEventArgs>? MiddleClicked;

    /// <summary>The pointer moved over the icon. Electron's <c>'mouse-move'</c>.</summary>
    public event EventHandler<TrayEventArgs>? MouseMoved;

    /// <summary>The pointer came onto the icon. Electron's <c>'mouse-enter'</c>.</summary>
    public event EventHandler<TrayEventArgs>? MouseEntered;

    /// <summary>The pointer left the icon; noticed within a quarter of a second. Electron's <c>'mouse-leave'</c>.</summary>
    public event EventHandler<TrayEventArgs>? MouseLeft;

    /// <summary>A balloon appeared. Electron's <c>'balloon-show'</c>.</summary>
    public event EventHandler? BalloonShown;

    /// <summary>The user clicked a balloon. Electron's <c>'balloon-click'</c>.</summary>
    public event EventHandler? BalloonClicked;

    /// <summary>A balloon timed out. Electron's <c>'balloon-closed'</c>.</summary>
    public event EventHandler? BalloonClosed;

    /// <summary>Removes the icon and closes its menu. Electron's <c>tray.destroy()</c>.</summary>
    public void Destroy() => OnUi(() =>
    {
        if (_destroyed) return;
        _destroyed = true;
        _menu.Close();
        _backend.Destroy();
    });

    /// <summary>Whether <see cref="Destroy"/> was called. Electron's <c>tray.isDestroyed()</c>.</summary>
    public bool IsDestroyed() => CefThread.InvokeOnUi(() => _destroyed);

    /// <summary>Shows another image. Electron's <c>tray.setImage()</c>.</summary>
    public void SetImage(NativeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Live(() => _backend.SetImage(image));
    }

    /// <summary>macOS: the image shown while the icon is pressed. Stored only elsewhere. Electron's <c>tray.setPressedImage()</c>.</summary>
    public void SetPressedImage(NativeImage image) => ArgumentNullException.ThrowIfNull(image);

    /// <summary>Sets the text shown on hover; Windows shows at most 127 characters. Electron's <c>tray.setToolTip()</c>.</summary>
    public void SetToolTip(string toolTip)
    {
        ArgumentNullException.ThrowIfNull(toolTip);
        Live(() => _backend.SetToolTip(toolTip));
    }

    /// <summary>macOS: text beside the icon; ignored elsewhere, as in Electron. Electron's <c>tray.setTitle()</c>.</summary>
    public void SetTitle(string title) => Live(() => { if (OperatingSystem.IsMacOS()) _title = title ?? ""; });

    /// <summary>The title as set; always empty off macOS. Electron's <c>tray.getTitle()</c>.</summary>
    public string GetTitle() => CefThread.InvokeOnUi(() => _title);

    /// <summary>macOS: report two clicks rather than a double click; ignored elsewhere, as in Electron.
    /// Electron's <c>tray.setIgnoreDoubleClickEvents()</c>.</summary>
    public void SetIgnoreDoubleClickEvents(bool ignore) => Live(() => { if (OperatingSystem.IsMacOS()) _ignoreDoubleClick = ignore; });

    /// <summary>The value set by <see cref="SetIgnoreDoubleClickEvents"/>; always false off macOS.
    /// Electron's <c>tray.getIgnoreDoubleClickEvents()</c>.</summary>
    public bool GetIgnoreDoubleClickEvents() => CefThread.InvokeOnUi(() => _ignoreDoubleClick);

    /// <summary>Shows a balloon notification (a toast on Windows 10 and 11). Electron's <c>tray.displayBalloon()</c>.</summary>
    public void DisplayBalloon(DisplayBalloonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Live(() => _backend.DisplayBalloon(options));
    }

    /// <summary>Takes the balloon away. Electron's <c>tray.removeBalloon()</c>.</summary>
    public void RemoveBalloon() => Live(_backend.RemoveBalloon);

    /// <summary>Gives the icon keyboard focus. Electron's <c>tray.focus()</c>.</summary>
    public void Focus() => Live(_backend.Focus);

    /// <summary>Sets the menu a right click opens, or none. Electron's <c>tray.setContextMenu()</c>.</summary>
    public void SetContextMenu(Menu? menu) => OnUi(() => _contextMenu = menu);

    /// <summary>Opens <paramref name="menu"/>, or the context menu, at <paramref name="position"/> (screen DIPs) or, when
    /// it is null or (0, 0), the pointer. Returns at once; the menu stays open until chosen from or dismissed.
    /// Electron's <c>tray.popUpContextMenu()</c>.</summary>
    public void PopUpContextMenu(Menu? menu = null, Point? position = null)
        => Live(() => { if ((menu ?? _contextMenu) is { } m) PopUp(m, position); });

    /// <summary>Closes the menu the icon opened, if any. Electron's <c>tray.closeContextMenu()</c>.</summary>
    public void CloseContextMenu() => Live(_menu.Close);

    /// <summary>The icon's bounds on screen in DIPs; empty when the shell cannot place it (on Windows 11, while it sits in
    /// the overflow). Electron's <c>tray.getBounds()</c>.</summary>
    public Rectangle GetBounds() => CefThread.InvokeOnUi(() => _destroyed ? default : _backend.GetBounds());

    // Electron's NotifyIcon::PopUpContextMenu: no menu without the foreground, and (0, 0) means the pointer.
    private void PopUp(Menu menu, Point? position)
    {
        if (!_backend.ActivateForMenu()) return;
        _menu.Show(menu, position is { } p && p != default ? p : Screen.GetCursorScreenPoint());
    }

    /// <summary>A shell event for this icon, posted to the UI thread with the modifier keys held when it arrived.</summary>
    internal void Raise(TrayEvent e, (bool Shift, bool Ctrl, bool Alt, bool Meta) keys)
    {
        if (_destroyed) return;
        switch (e)
        {
            case TrayEvent.BalloonShow: BalloonShown?.Invoke(this, EventArgs.Empty); return;
            case TrayEvent.BalloonClick: BalloonClicked?.Invoke(this, EventArgs.Empty); return;
            case TrayEvent.BalloonClosed: BalloonClosed?.Invoke(this, EventArgs.Empty); return;
            // Electron's rule: a context menu takes the right click; without one, the event does.
            case TrayEvent.RightClick when _contextMenu is { } menu: PopUp(menu, null); return;
        }
        if (TrayRules.ArgsOf(e, _backend.GetBounds(), Screen.GetCursorScreenPoint()) is not var (bounds, position)) return;
        var handler = e switch
        {
            TrayEvent.Click => Clicked,
            TrayEvent.DoubleClick => DoubleClicked,
            TrayEvent.MiddleClick => MiddleClicked,
            TrayEvent.RightClick => RightClicked,
            TrayEvent.MouseMove => MouseMoved,
            TrayEvent.MouseEnter => MouseEntered,
            TrayEvent.MouseLeave => MouseLeft,
            _ => null,
        };
        handler?.Invoke(this, new TrayEventArgs(bounds, position, keys.Shift, keys.Ctrl, keys.Alt, keys.Meta));
    }

    private void Live(Action action) => OnUi(() =>
    {
        if (!_destroyed) action();
    });

    private static void OnUi(Action action) => CefThread.InvokeOnUi(() =>
    {
        action();
        return true;
    });
}

/// <summary>What happened to a tray icon, OS-neutral.</summary>
internal enum TrayEvent
{
    None,
    Click,
    DoubleClick,
    MiddleClick,
    RightClick,
    MouseMove,
    MouseEnter,
    MouseLeave,
    BalloonShow,
    BalloonClick,
    BalloonClosed,
}

internal static class TrayRules
{
    /// <summary>A notification icon callback, version 0, as Electron's <c>NotifyIconHost::WndProc</c> reads it: the button
    /// press is the click, a right press or <c>WM_CONTEXTMENU</c> the right click, a right double click nothing;
    /// <c>NIN_BALLOONHIDE</c> and <c>NIN_KEYSELECT</c> go unhandled.</summary>
    internal static TrayEvent EventOf(uint message) => message switch
    {
        0x0201 => TrayEvent.Click,         // WM_LBUTTONDOWN
        0x0203 => TrayEvent.DoubleClick,   // WM_LBUTTONDBLCLK
        0x0207 => TrayEvent.MiddleClick,   // WM_MBUTTONDOWN
        0x0209 => TrayEvent.MiddleClick,   // WM_MBUTTONDBLCLK
        0x0204 => TrayEvent.RightClick,    // WM_RBUTTONDOWN
        0x007B => TrayEvent.RightClick,    // WM_CONTEXTMENU
        0x0200 => TrayEvent.MouseMove,     // WM_MOUSEMOVE
        0x0402 => TrayEvent.BalloonShow,   // NIN_BALLOONSHOW
        0x0405 => TrayEvent.BalloonClick,  // NIN_BALLOONUSERCLICK
        0x0404 => TrayEvent.BalloonClosed, // NIN_BALLOONTIMEOUT
        _ => TrayEvent.None,
    };

    /// <summary>The bounds and position Electron gives a pointer event (the other one empty), or null when it raises none:
    /// a move reported while the pointer is outside the icon's bounds.</summary>
    internal static (Rectangle Bounds, Point Position)? ArgsOf(TrayEvent e, Rectangle bounds, Point cursor) => e switch
    {
        TrayEvent.Click => (bounds, cursor),
        TrayEvent.DoubleClick or TrayEvent.MiddleClick or TrayEvent.RightClick => (bounds, default(Point)),
        TrayEvent.MouseMove => Contains(bounds, cursor) ? (default(Rectangle), cursor) : null,
        TrayEvent.MouseEnter or TrayEvent.MouseLeave => (default(Rectangle), cursor),
        _ => null,
    };

    /// <summary>Whether <paramref name="point"/> is inside <paramref name="rect"/>, right and bottom edges excluded, as
    /// <c>gfx::Rect::Contains</c>.</summary>
    internal static bool Contains(Rectangle rect, Point point)
        => point.X >= rect.X && point.X < rect.X + rect.Width && point.Y >= rect.Y && point.Y < rect.Y + rect.Height;

    /// <summary>The <c>NIIF_</c> flags for a balloon: the icon type (a given icon means custom), large icon, no sound,
    /// quiet time.</summary>
    internal static uint BalloonFlags(DisplayBalloonOptions o)
    {
        var type = o.IconType ?? (o.Icon is not null ? BalloonIconType.Custom : BalloonIconType.None);
        uint flags = type switch
        {
            BalloonIconType.Info => 1,
            BalloonIconType.Warning => 2,
            BalloonIconType.Error => 3,
            BalloonIconType.Custom => 4,
            _ => 0,
        };
        if (o.LargeIcon) flags |= 0x20;
        if (o.NoSound) flags |= 0x10;
        if (o.RespectQuietTime) flags |= 0x80;
        return flags;
    }
}

/// <summary>An icon the <see cref="TrayHoverDetector"/> watches.</summary>
internal interface ITrayHoverTarget
{
    uint Id { get; }

    /// <summary>The icon's bounds in screen DIPs.</summary>
    Rectangle GetBounds();

    /// <summary>Raise <c>mouse-enter</c>.</summary>
    void Entered();

    /// <summary>Raise <c>mouse-leave</c>.</summary>
    void Exited();
}

/// <summary>Electron's <c>MouseEnteredExitedDetector</c> (notify_icon_host.cc): the shell reports only moves over an icon,
/// so the first move inside its bounds enters it, and leaving is found by checking the pointer every 250 ms.</summary>
internal sealed class TrayHoverDetector(Func<Point> cursor, Action<Action> startTimer, Action stopTimer)
{
    /// <summary>Electron's <c>kMouseLeaveCheckFrequency</c>.</summary>
    internal const uint CheckMilliseconds = 250;

    private ITrayHoverTarget? _entered;
    private bool _timerRunning;

    /// <summary>The shell reported a move over <paramref name="icon"/>.</summary>
    public void MouseMove(ITrayHoverTarget icon)
    {
        if (!IsOver(icon)) return;
        if (_entered is { } current && current.Id != icon.Id) SendExited();
        // A running timer means the pointer is already over the icon.
        if (_timerRunning) return;
        icon.Entered();
        _entered = icon;
        _timerRunning = true;
        startTimer(Check);
    }

    /// <summary>The icon is going away.</summary>
    public void IconRemoved(ITrayHoverTarget icon)
    {
        if (_entered is { } current && current.Id == icon.Id) SendExited();
    }

    private void Check()
    {
        if (_entered is null || IsOver(_entered)) return;
        SendExited();
    }

    private void SendExited()
    {
        _timerRunning = false;
        stopTimer();
        var left = _entered;
        _entered = null;
        left?.Exited();
    }

    private bool IsOver(ITrayHoverTarget icon) => TrayRules.Contains(icon.GetBounds(), cursor());
}

/// <summary>A tray icon's context menu as Electron shows it, a Chromium Views menu: through CEF's <c>show_menu</c> on a
/// hidden Views window made for each menu. UI thread only.</summary>
internal sealed unsafe class TrayMenu
{
    private cef_window_t* _window;

    /// <summary>Shows <paramref name="menu"/> at <paramref name="at"/> (screen DIPs), closing any menu shown before. Returns
    /// at once: Views runs the menu in the normal message loop.</summary>
    public void Show(Menu menu, Point at)
    {
        Close();
        var windowDelegate = new WindowDelegate { Frameless = true, Resizable = false, Minimizable = false, Maximizable = false };
        NativeObject.AddRef(windowDelegate.Native);
        var window = CefNative.cef_window_create_top_level(windowDelegate.Native);
        _window = window;
        var handle = (nint)window;
        var popupDelegate = new MenuModelDelegate
        {
            CommandExecuted = (id, flags) => menu.FindByCommandId(id)?.Activate(BrowserWindow.GetFocusedWindow(), KeyboardEvent.FromCefFlags(flags)),
            // Posted: CEF is still inside the window's menu code when it reports the close.
            Closed = () => CefThread.PostUi(() => { if ((nint)_window == handle) Close(); }),
        };
        NativeObject.AddRef(popupDelegate.Native);
        var model = CefNative.cef_menu_model_create(popupDelegate.Native);
        MenuModels.Fill(model, menu);
        var point = new cef_point_t { x = at.X, y = at.Y };
        // The model reference passes to CEF, which keeps it, and through it the delegate, until the menu closes.
        window->show_menu(window, model, &point, cef_menu_anchor_position_t.CEF_MENU_ANCHOR_TOPLEFT);
    }

    /// <summary>Closes the menu, if one is open, and its window.</summary>
    public void Close()
    {
        if (_window is null) return;
        var window = _window;
        _window = null;
        window->cancel_menu(window);
        window->close(window);
        NativeObject.Release(window);
    }
}

/// <summary>One OS's tray icon.</summary>
internal interface ITrayBackend
{
    void Create(NativeImage image, Guid? guid);
    void SetImage(NativeImage image);
    void SetToolTip(string toolTip);
    void DisplayBalloon(DisplayBalloonOptions options);
    void RemoveBalloon();
    void Focus();
    /// <summary>Readies the OS for the icon's menu; false if the menu must not open.</summary>
    bool ActivateForMenu();
    Rectangle GetBounds();
    void Destroy();
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsTrayBackend(Tray owner) : ITrayBackend, ITrayHoverTarget
{
    private static readonly TrayHoverDetector s_hover = new(Screen.GetCursorScreenPoint,
        tick => WindowsTray.StartTimer(TrayHoverDetector.CheckMilliseconds, tick), WindowsTray.StopTimer);
    private static int s_exitHooked;
    private uint _id;
    private nint _icon;

    public uint Id => _id;

    public void Create(NativeImage image, Guid? guid)
    {
        // Windows leaves an icon behind until the pointer passes over it if the process ends without removing it.
        if (Interlocked.Exchange(ref s_exitHooked, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => WindowsTray.DeleteAll();
        _id = WindowsTray.Add(guid, OnCallback);
        SetImage(image);
    }

    // The shell's refusals are logged with Electron's messages, as Electron does, not thrown.
    public void SetImage(NativeImage image)
    {
        var old = _icon;
        _icon = Icon(image);
        if (!WindowsTray.SetIcon(_id, _icon)) Warn("Error setting status tray icon image");
        WindowsTaskbar.DestroyIconHandle(old);
    }

    public void SetToolTip(string toolTip)
    {
        if (!WindowsTray.SetToolTip(_id, toolTip)) Warn("Unable to set tooltip for status tray icon");
    }

    public void DisplayBalloon(DisplayBalloonOptions options)
    {
        var balloonIcon = options.Icon is { } icon ? Icon(icon) : 0;
        if (!WindowsTray.ShowBalloon(_id, options.Title, options.Content, TrayRules.BalloonFlags(options), balloonIcon)) Warn("Unable to create status tray balloon.");
        WindowsTaskbar.DestroyIconHandle(balloonIcon);
    }

    public void RemoveBalloon()
    {
        if (!WindowsTray.RemoveBalloon(_id)) Warn("Unable to remove status tray balloon.");
    }

    public void Focus()
    {
        if (!WindowsTray.Focus(_id)) Warn("Unable to focus tray icon.");
    }

    private static void Warn(string message) => Console.Error.WriteLine($"[Oriel] {message}");

    public bool ActivateForMenu() => WindowsTray.SetForeground();

    public Rectangle GetBounds()
        => WindowsTray.GetRect(_id) is var (x, y, w, h) ? Screen.ScreenToDipRect(null, new Rectangle(x, y, w, h)) : default;

    public void Destroy()
    {
        s_hover.IconRemoved(this);
        WindowsTray.Delete(_id);
        WindowsTaskbar.DestroyIconHandle(_icon);
        _icon = 0;
    }

    void ITrayHoverTarget.Entered() => Post(TrayEvent.MouseEnter);

    void ITrayHoverTarget.Exited() => Post(TrayEvent.MouseLeave);

    private void OnCallback(uint message)
    {
        var e = TrayRules.EventOf(message);
        if (e == TrayEvent.None) return;
        if (e == TrayEvent.MouseMove) s_hover.MouseMove(this);
        Post(e);
    }

    // As Electron: the keys are read now, the pointer and bounds when the posted event runs.
    private void Post(TrayEvent e)
    {
        static bool Down(int key) => (GetKeyState(key) & 0x8000) != 0;
        var keys = (Down(0x10), Down(0x11), Down(0x12), Down(0x5B) || Down(0x5C));
        CefThread.PostUi(() => owner.Raise(e, keys));
    }

    // The system DPI picks the representation: the notification area draws at that scale.
    private static nint Icon(NativeImage image)
    {
        if (image.IsEmpty()) return 0;
        var bitmap = ImagePixels.Unpremultiply(image.BitmapAt(GetDpiForSystem() / 96.0));
        return WindowsTaskbar.CreateIconHandle(bitmap.Bgra, bitmap.Width, bitmap.Height);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}

internal sealed class UnsupportedTrayBackend : ITrayBackend
{
    private static PlatformNotSupportedException NotYet() => new("Tray is not implemented on this OS yet.");

    public void Create(NativeImage image, Guid? guid) => throw NotYet();
    public void SetImage(NativeImage image) => throw NotYet();
    public void SetToolTip(string toolTip) => throw NotYet();
    public void DisplayBalloon(DisplayBalloonOptions options) => throw NotYet();
    public void RemoveBalloon() => throw NotYet();
    public void Focus() => throw NotYet();
    public bool ActivateForMenu() => throw NotYet();
    public Rectangle GetBounds() => throw NotYet();
    public void Destroy() => throw NotYet();
}
