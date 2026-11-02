using Riftway;

/// <summary>Windows BaseWindow behaviour with no page of its own: Destroy under a modal dialog, the title bar overlay, and
/// the Alt-driven menu bar with real keyboard input.</summary>
static class WindowsBaseWindowChecks
{
    internal delegate void CheckFn(string name, bool pass, object? detail = null);

    private const byte VkControl = 0x11, VkMenu = 0x12, VkF = 0x46, VkM = 0x4D;
    private const uint WmClose = 0x0010;

    internal static async Task RunAsync(CheckFn check, TimeSpan step)
    {
        if (!OperatingSystem.IsWindows()) return;
        await DestroyUnderDialogAsync(check, step);
        await TitleBarOverlayAsync(check, step);
        await MenuBarKeysAsync(check, step);
    }

    private static async Task DestroyUnderDialogAsync(CheckFn check, TimeSpan step)
    {
        var boxHost = Host("Riftway window test: base box host");
        var box = Dialog.ShowMessageBoxAsync(boxHost.Window, new MessageBoxOptions { Title = "Riftway base box", Message = "Destroyed under me", Buttons = ["Keep", "Drop", "Later"], CancelId = 2 });
        nint boxHwnd = 0;
        var boxShown = Eventually(() => (boxHwnd = WinTaskDialog.Find("Riftway base box")) != 0, step);
        var boxOwned = boxShown && WindowsShell.Owner(boxHwnd) == boxHost.Hwnd;
        // Electron's destroy closes the window at once (CloseNow): no close event. Its dialogs run on threads of their own,
        // which DestroyWindow cannot reach, so the dialog stays up unowned and its promise waits for the user.
        boxHost.Window.Destroy();
        var boxDestroyed = Eventually(() => boxHost.Window.IsDestroyed && boxHost.Closed == 1, step);
        await Task.Delay(1000);
        var boxStays = WindowsShell.Visible(boxHwnd) && WindowsShell.Owner(boxHwnd) == 0 && !box.IsCompleted;
        check("BaseWindow: Windows: Destroy with a message box up raises Closed once and no Closing; the box stays up, unowned and unanswered, as Electron's",
            boxOwned && boxDestroyed && boxStays && boxHost.Closing == 0,
            $"shown={boxShown} owned={boxOwned} destroyed={boxDestroyed} stays={boxStays} owner={WindowsShell.Owner(boxHwnd)} {box.Status} closed={boxHost.Closed} closing={boxHost.Closing}");
        WindowsShell.Send(boxHwnd, WmClose, 0, 0);
        var boxSettled = await Task.WhenAny(box, Task.Delay(step)) == box;
        check("BaseWindow: Windows: closing that box answers with its cancel id, and Closed is not raised again",
            boxSettled && box is { IsCompletedSuccessfully: true, Result.Response: 2 } && boxHost.Closed == 1,
            $"{box.Status} {(box.IsCompletedSuccessfully ? box.Result.Response : box.Exception?.GetBaseException().Message)} closed={boxHost.Closed}");

        var fileHost = Host("Riftway window test: base file dialog host");
        var open = Dialog.ShowOpenDialogAsync(fileHost.Window, new OpenDialogOptions { Title = "Riftway base file dialog" });
        nint fileHwnd = 0;
        var fileShown = Eventually(() => (fileHwnd = WindowsShell.FileDialog("Riftway base file dialog")) != 0, step);
        var fileOwned = fileShown && WindowsShell.Owner(fileHwnd) == fileHost.Hwnd;
        fileHost.Window.Destroy();
        var fileDestroyed = Eventually(() => fileHost.Window.IsDestroyed && fileHost.Closed == 1, step);
        await Task.Delay(1000);
        var fileStays = WindowsShell.Visible(fileHwnd) && WindowsShell.Owner(fileHwnd) == 0 && !open.IsCompleted;
        check("BaseWindow: Windows: Destroy with a file dialog up raises Closed once and no Closing; the dialog stays up, unowned and unanswered, as Electron's",
            fileOwned && fileDestroyed && fileStays && fileHost.Closing == 0,
            $"shown={fileShown} owned={fileOwned} destroyed={fileDestroyed} stays={fileStays} owner={WindowsShell.Owner(fileHwnd)} {open.Status} closed={fileHost.Closed} closing={fileHost.Closing}");
        WindowsShell.Send(fileHwnd, WmClose, 0, 0);
        var fileSettled = await Task.WhenAny(open, Task.Delay(step)) == open;
        check("BaseWindow: Windows: closing that dialog answers cancelled, and Closed is not raised again",
            fileSettled && open is { IsCompletedSuccessfully: true, Result.Canceled: true } && fileHost.Closed == 1,
            $"{open.Status} {open.Exception?.GetBaseException().Message} closed={fileHost.Closed}");

        // The UI thread still answers: a hang under the dialog's modal loop would stall this constructor.
        var probe = Task.Run(() => new BaseWindow(new BaseWindowOptions { Title = "Riftway window test: base after destroy", Show = false }));
        var answered = await Task.WhenAny(probe, Task.Delay(step)) == probe;
        check("BaseWindow: Windows: the app still answers after destroying windows under dialogs", answered && probe.IsCompletedSuccessfully);
        if (answered) probe.Result.Destroy();
    }

