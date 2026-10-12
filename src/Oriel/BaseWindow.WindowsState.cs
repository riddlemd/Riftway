using System.ComponentModel;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

// Each Win32 call sits behind an OperatingSystem.IsWindows() check the analyzer cannot always follow into lambdas.
#pragma warning disable CA1416

namespace Oriel;

public partial record BaseWindowOptions
{
    /// <summary>Whether the window can be focused. On Windows false gives it <c>WS_EX_NOACTIVATE</c>, which also keeps it
    /// off the taskbar. Electron's <c>focusable</c>.</summary>
    /// <remarks>Stored only on macOS and Linux. On Windows a non-focusable window is shown without activating it.</remarks>
    public bool Focusable { get; init; } = true;

    /// <summary>Whether the user can move the window (Windows; ignored elsewhere, as on Linux in Electron).
    /// Electron's <c>movable</c>.</summary>
    public bool Movable { get; init; } = true;

    /// <summary>The window's type. On Windows only <c>toolbar</c> means anything: a tool window
    /// (<c>WS_EX_TOOLWINDOW</c>), which has no taskbar button. Ignored elsewhere for now. Electron's <c>type</c>.</summary>
    public string? Type { get; init; }
}

/// <summary>Arguments for <see cref="BrowserWindow.QuerySessionEnd"/>: cancel to ask Windows not to end the session.</summary>
public sealed class QuerySessionEndEventArgs(IReadOnlyList<string> reasons) : CancelEventArgs
{
    /// <summary>Why the session is ending: <c>shutdown</c>, or any of <c>close-app</c>, <c>critical</c>, <c>logoff</c>.
    /// Electron's <c>event.reasons</c>.</summary>
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>Arguments for <see cref="BrowserWindow.SessionEnded"/>.</summary>
public sealed class SessionEndEventArgs(IReadOnlyList<string> reasons) : EventArgs
{
    /// <summary>Why the session ended, as in <see cref="QuerySessionEndEventArgs.Reasons"/>.</summary>
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>Arguments for <see cref="BrowserWindow.SystemContextMenu"/>: cancel to keep the system menu from showing.</summary>
public sealed class SystemContextMenuEventArgs(Point point) : CancelEventArgs
{
    /// <summary>Where the menu was asked for, in screen pixels as Windows reports it (not DIPs, as in Electron).</summary>
    public Point Point { get; } = point;
}

/// <summary>Arguments for <see cref="BrowserWindow.AppCommand"/>.</summary>
public sealed class AppCommandEventArgs(string command) : EventArgs
{
    /// <summary>Electron's name for the command, such as <c>browser-backward</c> or <c>media-play-pause</c>;
    /// <c>unknown</c> for one Electron does not name.</summary>
    public string Command { get; } = command;
}

public sealed unsafe partial class BrowserWindow
{
    // Windows: Electron's NativeWindowViews state that PreHandleMSG reads, kept in a subclass of the HWND.
    private readonly WindowsMessageHooks _windowsHooks = new();
    private bool? _focusable;

    /// <summary>The user finished moving the window (Windows). Electron's <c>moved</c>.</summary>
    public event EventHandler? MoveFinished;

    /// <summary>The user finished resizing the window (Windows). Electron's <c>resized</c>.</summary>
    public event EventHandler? ResizeFinished;

    /// <summary>Windows asks whether the session may end (<c>WM_QUERYENDSESSION</c>); cancel to refuse. Electron's
    /// <c>query-session-end</c>.</summary>
    public event EventHandler<QuerySessionEndEventArgs>? QuerySessionEnd;

    /// <summary>The session is ending (<c>WM_ENDSESSION</c>), raised on every open window. Unless a handler starts
    /// <see cref="OrielApp.Quit"/>, the process then ends at once, as in Electron. Electron's <c>session-end</c>.</summary>
    public event EventHandler<SessionEndEventArgs>? SessionEnded;

