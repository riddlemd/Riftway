using System.Runtime.InteropServices;
using Riftway;

/// <summary>Windows window state from Electron's NativeWindowViews: focusable, movable, enabled, z-order, media source
/// ids, snap and tablet state, shape, aspect ratio, window type, session end, system context menu, app commands and
/// always-on-top levels. Every check reads the result back from Win32, not from Riftway.</summary>
static partial class WindowsStateChecks
{
    internal delegate void CheckFn(string name, bool pass, object? detail = null);

    internal static async Task RunAsync(CheckFn check, TimeSpan step)
    {
        if (!OperatingSystem.IsWindows()) return;
        // The shell names the group after the exe's FileDescription; in the bootstrap layout the exe's own name differs.
        var app = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileDescription is { Length: > 0 } description
            ? description : System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        int? Buttons() => WindowsShell.TaskbarNames().FirstOrDefault(n => n.StartsWith(app + " - ", StringComparison.Ordinal)) is { } name
            && System.Text.RegularExpressions.Regex.Match(name[(app.Length + 3)..], @"\d+") is { Success: true } m ? int.Parse(m.Value) : null;

        var w = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: state", Width = 400, Height = 300, X = 200, Y = 200 });
        await Task.Delay(1200);
        var hwnd = w.GetNativeWindowHandle();

        // focusable
        check("a default window has no WS_EX_NOACTIVATE and is focusable", !Has(hwnd, ExNoActivate) && w.IsFocusable, ExStyle(hwnd));
        var withButton = Buttons();
        w.Focus();
        Eventually(() => GetForegroundWindow() == hwnd, step);
        w.SetFocusable(false);
        check("SetFocusable(false) sets WS_EX_NOACTIVATE, drops the taskbar button and the focus",
            Eventually(() => Has(hwnd, ExNoActivate) && GetForegroundWindow() != hwnd && Buttons() == withButton - 1, step) && !w.IsFocusable,
            $"style={ExStyle(hwnd):X} fg={GetForegroundWindow() == hwnd} buttons {withButton} -> {Buttons()}");
        _ = SetWindowLongW(hwnd, GwlExStyleIndex, ExStyle(hwnd) & ~ExNoActivate);
        w.Hide();
        w.Show();
        await Task.Delay(1500);
        check("SetFocusable(false) also sets SkipTaskbar: the button stays gone without the style after a re-show", Buttons() == withButton - 1, $"{withButton} -> {Buttons()}");
        w.SetFocusable(true);
        check("SetFocusable(true) clears it and gives the button back", Eventually(() => !Has(hwnd, ExNoActivate) && Buttons() == withButton, step) && w.IsFocusable,
            $"style={ExStyle(hwnd):X} buttons {withButton} -> {Buttons()}");
        _ = SetWindowLongW(hwnd, GwlExStyleIndex, ExStyle(hwnd) | ExNoActivate);
        check("IsFocusable reads WS_EX_NOACTIVATE set by someone else", !w.IsFocusable, ExStyle(hwnd).ToString("X"));
        _ = SetWindowLongW(hwnd, GwlExStyleIndex, ExStyle(hwnd) & ~ExNoActivate);
        var unfocusable = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: unfocusable", Width = 300, Height = 200, Focusable = false, Type = "toolbar" });
        await Task.Delay(1200);
        var uh = unfocusable.GetNativeWindowHandle();
        check("the Focusable = false option creates the window with WS_EX_NOACTIVATE", Has(uh, ExNoActivate) && !unfocusable.IsFocusable, ExStyle(uh).ToString("X"));
        check("a non-focusable window is not activated when it first shows, as Electron's widget cannot activate", GetForegroundWindow() != uh, GetForegroundWindow());
        unfocusable.Hide();
        unfocusable.Show();
        await Task.Delay(800);
        check("Show() does not activate a non-focusable window either", unfocusable.IsVisible && GetForegroundWindow() != uh, GetForegroundWindow());
        check("Type = toolbar gives WS_EX_TOOLWINDOW; a default window has none", Has(uh, ExToolWindow) && !Has(hwnd, ExToolWindow), $"{ExStyle(uh):X} {ExStyle(hwnd):X}");

        // movable, moved, resized
        var movedCount = 0;
        var resizedCount = 0;
        w.MoveFinished += (_, _) => movedCount++;
        w.ResizeFinished += (_, _) => resizedCount++;
        var start = WindowRect(hwnd);
        var drag = start with { Left = start.Left + 50, Right = start.Right + 50 };
        var willMoves = 0;
        void CountWillMove(object? _, WillMoveEventArgs e) => willMoves++;
        w.WillMove += CountWillMove;
        w.SetMovable(false);
        var blocked = Send(hwnd, WmMoving, 0, drag);
        check("SetMovable(false): WM_MOVING keeps the window where it is", blocked == WindowRect(hwnd) && !w.IsMovable, $"{blocked} vs {WindowRect(hwnd)}");
        w.WillMove -= CountWillMove;
        check("WillMove fires before a window that is not movable blocks the move, as Electron's PreHandleMSG", willMoves == 1, willMoves);
        w.SetMovable(true);
        var allowed = Send(hwnd, WmMoving, 0, drag);
        check("SetMovable(true): WM_MOVING goes through untouched", allowed == drag && w.IsMovable, allowed);
        _ = SendMessageW(hwnd, WmExitSizeMove, 0, 0);
        check("WM_EXITSIZEMOVE after a move raises MoveFinished only", movedCount == 1 && resizedCount == 0, $"moved={movedCount} resized={resizedCount}");
        Send(hwnd, WmSizing, 8, start);
        _ = SendMessageW(hwnd, WmExitSizeMove, 0, 0);
        check("WM_EXITSIZEMOVE after a resize raises ResizeFinished only", movedCount == 1 && resizedCount == 1, $"moved={movedCount} resized={resizedCount}");
        _ = SendMessageW(hwnd, WmExitSizeMove, 0, 0);
        check("WM_EXITSIZEMOVE alone raises neither", movedCount == 1 && resizedCount == 1, $"moved={movedCount} resized={resizedCount}");
        var fixedWindow = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: fixed", Width = 300, Height = 200, Movable = false });
        await Task.Delay(1000);
        var fh = fixedWindow.GetNativeWindowHandle();
        var fr = WindowRect(fh);
        check("the Movable = false option blocks WM_MOVING", Send(fh, WmMoving, 0, fr with { Top = fr.Top + 40, Bottom = fr.Bottom + 40 }) == fr && !fixedWindow.IsMovable);

        // enabled
        w.SetEnabled(false);
        check("SetEnabled(false) disables the window (IsWindowEnabled)", Eventually(() => IsWindowEnabled(hwnd) == 0, step) && !w.IsEnabled);
        w.SetEnabled(true);
        check("SetEnabled(true) enables it", Eventually(() => IsWindowEnabled(hwnd) != 0, step) && w.IsEnabled);
        var modal = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: state modal", Width = 250, Height = 150, Parent = w, Modal = true });
        await Task.Delay(1000);
        var disabledByModal = IsWindowEnabled(hwnd) == 0;
        w.SetEnabled(false);
        w.SetEnabled(true);
        await Task.Delay(300);
        check("SetEnabled(true) leaves the parent of a shown modal disabled", disabledByModal && IsWindowEnabled(hwnd) == 0, $"byModal={disabledByModal} now={IsWindowEnabled(hwnd)}");
        w.SetEnabled(false);
        modal.Destroy();
        await Task.Delay(800);
        check("a window disabled by SetEnabled(false) stays disabled when its modal closes", IsWindowEnabled(hwnd) == 0 && !w.IsEnabled, IsWindowEnabled(hwnd));
        w.SetEnabled(true);
        check("and SetEnabled(true) then enables it", Eventually(() => IsWindowEnabled(hwnd) != 0, step));

        // media source id, moveAbove, moveTop
        check("GetMediaSourceId is window:<HWND>:0", w.GetMediaSourceId() == $"window:{(long)hwnd}:0", w.GetMediaSourceId());
        var other = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: other", Width = 300, Height = 200, X = 260, Y = 260 });
        await Task.Delay(1000);
        var oh = other.GetNativeWindowHandle();
        var foreground = GetForegroundWindow();
        w.MoveAbove(other.GetMediaSourceId());
        check("MoveAbove puts the window just above the other (GW_HWNDPREV)", Eventually(() => GetWindow(oh, GwHwndPrev) == hwnd, step), $"{GetWindow(oh, GwHwndPrev)} vs {hwnd}");
        other.MoveTop();
        check("MoveTop raises the window over the other without activating it",
            Eventually(() => Above(oh, hwnd), step) && GetForegroundWindow() == foreground, $"above={Above(oh, hwnd)} fg {foreground} -> {GetForegroundWindow()}");
        check("MoveAbove with an id that names no window throws Electron's error",
            Throws(() => w.MoveAbove("window:1:0")) == "Invalid media source id (Parameter 'mediaSourceId')" && Throws(() => w.MoveAbove("screen:0:0")) is not null
            && Throws(() => w.MoveAbove("nonsense")) is not null, Throws(() => w.MoveAbove("window:1:0")));
        other.Destroy();

        // tablet mode and snap
        check("IsTabletMode matches UIViewSettings.UserInteractionMode for the window", w.IsTabletMode == TouchMode(hwnd), $"{w.IsTabletMode} vs {TouchMode(hwnd)}");
        check("a placed window is not snapped (IsWindowArranged)", !w.IsSnapped && IsWindowArranged(hwnd) == 0);
        w.Focus();
        Eventually(() => GetForegroundWindow() == hwnd, step);
        Keys(0x5B, 0x25); // Win+Left
        var snapped = Eventually(() => IsWindowArranged(hwnd) != 0, step);
        Keys(0x1B); // Escape leaves Snap Assist
        check("a window snapped with Win+Left reads as snapped", snapped && w.IsSnapped, $"arranged={IsWindowArranged(hwnd)}");
        _ = ShowWindow(hwnd, 9 /* SW_RESTORE */);
        await Task.Delay(500);
        w.SetBounds(new Rectangle(200, 200, 400, 300));
        Eventually(() => IsWindowArranged(hwnd) == 0, step);

        // shape
        var scale = GetDpiForWindow(hwnd) / 96.0;
        w.SetShape([new Rectangle(0, 0, 100, 100), new Rectangle(150, 150, 50, 50)]);
        bool In(int x, int y) => RegionContains(hwnd, (int)(x * scale), (int)(y * scale));
        check("SetShape sets the window region to the union of the rectangles (GetWindowRgn)",
            Eventually(() => In(10, 10), step) && In(160, 160) && !In(120, 50) && !In(300, 250), $"scale={scale}");
        w.SetSize(420, 320);
        await Task.Delay(500);
        check("the shape survives a resize", In(10, 10) && !In(120, 50));
        var shaped = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: frameless shape", Width = 300, Height = 200, Frame = false });
        await Task.Delay(1000);
        var sh = shaped.GetNativeWindowHandle();
        shaped.SetShape([new Rectangle(0, 0, 100, 100)]);
        Eventually(() => RegionContains(sh, 10, 10), step);
        shaped.SetSize(340, 240);
        await Task.Delay(500);
        check("a frameless window's shape survives a resize", RegionContains(sh, (int)(10 * scale), (int)(10 * scale)) && !RegionContains(sh, (int)(150 * scale), (int)(150 * scale)));
        shaped.Maximize();
        await Task.Delay(800);
        check("a frameless window keeps its shape when maximized (Chromium would set the work area)", shaped.IsMaximized && RegionContains(sh, (int)(10 * scale), (int)(10 * scale)) && !RegionContains(sh, (int)(150 * scale), (int)(150 * scale)));
        shaped.Destroy();
        w.SetShape([]);
        check("SetShape([]) removes the region", Eventually(() => GetWindowRgn(hwnd, Scratch()) == 0 /* ERROR: no region */, step));

        // aspect ratio
        w.SetAspectRatio(2.0);
        check("SetAspectRatio(2) resizes the window to twice as wide as high", Eventually(() => WindowRect(hwnd) is var r && Math.Abs(r.Width - 2 * r.Height) <= 1, step), WindowRect(hwnd));
        var now = WindowRect(hwnd);
        Rectangle? seen = null;
        void SeeWillResize(object? _, WillResizeEventArgs e) => seen ??= e.NewBounds;
        w.WillResize += SeeWillResize;
        var dragged = Send(hwnd, WmSizing, 2 /* WMSZ_RIGHT */, now with { Right = now.Left + 900, Bottom = now.Top + 300 });
        w.WillResize -= SeeWillResize;
        check("WillResize sees the dragged rectangle before the aspect ratio fits it, as Chromium fits it after Electron's PreHandleMSG",
            seen is { } s && Math.Abs(s.Width * 300 - s.Height * 900) <= Math.Max(s.Width, s.Height) * 2, seen);
        check("a WM_SIZING from the right edge keeps the ratio from the width", dragged == now with { Right = now.Left + 900, Bottom = now.Top + 450 }, dragged);
        var fromTop = Send(hwnd, WmSizing, 3 /* WMSZ_TOP */, new Rect(now.Left, now.Top, now.Left + 500, now.Top + 400));
        check("a WM_SIZING from the top edge keeps the ratio from the height", fromTop == new Rect(now.Left, now.Top, now.Left + 800, now.Top + 400), fromTop);
        var (cw, ch) = ClientSize(hwnd);
        var inset = (W: now.Width - cw, H: now.Height - ch);
        w.SetMinimumSize((int)Math.Ceiling(300 / scale), 0);
        var small = Send(hwnd, WmSizing, 6 /* WMSZ_BOTTOM */, now with { Bottom = now.Top + 100 });
        var minWidth = (int)Math.Ceiling(Math.Ceiling(300 / scale) * scale) + inset.W;
        check("below the minimum width, sizing takes the minimum plus the native frame, as Chromium",
            small.Width == minWidth && small.Height == (int)Math.Round((minWidth - 0) / 2.0, MidpointRounding.AwayFromZero), $"{small} min={minWidth}");
        w.SetMinimumSize(0, 0);
        w.SetAspectRatio(0);
        var free = Send(hwnd, WmSizing, 2, now with { Right = now.Left + 900, Bottom = now.Top + 300 });
        check("SetAspectRatio(0) stops keeping the ratio", free == now with { Right = now.Left + 900, Bottom = now.Top + 300 }, free);
        _ = SendMessageW(hwnd, WmExitSizeMove, 0, 0);
        w.SetBounds(new Rectangle(200, 200, 400, 300));

        // session end
        IReadOnlyList<string>? queried = null;
        var refuse = false;
        w.QuerySessionEnd += (_, e) => { queried = e.Reasons; e.Cancel = refuse; };
        var ended = 0;
        w.SessionEnded += (_, _) => ended++;
        var allow = SendMessageW(hwnd, WmQueryEndSession, 0, unchecked((nint)0x80000000L));
        check("WM_QUERYENDSESSION raises QuerySessionEnd with the reasons and answers TRUE", allow == 1 && queried is ["logoff"], $"{allow} {string.Join(",", queried ?? [])}");
        refuse = true;
        var refused = SendMessageW(hwnd, WmQueryEndSession, 0, 0);
        check("a cancelled QuerySessionEnd answers FALSE", refused == 0 && queried is ["shutdown"], $"{refused} {string.Join(",", queried ?? [])}");
        _ = SendMessageW(hwnd, WmEndSession, 0, 0);
        check("WM_ENDSESSION with wParam FALSE raises nothing", ended == 0, ended);

        // system context menu
        Point? menuAt = null;
        var keepMenu = true;
        w.SystemContextMenu += (_, e) => { menuAt = e.Point; e.Cancel = keepMenu; };
        var c = ClientOrigin(hwnd);
        var at = MakeLParam(c.X + 20, c.Y + 30);
        var menuResult = SendMessageW(hwnd, WmContextMenu, hwnd, at);
        check("WM_CONTEXTMENU raises SystemContextMenu with the screen point", menuAt == new Point(c.X + 20, c.Y + 30) && menuResult == 0, menuAt);
        keepMenu = true;
        var bounds = WindowRect(hwnd);
        var caption = MakeLParam(bounds.Left + 80, bounds.Top + 12);
        check("a cancelled SystemContextMenu keeps the system menu from showing (title bar point)", !MenuShows(hwnd, caption));
        keepMenu = false;
        check("an uncancelled one lets the system menu show", MenuShows(hwnd, caption));
        var frameless = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: frameless state", Width = 300, Height = 200, Frame = false });
        await Task.Delay(1000);
        var frh = frameless.GetNativeWindowHandle();
        var framelessMenus = 0;
        frameless.SystemContextMenu += (_, e) => { framelessMenus++; e.Cancel = true; };
        var fc = ClientOrigin(frh);
        _ = SendMessageW(frh, WmContextMenu, frh, MakeLParam(fc.X + 10, fc.Y + 10));
        check("a frameless window raises no SystemContextMenu from WM_CONTEXTMENU, as Electron", framelessMenus == 0, framelessMenus);
        // A right click on a drag region is a caption click: Chromium captures the press and shows the system menu on release.
        await frameless.LoadUrl("data:text/html,<body style='margin:0;height:100vh;-webkit-app-region:drag'></body>");
        var fb = WindowRect(frh);
        var (dx, dy) = (fb.Left + fb.Width / 2, fb.Top + fb.Height / 2);
        Eventually(() => SendMessageW(frh, 0x0084 /* WM_NCHITTEST */, 0, MakeLParam(dx, dy)) == 2, step);
        var dragHit = SendMessageW(frh, 0x0084, 0, MakeLParam(dx, dy));
        var dragMenu = CaptionRightClickShowsMenu(frh, dx, dy);
        check("a right click on a frameless window's drag region raises SystemContextMenu, and cancelling it keeps the menu hidden, as Electron",
            dragHit == 2 && framelessMenus == 1 && !dragMenu, $"hit={dragHit} raised={framelessMenus} menu={dragMenu}");
        frameless.Destroy();

        // app commands
        var commands = new List<string>();
        w.AppCommand += (_, e) => commands.Add(e.Command);
        _ = SendMessageW(hwnd, WmAppCommand, hwnd, (nint)((0x8000 | 1) << 16)); // mouse, APPCOMMAND_BROWSER_BACKWARD
        _ = SendMessageW(hwnd, WmAppCommand, hwnd, (nint)(14 << 16));
        _ = SendMessageW(hwnd, WmAppCommand, hwnd, (nint)(99 << 16));
        check("WM_APPCOMMAND raises AppCommand with Electron's names", commands is ["browser-backward", "media-play-pause", "unknown"], string.Join(",", commands));

        // always on top
        var changes = new List<bool>();
        w.AlwaysOnTopChanged += (_, e) => changes.Add(e.IsAlwaysOnTop);
        var tray = FindWindowW("Shell_TrayWnd", null);
        w.SetAlwaysOnTop(true, "screen-saver");
        check("a screen-saver level is topmost and above the taskbar",
            Eventually(() => Has(hwnd, ExTopmost, GwlExStyleIndex) && Above(hwnd, tray), step), $"topmost={Has(hwnd, ExTopmost)} above={Above(hwnd, tray)}");
        w.SetAlwaysOnTop(true, "screen-saver");
        w.SetAlwaysOnTop(false);
        check("AlwaysOnTopChanged fires on each change only", Eventually(() => changes is [true, false], step), string.Join(",", changes));
        check("SetAlwaysOnTop(false) clears WS_EX_TOPMOST", Eventually(() => !Has(hwnd, ExTopmost), step));
        w.SetAlwaysOnTop(true, "floating");
        check("a floating level is topmost but just under the taskbar, as Electron on Windows",
            Eventually(() => Has(hwnd, ExTopmost) && Above(tray, hwnd), step), $"topmost={Has(hwnd, ExTopmost)} under={Above(tray, hwnd)}");
        w.MoveTop();
        Eventually(() => Above(hwnd, tray), step);
        fixedWindow.Focus();
        await Task.Delay(300);
        w.Focus();
        check("activating a floating window puts it back under the taskbar", Eventually(() => Above(tray, hwnd), step) && Has(hwnd, ExTopmost), Above(tray, hwnd));
        w.SetAlwaysOnTop(true, "normal");
        check("level normal with flag set still keeps the window on top on Windows", Eventually(() => w.IsAlwaysOnTop && Has(hwnd, ExTopmost), step), w.IsAlwaysOnTop);
        w.SetAlwaysOnTop(false);
        Eventually(() => !Has(hwnd, ExTopmost), step);
        string? unknownError = null;
        try { w.SetAlwaysOnTop(true, "bogus-level"); } catch (Exception e) { unknownError = e.GetType().Name; }
        check("an unknown level does not throw and keeps the window on top, as Electron on Windows",
            unknownError is null && Eventually(() => w.IsAlwaysOnTop && Has(hwnd, ExTopmost), step), $"error={unknownError} onTop={w.IsAlwaysOnTop}");
        // Activation is when a behind-the-taskbar level drops back under it, so the unknown level must survive one.
        w.MoveTop();
        fixedWindow.Focus();
        await Task.Delay(300);
        w.Focus();
        await Task.Delay(500);
        check("an unknown level stays above the taskbar after activation, as Electron on Windows", Above(hwnd, tray) && Has(hwnd, ExTopmost), $"above={Above(hwnd, tray)}");
        w.SetAlwaysOnTop(true, null!);
        fixedWindow.Focus();
        await Task.Delay(300);
        w.Focus();
        check("a null level is floating, under the taskbar after activation, as Electron's non-string level",
            Eventually(() => Above(tray, hwnd), step) && Has(hwnd, ExTopmost), $"under={Above(tray, hwnd)}");
        w.SetAlwaysOnTop(false);
        Eventually(() => !Has(hwnd, ExTopmost), step);

        unfocusable.Destroy();
        fixedWindow.Destroy();
        w.Destroy();
        await Task.Delay(300);
    }

}