    private static async Task TitleBarOverlayAsync(CheckFn check, TimeSpan step)
    {
        // As the BrowserWindow overlay checks: no menu bar under the overlay.
        var appMenu = Menu.GetApplicationMenu();
        Menu.SetApplicationMenu(null);
        var overlaid = new BaseWindow(new BaseWindowOptions
        {
            Title = "Riftway window test: base title bar overlay", X = 240, Y = 240, Width = 500, Height = 300, BackgroundColor = "#FFFF00",
            TitleBarStyle = TitleBarStyle.Hidden, TitleBarOverlay = new TitleBarOverlayOptions { Color = "#2050C0", SymbolColor = "#FFFFFF", Height = 40 },
        });
        var closed = false;
        overlaid.Closed += (_, _) => closed = true;
        var hwnd = overlaid.GetNativeWindowHandle();
        overlaid.Focus();
        Eventually(() => WindowsShell.Foreground() == hwnd, step);
        await Task.Delay(1200);
        var box = overlaid.GetTitleBarOverlayBounds();
        check("BaseWindow: Windows: the title bar overlay sits at the top right, 45 + 46 + 46 DIPs wide, at the height asked",
            box == new Rectangle(500 - 137, 0, 137, 40), box);
        var scale = Screen.GetDisplayMatching(overlaid.GetBounds()).ScaleFactor;
        int Px(int dip) => (int)Math.Round(dip * scale);
        int ButtonLeft(int index) { var (x, _, w, _) = WindowsShell.Bounds(hwnd); return x + w - Px(index switch { 0 => 137, 1 => 92, _ => 46 }); }
        (int X, int Y) Button(int index, int dy = 0) => (ButtonLeft(index) + Px(6), WindowsShell.Bounds(hwnd).Y + Px(6 + dy));
        var resting = WindowsShell.ScreenPixel(Button(0).X, Button(0).Y);
        var under = WindowsShell.ScreenPixel(Button(0).X, Button(0, 40).Y);
        check("BaseWindow: Windows: the overlay has its colour, the window's colour just below it",
            IsBlue(resting) && IsYellow(under), $"{Rgb(resting)} below {Rgb(under)}");
        const int HtClient = 1, HtMaxButton = 9;
        var (mx, my) = (Button(1).X + Px(17), Button(1, 8).Y);
        var maxHit = WindowsShell.HitTest(hwnd, mx, my);
        var minHit = WindowsShell.HitTest(hwnd, Button(0).X + Px(17), my);
        check("BaseWindow: Windows: the overlay's maximize button answers HTMAXBUTTON, its neighbours do not", maxHit == HtMaxButton && minHit == HtClient, $"{maxHit} {minHit}");
        WindowsShell.MovePointer(Button(2).X, Button(2, 10).Y);
        check("BaseWindow: Windows: the overlay's close button turns red under the pointer",
            Eventually(() => IsRed(WindowsShell.ScreenPixel(Button(2).X, Button(2).Y)), step), Rgb(WindowsShell.ScreenPixel(Button(2).X, Button(2).Y)));
        WindowsShell.MovePointer(Button(0).X, Button(0, 120).Y);
        await Task.Delay(500);

        WindowsShell.Click(Button(1).X, Button(1, 8).Y);
        var maximized = Eventually(() => overlaid.IsMaximized, step);
        WindowsShell.Click(Button(1).X, Button(1, 8).Y);
        var restored = Eventually(() => !overlaid.IsMaximized, step);
        check("BaseWindow: Windows: the overlay's maximize button maximizes, then restores", maximized && restored, $"maximized={maximized} restored={restored}");
        await Task.Delay(500);
        WindowsShell.Click(Button(0).X, Button(0, 8).Y);
        check("BaseWindow: Windows: the overlay's minimize button minimizes", Eventually(() => overlaid.IsMinimized, step));
        overlaid.Restore();
        await Task.Delay(800);
        WindowsShell.Click(Button(2).X, Button(2, 8).Y);
        check("BaseWindow: Windows: the overlay's close button closes it", Eventually(() => closed, step));
        if (!overlaid.IsDestroyed) overlaid.Destroy();
        Menu.SetApplicationMenu(appMenu);
    }

