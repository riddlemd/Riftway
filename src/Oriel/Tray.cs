using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A click or move on a tray icon. Electron's <c>event</c>, <c>bounds</c> and <c>position</c> arguments.</summary>
public sealed class TrayEventArgs(Rectangle bounds, Point position, bool shift, bool ctrl, bool alt, bool meta) : EventArgs
{
    /// <summary>The icon's bounds on screen in DIPs; empty when the shell cannot place it. Electron's <c>bounds</c>.</summary>
    public Rectangle Bounds { get; } = bounds;

    /// <summary>Where the pointer was, in screen DIPs. Electron's <c>position</c>.</summary>
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

    /// <summary>A balloon appeared. Electron's <c>'balloon-show'</c>.</summary>
    public event EventHandler? BalloonShown;

    /// <summary>The user clicked a balloon. Electron's <c>'balloon-click'</c>.</summary>
    public event EventHandler? BalloonClicked;

    /// <summary>A balloon timed out or the user closed it. Electron's <c>'balloon-closed'</c>.</summary>
    public event EventHandler? BalloonClosed;

    /// <summary>Removes the icon. Electron's <c>tray.destroy()</c>.</summary>
    public void Destroy() => OnUi(() =>
    {
        if (_destroyed) return;
        _destroyed = true;
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

    /// <summary>macOS: text beside the icon. Stored only elsewhere. Electron's <c>tray.setTitle()</c>.</summary>
    public void SetTitle(string title) => OnUi(() => _title = title ?? "");

    /// <summary>The title as set. Electron's <c>tray.getTitle()</c>.</summary>
    public string GetTitle() => CefThread.InvokeOnUi(() => _title);

    /// <summary>macOS: report two clicks rather than a double click. Stored only elsewhere.
    /// Electron's <c>tray.setIgnoreDoubleClickEvents()</c>.</summary>
    public void SetIgnoreDoubleClickEvents(bool ignore) => OnUi(() => _ignoreDoubleClick = ignore);

    /// <summary>The value set by <see cref="SetIgnoreDoubleClickEvents"/>. Electron's <c>tray.getIgnoreDoubleClickEvents()</c>.</summary>
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

    /// <summary>Opens <paramref name="menu"/>, or the context menu, at <paramref name="position"/> (screen DIPs) or the
    /// pointer. Returns when it closes. Electron's <c>tray.popUpContextMenu()</c>.</summary>
    public void PopUpContextMenu(Menu? menu = null, Point? position = null)
        => Live(() => { if ((menu ?? _contextMenu) is { } m) PopUp(m, position); });

    /// <summary>Closes the menu the icon opened, if any. Electron's <c>tray.closeContextMenu()</c>.</summary>
    // Not through the UI thread: it is inside the menu's loop and would run the call only after the menu closed.
    public void CloseContextMenu() => _backend.CloseContextMenu();

    /// <summary>The icon's bounds on screen in DIPs; empty when the shell cannot place it (on Windows 11, while it sits in
    /// the overflow). Electron's <c>tray.getBounds()</c>.</summary>
    public Rectangle GetBounds() => CefThread.InvokeOnUi(() => _destroyed ? default : _backend.GetBounds());

    private void PopUp(Menu menu, Point? position)
    {
        if (_backend.PopUpMenu(TrayRules.MenuEntries(menu), position) is { } commandId && menu.FindByCommandId(commandId) is { } item)
            item.Activate(BrowserWindow.GetFocusedWindow());
    }

    /// <summary>A shell event for this icon, on the UI thread.</summary>
    internal void Raise(TrayEvent e, Point position, (bool Shift, bool Ctrl, bool Alt, bool Meta) keys)
    {
        if (_destroyed) return;
        TrayEventArgs Args() => new(_backend.GetBounds(), position, keys.Shift, keys.Ctrl, keys.Alt, keys.Meta);
        switch (e)
        {
            case TrayEvent.Click: Clicked?.Invoke(this, Args()); break;
            case TrayEvent.DoubleClick: DoubleClicked?.Invoke(this, Args()); break;
            case TrayEvent.MiddleClick: MiddleClicked?.Invoke(this, Args()); break;
            case TrayEvent.MouseMove: MouseMoved?.Invoke(this, Args()); break;
            // Electron's rule: a context menu takes the right click; without one, the event does.
            case TrayEvent.RightClick when _contextMenu is { } menu: PopUp(menu, position); break;
            case TrayEvent.RightClick: RightClicked?.Invoke(this, Args()); break;
            case TrayEvent.BalloonShow: BalloonShown?.Invoke(this, EventArgs.Empty); break;
            case TrayEvent.BalloonClick: BalloonClicked?.Invoke(this, EventArgs.Empty); break;
            case TrayEvent.BalloonClosed: BalloonClosed?.Invoke(this, EventArgs.Empty); break;
        }
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
    BalloonShow,
    BalloonClick,
    BalloonClosed,
}

internal static class TrayRules
{
    /// <summary>A notification icon version 4 event: Electron takes the press for a click, the double click and the middle
    /// press; version 4 sends <c>WM_CONTEXTMENU</c> for a right click or Shift+F10, and <c>NIN_KEYSELECT</c> for Enter
    /// or Space on a focused icon.</summary>
    internal static TrayEvent EventOf(uint code) => code switch
    {
        0x0201 => TrayEvent.Click,         // WM_LBUTTONDOWN
        0x0401 => TrayEvent.Click,         // NIN_KEYSELECT
        0x0203 => TrayEvent.DoubleClick,   // WM_LBUTTONDBLCLK
        0x0207 => TrayEvent.MiddleClick,   // WM_MBUTTONDOWN
        0x007B => TrayEvent.RightClick,    // WM_CONTEXTMENU
        0x0200 => TrayEvent.MouseMove,     // WM_MOUSEMOVE
        0x0402 => TrayEvent.BalloonShow,   // NIN_BALLOONSHOW
        0x0405 => TrayEvent.BalloonClick,  // NIN_BALLOONUSERCLICK
        0x0403 => TrayEvent.BalloonClosed, // NIN_BALLOONHIDE
        0x0404 => TrayEvent.BalloonClosed, // NIN_BALLOONTIMEOUT
        _ => TrayEvent.None,
    };

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

    /// <summary>A native menu from <paramref name="menu"/>: hidden items left out, each label with its accelerator after a
    /// tab, as Windows menus show shortcuts.</summary>
    internal static List<WindowsMenuEntry> MenuEntries(Menu menu)
        => [.. menu.Items.Where(i => i.Visible).Select(i => i.Type == MenuItemType.Separator
            ? new WindowsMenuEntry(0, "", true, true, false, false, null)
            : new WindowsMenuEntry(i.CommandId, AcceleratorText(i.Accelerator) is { } keys ? i.Label + "\t" + keys : i.Label, false, i.Enabled,
                i.Checked && i.Type is MenuItemType.Checkbox or MenuItemType.Radio, i.Type == MenuItemType.Radio,
                i.Type == MenuItemType.Submenu && i.Submenu is { } sub ? MenuEntries(sub) : null))];

    /// <summary>An accelerator as Windows writes it, e.g. <c>CmdOrCtrl+Shift+S</c> as <c>Ctrl+Shift+S</c>; null for none, or
    /// for a Command/Super shortcut, which Windows menus do not show.</summary>
    internal static string? AcceleratorText(string? accelerator)
    {
        if (string.IsNullOrEmpty(accelerator)) return null;
        var parts = new List<string>();
        foreach (var token in accelerator.Split('+'))
        {
            var t = token.Trim();
            switch (t.ToLowerInvariant())
            {
                case "cmdorctrl" or "commandorcontrol" or "control" or "ctrl": parts.Add("Ctrl"); break;
                case "alt" or "option" or "altgr": parts.Add("Alt"); break;
                case "shift": parts.Add("Shift"); break;
                case "cmd" or "command" or "super" or "meta": return null;
                case "plus": parts.Add("+"); break;
                default: parts.Add(t.Length == 1 ? t.ToUpperInvariant() : t); break;
            }
        }
        return string.Join("+", parts);
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
    /// <summary>Shows the menu and returns the chosen command id, or null.</summary>
    int? PopUpMenu(IReadOnlyList<WindowsMenuEntry> entries, Point? position);
    void CloseContextMenu();
    Rectangle GetBounds();
    void Destroy();
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsTrayBackend(Tray owner) : ITrayBackend
{
    private static int s_exitHooked;
    private uint _id;
    private nint _icon;

    public void Create(NativeImage image, Guid? guid)
    {
        // Windows leaves an icon behind until the pointer passes over it if the process ends without removing it.
        if (Interlocked.Exchange(ref s_exitHooked, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => WindowsTray.DeleteAll();
        _icon = Icon(image);
        _id = WindowsTray.Add(_icon, "", guid, OnCallback);
        if (_id == 0) throw new InvalidOperationException("The shell did not take the tray icon.");
    }

    public void SetImage(NativeImage image)
    {
        var old = _icon;
        _icon = Icon(image);
        WindowsTray.SetIcon(_id, _icon);
        WindowsTaskbar.DestroyIconHandle(old);
    }

    public void SetToolTip(string toolTip) => WindowsTray.SetToolTip(_id, toolTip);

    public void DisplayBalloon(DisplayBalloonOptions options)
    {
        var balloonIcon = options.Icon is { } icon ? Icon(icon) : 0;
        WindowsTray.ShowBalloon(_id, options.Title, options.Content, TrayRules.BalloonFlags(options), balloonIcon);
        WindowsTaskbar.DestroyIconHandle(balloonIcon);
    }

    public void RemoveBalloon() => WindowsTray.RemoveBalloon(_id);

    public void Focus() => WindowsTray.Focus(_id);

    public int? PopUpMenu(IReadOnlyList<WindowsMenuEntry> entries, Point? position)
    {
        var at = position is { } p ? Screen.DipToScreenPoint(p) : Cursor();
        return WindowsTray.TrackMenu(entries, at.X, at.Y);
    }

    public void CloseContextMenu() => WindowsTray.CloseMenu();

    public Rectangle GetBounds()
        => WindowsTray.GetRect(_id) is var (x, y, w, h) ? Screen.ScreenToDipRect(null, new Rectangle(x, y, w, h)) : default;

    public void Destroy()
    {
        WindowsTray.Delete(_id);
        WindowsTaskbar.DestroyIconHandle(_icon);
        _icon = 0;
    }

    private void OnCallback(uint code, int x, int y)
    {
        var e = TrayRules.EventOf(code);
        if (e == TrayEvent.None) return;
        static bool Down(int key) => (GetKeyState(key) & 0x8000) != 0;
        var keys = (Down(0x10), Down(0x11), Down(0x12), Down(0x5B) || Down(0x5C));
        owner.Raise(e, Screen.ScreenToDipPoint(new Point(x, y)), keys);
    }

    // The system DPI picks the representation: the notification area draws at that scale.
    private static nint Icon(NativeImage image)
    {
        if (image.IsEmpty()) return 0;
        var bitmap = ImagePixels.Unpremultiply(image.BitmapAt(GetDpiForSystem() / 96.0));
        return WindowsTaskbar.CreateIconHandle(bitmap.Bgra, bitmap.Width, bitmap.Height);
    }

    private static Point Cursor()
    {
        Point p;
        _ = GetCursorPos(&p);
        return p;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetCursorPos(Point* point);
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
    public int? PopUpMenu(IReadOnlyList<WindowsMenuEntry> entries, Point? position) => throw NotYet();
    public void CloseContextMenu() => throw NotYet();
    public Rectangle GetBounds() => throw NotYet();
    public void Destroy() => throw NotYet();
}