    /// <summary>The system context menu is about to show, from a right click on the title bar, or on a drag region of a
    /// frameless window (Windows); cancel to keep it hidden. Electron's <c>system-context-menu</c>.</summary>
    public event EventHandler<SystemContextMenuEventArgs>? SystemContextMenu;

    /// <summary>A command from a keyboard key or mouse button, such as Back or Play (<c>WM_APPCOMMAND</c>, Windows).
    /// Electron's <c>app-command</c>.</summary>
    public event EventHandler<AppCommandEventArgs>? AppCommand;

    /// <summary>Lets the window be focused, or stops. On Windows this sets <c>WS_EX_NOACTIVATE</c>, removes or restores
    /// the taskbar button (<see cref="SetSkipTaskbar"/>), and unfocuses the window when turned off. Electron's
    /// <c>win.setFocusable()</c>.</summary>
    /// <remarks>Stored only on macOS and Linux for now.</remarks>
    public void SetFocusable(bool focusable)
    {
        Command(w =>
        {
            _focusable = focusable;
            if (OperatingSystem.IsWindows()) WindowsWindowOps.SetNoActivate(Hwnd(w), !focusable);
        });
        if (!OperatingSystem.IsWindows()) return;
        SetSkipTaskbar(!focusable);
        if (!focusable) Blur();
    }

    /// <summary>Whether the window can be focused; on Windows also false while it has <c>WS_EX_NOACTIVATE</c>.
    /// Electron's <c>win.isFocusable()</c> and <c>win.focusable</c>.</summary>
    public bool IsFocusable => Query(w => (_focusable ?? _options.Focusable)
        && !(OperatingSystem.IsWindows() && WindowsWindowOps.IsNoActivate(Hwnd(w))), false);

    /// <summary>Lets the user move the window, or stops (Windows). Electron's <c>win.setMovable()</c>.</summary>
    public void SetMovable(bool movable) => Command(_ => _windowsHooks.Movable = movable);

    /// <summary>Whether the user can move the window; always true off Windows, as Electron on Linux.
    /// Electron's <c>win.isMovable()</c> and <c>win.movable</c>.</summary>
    public bool IsMovable => !OperatingSystem.IsWindows() || Query(_ => _windowsHooks.Movable, false);

    /// <summary>Raises the window to the top of the z-order without focusing it. Windows only for now.
    /// Electron's <c>win.moveTop()</c>.</summary>
    public void MoveTop() => Command(w =>
    {
        if (OperatingSystem.IsWindows()) WindowsWindowOps.MoveTop(Hwnd(w));
    });

    /// <summary>Puts the window just above the window <paramref name="mediaSourceId"/> names
    /// (<c>window:</c><i>id</i><c>:</c><i>id</i>, as <see cref="GetMediaSourceId"/> returns). Windows only for now.
    /// Electron's <c>win.moveAbove()</c>.</summary>
    /// <exception cref="ArgumentException">The id names no window: Electron's <c>Invalid media source id</c>.</exception>
    public void MoveAbove(string mediaSourceId)
    {
        ArgumentNullException.ThrowIfNull(mediaSourceId);
        var other = MediaSourceId.ParseWindow(mediaSourceId);
        var moved = other is { } id && Query(w => !OperatingSystem.IsWindows() || WindowsWindowOps.MoveAbove(Hwnd(w), (nint)id), true);
        if (!moved) throw new ArgumentException("Invalid media source id", nameof(mediaSourceId));
    }

    /// <summary>The window's id for screen capture and <see cref="MoveAbove"/>: <c>window:</c><i>HWND or X11 window</i><c>:0</c>.
    /// Electron's <c>win.getMediaSourceId()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">On macOS, for now.</exception>
    public string GetMediaSourceId()
    {
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("GetMediaSourceId is not implemented on macOS yet.");
        return MediaSourceId.ForWindow(GetNativeWindowHandle());
    }