    private static async Task MenuBarKeysAsync(CheckFn check, TimeSpan step)
    {
        var bw = new BaseWindow(new BaseWindowOptions { Title = "Riftway window test: base menu keys", X = 220, Y = 220, Width = 420, Height = 300, AutoHideMenuBar = true });
        bw.SetMenu(new Menu
        {
            new MenuItem { Label = "&File", Submenu = new Menu { new MenuItem { Label = "Quit" } } },
            new MenuItem { Label = "&Window", Submenu = new Menu { new MenuItem { Role = MenuItemRole.Minimize } } },
        });
        bw.Focus();
        var hwnd = bw.GetNativeWindowHandle();
        var foreground = Eventually(() => WindowsShell.Foreground() == hwnd, step);
        await Task.Delay(300);
        var startsHidden = !bw.IsMenuBarVisible;
        WindowsShell.Chord(VkMenu);
        var shown = Eventually(() => bw.IsMenuBarVisible, step);
        WindowsShell.Chord(VkMenu);
        var hidden = Eventually(() => !bw.IsMenuBarVisible, step);
        check("BaseWindow: Windows: a real Alt shows its auto-hidden menu bar, and Alt again hides it",
            foreground && startsHidden && shown && hidden, $"foreground={foreground} startsHidden={startsHidden} shown={shown} hidden={hidden}");

        WindowsShell.Chord(VkMenu, VkF);
        var fileOpen = Eventually(() => bw.IsMenuBarMenuOpen && bw.IsMenuBarVisible, step);
        bw.CancelMenuBarMenu();
        var closedAgain = Eventually(() => !bw.IsMenuBarMenuOpen && !bw.IsMenuBarVisible, step);
        check("BaseWindow: Windows: a real Alt+F opens &File on the auto-hidden bar, which hides again when the menu closes",
            fileOpen && closedAgain, $"open={fileOpen} closedAgain={closedAgain}");

        bw.Focus();
        Eventually(() => WindowsShell.Foreground() == hwnd, step);
        await Task.Delay(300);
        check("BaseWindow: Windows: a window role from its menu bar's accelerator (a real Ctrl+M) acts on it",
            Fires(h => bw.Minimized += h, h => bw.Minimized -= h, () => WindowsShell.Chord(VkControl, VkM), step));
        bw.Destroy();
    }

    private sealed class Counted
    {
        public required BaseWindow Window { get; init; }
        public required nint Hwnd { get; init; }
        public int Closing;
        public int Closed;
    }

    private static Counted Host(string title)
    {
        var window = new BaseWindow(new BaseWindowOptions { Title = title, X = 200, Y = 200, Width = 400, Height = 300 });
        var counted = new Counted { Window = window, Hwnd = window.GetNativeWindowHandle() };
        window.Closing += (_, _) => counted.Closing++;
        window.Closed += (_, _) => counted.Closed++;
        return counted;
    }

    private static string Rgb((byte R, byte G, byte B) c) => $"{c.R},{c.G},{c.B}";
    // The display is colour-managed: test the dominant channel, not exact values.
    private static bool IsBlue((byte R, byte G, byte B) c) => c.B > 120 && c.B > c.R + 50 && c.B > c.G + 30;
    private static bool IsRed((byte R, byte G, byte B) c) => c.R > 150 && c.R > c.G + 80 && c.R > c.B + 80;
    private static bool IsYellow((byte R, byte G, byte B) c) => c.R > 200 && c.G > 200 && c.B < 100;

    private static bool Fires(Action<EventHandler> subscribe, Action<EventHandler> unsubscribe, Action act, TimeSpan timeout)
    {
        using var fired = new ManualResetEventSlim();
        EventHandler handler = (_, _) => fired.Set();
        subscribe(handler);
        try
        {
            act();
            return fired.Wait(timeout);
        }
        finally { unsubscribe(handler); }
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
}