static unsafe partial class WindowsStateChecks
{
    private const uint WmMoving = 0x0216, WmSizing = 0x0214, WmExitSizeMove = 0x0232, WmQueryEndSession = 0x0011, WmEndSession = 0x0016;
    private const uint WmContextMenu = 0x007B, WmAppCommand = 0x0319;
    private const uint ExNoActivate = 0x08000000, ExToolWindow = 0x80, ExTopmost = 0x8;
    private const int GwlExStyleIndex = -20;
    private const uint GwHwndPrev = 3, GwHwndNext = 2;

    internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
    }

    private static uint ExStyle(nint hwnd) => GetWindowLongW(hwnd, GwlExStyleIndex);

    private static bool Has(nint hwnd, uint bits, int index = GwlExStyleIndex) => (GetWindowLongW(hwnd, index) & bits) != 0;

    private static Rect WindowRect(nint hwnd)
    {
        Rect r;
        _ = GetWindowRect(hwnd, &r);
        return r;
    }

    private static Rect Send(nint hwnd, uint message, nint wParam, Rect rect)
    {
        _ = SendMessageW(hwnd, message, wParam, (nint)(&rect));
        return rect;
    }

    // Whether `upper` comes before `lower` in the z-order.
    private static bool Above(nint upper, nint lower)
    {
        for (var h = GetWindow(lower, GwHwndPrev); h != 0; h = GetWindow(h, GwHwndPrev))
            if (h == upper) return true;
        return false;
    }

    private static string? Throws(Action action)
    {
        try { action(); return null; }
        catch (Exception e) { return e.Message; }
    }

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static (int W, int H) ClientSize(nint hwnd)
    {
        Rect r;
        _ = GetClientRect(hwnd, &r);
        return (r.Width, r.Height);
    }

    // Sends WM_CONTEXTMENU and reports whether a menu loop held the window's thread; the menu is then dismissed.
    // WM_NCRBUTTONDOWN on the caption, then the WM_RBUTTONUP the capture turns the release into; true if a menu showed.
    private static bool CaptionRightClickShowsMenu(nint hwnd, int x, int y)
    {
        _ = SendMessageW(hwnd, 0x00A4 /* WM_NCRBUTTONDOWN */, 2 /* HTCAPTION */, MakeLParam(x, y));
        var c = ClientOrigin(hwnd);
        var answered = SendMessageTimeoutW(hwnd, 0x0205 /* WM_RBUTTONUP */, 0, MakeLParam(x - c.X, y - c.Y), 0x2, 1500, out _) != 0;
        var shown = !answered || FindWindowW("#32768", null) != 0;
        for (var i = 0; i < 20 && FindWindowW("#32768", null) is var menu && menu != 0; i++)
        {
            _ = PostMessageW(menu, 0x0100 /* WM_KEYDOWN */, 0x1B, 0);
            _ = PostMessageW(hwnd, 0x001F /* WM_CANCELMODE */, 0, 0);
            Thread.Sleep(150);
        }
        return shown;
    }

    private static bool MenuShows(nint hwnd, nint lParam)
    {
        var answered = SendMessageTimeoutW(hwnd, WmContextMenu, hwnd, lParam, 0x2 /* SMTO_ABORTIFHUNG */, 1500, out _) != 0;
        if (answered) return false;
        for (var i = 0; i < 20 && FindWindowW("#32768", null) is var menu && menu != 0; i++)
        {
            _ = PostMessageW(menu, 0x0100 /* WM_KEYDOWN */, 0x1B, 0);
            _ = PostMessageW(hwnd, 0x001F /* WM_CANCELMODE */, 0, 0);
            Thread.Sleep(150);
        }
        Thread.Sleep(300);
        return true;
    }

    private static nint Scratch() => CreateRectRgn(0, 0, 0, 0);

    private static bool RegionContains(nint hwnd, int x, int y)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try { return GetWindowRgn(hwnd, region) is 2 or 3 && PtInRegion(region, x, y) != 0; }
        finally { _ = DeleteObject(region); }
    }

    private static Point ClientOrigin(nint hwnd)
    {
        var p = new Pt();
        _ = ClientToScreen(hwnd, &p);
        return new Point(p.X, p.Y);
    }

    private static nint MakeLParam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    private static void Keys(params ushort[] keys)
    {
        var inputs = new List<Input>();
        foreach (var k in keys) inputs.Add(new Input { Type = 1, Vk = k });
        foreach (var k in keys.Reverse()) inputs.Add(new Input { Type = 1, Vk = k, Flags = 2 /* KEYEVENTF_KEYUP */ });
        var array = inputs.ToArray();
        fixed (Input* p = array) _ = SendInput((uint)array.Length, p, sizeof(Input));
        Thread.Sleep(300);
    }

    // Read independently of Riftway: UIViewSettings through its interop factory, touch mode being 1.
    private static bool TouchMode(nint hwnd)
    {
        if (WindowsCreateString("Windows.UI.ViewManagement.UIViewSettings", 40, out var name) < 0) return false;
        try
        {
            var iid = new Guid("3694DBF9-8F68-44BE-8FF5-195C98EDE8A6");
            if (RoGetActivationFactory(name, ref iid, out var interop) < 0) return false;
            var settingsIid = new Guid("C63657F6-8850-470D-88F8-455E16EA2C26");
            void* settings;
            var vtable = *(void***)interop;
            if (((delegate* unmanaged[Stdcall]<nint, nint, Guid*, void**, int>)vtable[6])(interop, hwnd, &settingsIid, &settings) < 0) return false;
            int mode;
            var ok = ((delegate* unmanaged[Stdcall]<void*, int*, int>)(*(void***)settings)[6])(settings, &mode) >= 0;
            return ok && mode == 1;
        }
        finally { _ = WindowsDeleteString(name); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }

    // INPUT with a KEYBDINPUT, padded to the size of the union's largest member (MOUSEINPUT) on 64-bit.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern uint SetWindowLongW(nint hwnd, int index, uint value);
    [DllImport("user32.dll")] private static extern int GetClientRect(nint hwnd, Rect* rect);
    [DllImport("user32.dll")] private static extern int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, Rect* rect);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern int IsWindowArranged(nint hwnd);
    [DllImport("user32.dll")] private static extern int ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("user32.dll")] private static extern int ClientToScreen(nint hwnd, Pt* point);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input* inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string className, string? windowName);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint handle);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, out nint name);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint name);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, ref Guid iid, out nint factory);
}