    /// <summary>Whether Windows is in tablet mode for this window (touch interaction mode); false elsewhere, as in
    /// Electron. Electron's <c>win.isTabletMode()</c>.</summary>
    public bool IsTabletMode => OperatingSystem.IsWindows() && Query(w => WindowsWindowOps.IsTabletMode(Hwnd(w)), false);

    /// <summary>Whether the window is snapped (arranged) by Windows Snap; false elsewhere, as in Electron.
    /// Electron's <c>win.isSnapped()</c> and <c>win.snapped</c>.</summary>
    public bool IsSnapped => OperatingSystem.IsWindows() && Query(w => WindowsWindowOps.IsArranged(Hwnd(w)), false);

    /// <summary>Limits where the window draws and takes input to <paramref name="rects"/>, in DIPs from the window's
    /// top left; an empty list removes the limit. Windows only for now. Electron's <c>win.setShape()</c>.</summary>
    public void SetShape(IReadOnlyList<Rectangle> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        Command(w =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var hwnd = Hwnd(w);
            WindowsWindowMessages.ApplyRegion(hwnd, rects.Count == 0 ? null : [.. WindowShape.ToPixels(rects, WindowsWindow.ScaleFactor(hwnd)).Select(r => (r.Left, r.Top, r.Right, r.Bottom))]);
        });
    }

    /// <summary>Keeps the window at <paramref name="aspectRatio"/> (width / height) while the user resizes it, and resizes
    /// it to that ratio now; 0 or less stops. Windows only for now. Electron's <c>win.setAspectRatio()</c>.</summary>
    /// <remarks>As Electron on Windows, the ratio applies to the whole window, frame included, and
    /// <paramref name="extraSize"/> is ignored (macOS uses it).</remarks>
    public void SetAspectRatio(double aspectRatio, (int Width, int Height)? extraSize = null) => Command(w =>
    {
        _ = extraSize;
        if (!OperatingSystem.IsWindows()) return;
        if (aspectRatio <= 0)
        {
            _windowsHooks.Sizing = null;
            return;
        }
        var ratio = AspectRatioSizing.Ratio(aspectRatio);
        var hwnd = Hwnd(w);
        _windowsHooks.Sizing = (edge, rect) => FitToAspectRatio(hwnd, edge, ratio, rect);
        var (window, _) = WindowsWindowOps.Rects(hwnd);
        var (l, t, r, b) = FitToAspectRatio(hwnd, AspectRatioSizing.BottomRight, ratio, window);
        WindowsWindowOps.SetRect(hwnd, l, t, r, b);
    });

    // Chromium's HWNDMessageHandler::SizeWindowToAspectRatio: the widget's limits in pixels, plus the native frame.
    private (int, int, int, int) FitToAspectRatio(nint hwnd, int edge, float ratio, (int Left, int Top, int Right, int Bottom) rect)
    {
        var scale = WindowsWindow.ScaleFactor(hwnd);
        static int Px(int dip, double scale) => (int)Math.Ceiling(dip * scale);
        var min = (Width: Px(_windowDelegate.MinimumSize.width, scale), Height: Px(_windowDelegate.MinimumSize.height, scale));
        var max = (Width: Px(_windowDelegate.MaximumSize.width, scale), Height: Px(_windowDelegate.MaximumSize.height, scale));
        if (!_windowDelegate.Frameless || (_window is not null && _window->is_maximized(_window) != 0))
        {
            var (window, client) = WindowsWindowOps.Rects(hwnd);
            var inset = (Width: window.Right - window.Left - client.Width, Height: window.Bottom - window.Top - client.Height);
            min = (min.Width + inset.Width, min.Height + inset.Height);
            max = (max.Width == 0 ? 0 : max.Width + inset.Width, max.Height == 0 ? 0 : max.Height + inset.Height);
        }
        var fitted = AspectRatioSizing.Fit(edge, ratio, min, max.Width == 0 || max.Height == 0 ? null : max, (0, 0),
            new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
        return (fitted.Left, fitted.Top, fitted.Right, fitted.Bottom);
    }

    private bool IsWindowsEnabled => Query(w => WindowsWindowOps.IsEnabled(Hwnd(w)), false);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ApplyWindowsEnabled(cef_window_t* w)
    {
        var shouldBe = _enabled.Requested && !_children.Any(c => c._options.Modal && c._window is not null && c._window->@base.@base.is_visible(&c._window->@base.@base) != 0);
        _windowsHooks.KeepDisabled = !_enabled.Requested;
        if (shouldBe != WindowsWindowOps.IsEnabled(Hwnd(w))) WindowsWindowOps.Enable(Hwnd(w), shouldBe);
    }

    // Windows, at creation before the first show: the options Electron applies in NativeWindowViews' constructor,
    // and the subclass that answers the messages PreHandleMSG handles.
    private void AttachWindowsState(cef_window_t* window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = Hwnd(window);
        _windowsHooks.Movable = _options.Movable;
        _windowsHooks.MoveFinished = () => MoveFinished?.Invoke(this, EventArgs.Empty);
        _windowsHooks.ResizeFinished = () => ResizeFinished?.Invoke(this, EventArgs.Empty);
        _windowsHooks.QueryEndSession = lParam =>
        {
            var args = new QuerySessionEndEventArgs(EndSessionReasons.From(lParam));
            QuerySessionEnd?.Invoke(this, args);
            return args.Cancel;
        };
        bool RaiseSystemContextMenu(int x, int y)
        {
            var args = new SystemContextMenuEventArgs(new Point(x, y));
            SystemContextMenu?.Invoke(this, args);
            return args.Cancel;
        }
        // Electron raises it for frameless windows from a right click on a drag region (the caption) instead.
        _windowsHooks.SystemContextMenu = (x, y) => !_windowDelegate.Frameless && RaiseSystemContextMenu(x, y);
        _windowsHooks.CaptionRightClick = _windowDelegate.Frameless ? RaiseSystemContextMenu : null;
        _windowsHooks.AppCommand = lParam => AppCommand?.Invoke(this, new AppCommandEventArgs(AppCommands.Name(AppCommands.FromLParam(lParam))));
        WindowsWindowMessages.SessionEnded ??= OnSessionEnded;
        if (!WindowsWindowMessages.Attach(hwnd, _windowsHooks)) Console.Error.WriteLine("[Oriel] The window's messages could not be routed.");
        if (!_options.Focusable) WindowsWindowOps.SetNoActivate(hwnd, true);
        if (_options.Type == "toolbar") WindowsWindowOps.AddToolWindowStyle(hwnd);
    }

    // Windows, for the first WM_ENDSESSION: every window hears of it, then the process ends unless a quit began.
    private static void OnSessionEnded(nint lParam)
    {
        var reasons = EndSessionReasons.From(lParam);
        foreach (var window in GetAllWindows()) window.SessionEnded?.Invoke(window, new SessionEndEventArgs(reasons));
        if (OperatingSystem.IsWindows() && OrielApp.CurrentOrNull?.IsQuitting != true) WindowsWindowOps.TerminateNow(0);
    }

    // Electron's SetAlwaysOnTop on Windows: the macOS levels below pop-up-menu go behind the taskbar.
    private void ApplyAlwaysOnTopLevel(cef_window_t* w, bool onTop, string level)
    {
        if (!OperatingSystem.IsWindows()) return;
        _windowsHooks.BehindTaskbar = PlacedBehindTaskbar(onTop, level);
        if (_windowsHooks.BehindTaskbar) WindowsWindowOps.MoveBehindTaskbar(Hwnd(w));
    }

    /// <summary>Whether Windows keeps an on-top window under the taskbar: only for the macOS levels below <c>pop-up-menu</c>,
    /// so an unknown level stays above it, as Electron's <c>NativeWindowViews::SetAlwaysOnTop</c>.</summary>
    internal static bool PlacedBehindTaskbar(bool onTop, string level)
        => onTop && level is "floating" or "torn-off-menu" or "modal-panel" or "main-menu" or "status";
}
