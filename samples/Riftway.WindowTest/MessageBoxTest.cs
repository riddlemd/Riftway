using System.Runtime.InteropServices;
using Riftway;

/// <summary>Message boxes end to end: each box is read back from the live task dialog and answered as a user would.</summary>
static class MessageBoxTest
{
    internal delegate void CheckFn(string name, bool pass, object? detail = null);

    internal static async Task RunAsync(BrowserWindow win, CheckFn check, TimeSpan step)
    {
        if (OperatingSystem.IsMacOS())
        {
            await MacMessageBoxTest.RunAsync(win, check, step);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            await LinuxMessageBoxTest.RunAsync(win, check, step);
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        var scenarios = new (string Name, Func<Task> Run)[]
        {
            ("owned box", () => OwnedAsync(win, check, step)),
            ("window-less NoLink box", () => NoLinkAsync(win, check, step)),
            ("close button and Esc", () => DismissAsync(win, check, step)),
            ("Signal", () => SignalAsync(check, step)),
            ("second box on one window", () => SecondBoxAsync(win, check, step)),
            ("ShowMessageBoxSync", () => SyncAsync(win, check, step)),
            ("ShowErrorBox", () => ErrorBoxAsync(win, check, step)),
            ("untitled box and icon", () => UntitledAsync(check, step)),
            ("about panel", () => AboutPanelAsync(check, step)),
        };
        foreach (var (name, run) in scenarios)
        {
            try { await run(); }
            catch (Exception e) { check($"message box: {name} ran through", false, e.GetBaseException().Message); }
            finally
            {
                // A box left open would hang the run.
                WinTaskDialog.CloseAll();
                await Task.Delay(300);
            }
        }
        check("TaskDialog comes from comctl32 v6",
            System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                .Any(m => m.ModuleName.Equals("comctl32.dll", StringComparison.OrdinalIgnoreCase) && m.FileName.Contains("common-controls_6595b64144ccf1df_6.", StringComparison.OrdinalIgnoreCase)), null);
        check("CheckboxChecked without CheckboxLabel throws Electron's error",
            Throws<ArgumentException>(() => Dialog.ShowMessageBoxAsync(new MessageBoxOptions { CheckboxChecked = true }), "checkboxChecked requires that checkboxLabel also be passed"), null);
    }

    private static async Task OwnedAsync(BrowserWindow win, CheckFn check, TimeSpan step)
    {
        var owner = win.GetNativeWindowHandle();
        var box = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions
        {
            Title = "Riftway mb rich", Type = MessageBoxType.Warning, Message = "Main message", Detail = "Some detail",
            Buttons = ["Yes", "No", "Maybe"], DefaultId = 1, CheckboxLabel = "Remember",
        });
        var h = WaitFor("Riftway mb rich", step);
        check("message box opens, titled by Title", h != 0, h);
        var els = WinTaskDialog.Elements(h);
        var buttons = WinTaskDialog.Buttons(h);
        check("Detail makes Message the main instruction", Has(els, "MainInstruction", "Main message") && Has(els, "ContentText", "Some detail"), Describe(els));
        check("'Yes' and 'No' become the system's buttons", Has(els, "CommandButton_6", "Yes") && Has(els, "CommandButton_7", "No"), Describe(els));
        check("other labels become command links (id 100 + index)", Has(els, "CommandLink_102", "Maybe") && buttons.Any(b => WinTaskDialog.Plain(b.Text) == "Maybe" && WinTaskDialog.IsCommandLink(b)), Describe(els));
        check("Type shows an icon", els.Any(e => e.AutomationId == "MainIcon"), Describe(els));
        check("CheckboxLabel shows an unchecked checkbox", Has(els, "VerificationCheckBox", "Remember") && WinTaskDialog.IsChecked(h) == false, WinTaskDialog.IsChecked(h));
        check("DefaultId selects its button", buttons.Where(WinTaskDialog.IsDefault).Select(b => WinTaskDialog.Plain(b.Text)).SingleOrDefault() == "No", string.Join(",", buttons.Select(b => $"{b.Text}:{b.Style:X}")));
        check("a box for a window is owned by it", WinTaskDialog.Owner(h) == owner, WinTaskDialog.Owner(h));
        check("its window is disabled while the box shows", !WinTaskDialog.IsEnabled(owner));
        check("an async box runs off the UI thread", WinTaskDialog.ThreadOf(h) != WinTaskDialog.ThreadOf(owner));
        WinTaskDialog.ToggleCheckbox(h);
        check("toggling the checkbox checks it", Eventually(() => WinTaskDialog.IsChecked(h) == true, step));
        // Software that enumerates dialogs can send ids the box never made; Electron ignores those clicks.
        WinTaskDialog.ClickId(h, 105);
        check("a click for an id the box never made is ignored", !Eventually(() => !WinTaskDialog.Exists(h), TimeSpan.FromMilliseconds(700)));
        Click(h, "Maybe");
        var result = await Within(box, step);
        check("clicking a command link answers its index and the checkbox", result is { Response: 2, CheckboxChecked: true }, result);
        check("its window is enabled again once the box closes", Eventually(() => WinTaskDialog.IsEnabled(owner), step));
    }

    private static async Task NoLinkAsync(BrowserWindow win, CheckFn check, TimeSpan step)
    {
        var owner = win.GetNativeWindowHandle();
        var box = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Title = "Riftway mb nolink", Message = "Plain message", Buttons = ["Save", "Don't Save", "Cancel"], NoLink = true });
        var h = WaitFor("Riftway mb nolink", step);
        var els = WinTaskDialog.Elements(h);
        var buttons = WinTaskDialog.Buttons(h);
        check("without Detail the message is the content, not highlighted", Has(els, "ContentText", "Plain message") && !els.Any(e => e.AutomationId == "MainInstruction"), Describe(els));
        check("NoLink: every label a plain custom button, 'Cancel' included", Has(els, "CommandButton_100", "Save") && Has(els, "CommandButton_101", "Don't Save") && Has(els, "CommandButton_102", "Cancel")
            && buttons.Count == 3 && !buttons.Any(WinTaskDialog.IsCommandLink), Describe(els));
        check("Type None shows no icon", !els.Any(e => e.AutomationId == "MainIcon"), Describe(els));
        check("no CheckboxLabel, no checkbox", WinTaskDialog.IsChecked(h) is null);
        check("a box without a window has no owner and leaves windows enabled", WinTaskDialog.Owner(h) == 0 && WinTaskDialog.IsEnabled(owner), WinTaskDialog.Owner(h));
        WinTaskDialog.Escape(h);
        var result = await Within(box, step);
        check("Esc answers the detected cancel id ('Cancel')", result is { Response: 2, CheckboxChecked: false }, result);
    }

    private static async Task DismissAsync(BrowserWindow win, CheckFn check, TimeSpan step)
    {
        var box = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions
        {
            Title = "Riftway mb close", Message = "Close me", Buttons = ["Retry", "Later"], DefaultId = 0, CancelId = 1, CheckboxLabel = "Opt", CheckboxChecked = true,
        });
        var h = WaitFor("Riftway mb close", step);
        check("CheckboxChecked shows the checkbox", WinTaskDialog.IsChecked(h) is not null);
        check("DefaultId on a system button selects it", WinTaskDialog.Buttons(h).Where(WinTaskDialog.IsDefault).Select(b => WinTaskDialog.Plain(b.Text)).SingleOrDefault() == "Retry");
        WinTaskDialog.CloseButton(h);
        var result = await Within(box, step);
        check("the close button answers CancelId; an untouched CheckboxChecked box answers checked", result is { Response: 1, CheckboxChecked: true }, result);

        // IDCANCEL belongs to a 'Cancel' system button when there is one, whatever CancelId says.
        var common = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions { Title = "Riftway mb esc", Message = "Esc", Buttons = ["OK", "Cancel"], CancelId = 0 });
        h = WaitFor("Riftway mb esc", step);
        WinTaskDialog.Escape(h);
        var escaped = await Within(common, step);
        check("Esc with a 'Cancel' system button answers that button", escaped is { Response: 1 }, escaped);
    }

    private static async Task SignalAsync(CheckFn check, TimeSpan step)
    {
        using (var aborted = new CancellationTokenSource())
        {
            aborted.Cancel();
            var early = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Title = "Riftway mb aborted", Buttons = ["A", "B"], DefaultId = 0, CheckboxLabel = "c", CheckboxChecked = true, Signal = aborted.Token });
            check("an already-cancelled Signal answers at once, without a box",
                early.IsCompletedSuccessfully && early.Result is { Response: 1, CheckboxChecked: true } && !Eventually(() => WinTaskDialog.Find("Riftway mb aborted") != 0, TimeSpan.FromMilliseconds(700)),
                early.IsCompletedSuccessfully ? early.Result : early.Status);
        }

        using (var signal = new CancellationTokenSource())
        {
            var box = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Title = "Riftway mb signal", Buttons = ["A", "B"], DefaultId = 0, Signal = signal.Token });
            var h = WaitFor("Riftway mb signal", step);
            check("a Signal box opens", h != 0);
            await signal.CancelAsync();
            var result = await Within(box, step);
            check("cancelling Signal closes the box with the cancel id", result is { Response: 1 } && Eventually(() => !WinTaskDialog.Exists(h), step), result);
        }

        using (var soon = new CancellationTokenSource())
        {
            var box = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Title = "Riftway mb soon", Buttons = ["A", "B", "Cancel"], Signal = soon.Token });
            await soon.CancelAsync();
            var result = await Within(box, step);
            check("cancelling Signal before the box appears still closes it", result is { Response: 2 } && WinTaskDialog.Find("Riftway mb soon") == 0, result);
        }
    }

    private static async Task SecondBoxAsync(BrowserWindow win, CheckFn check, TimeSpan step)
    {
        var owner = win.GetNativeWindowHandle();
        var first = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions { Title = "Riftway mb first", Message = "First", Buttons = ["First", "Second"], DefaultId = 1 });
        var h1 = WaitFor("Riftway mb first", step);
        check("DefaultId on a command link selects it", WinTaskDialog.Buttons(h1).Where(WinTaskDialog.IsDefault).Select(b => (b.Text, WinTaskDialog.IsCommandLink(b))).SingleOrDefault() == ("Second", true));
        var second = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions { Title = "Riftway mb second", Message = "Second", Buttons = ["OK"] });
        var h2 = WaitFor("Riftway mb second", step);
        check("a second box for a window that already shows one opens alongside", h1 != 0 && h2 != 0 && WinTaskDialog.Exists(h1));
        check("the second box is not owned: its window is already disabled", WinTaskDialog.Owner(h1) == owner && WinTaskDialog.Owner(h2) == 0, $"{WinTaskDialog.Owner(h1)} {WinTaskDialog.Owner(h2)}");
        Click(h2, "OK");
        var r2 = await Within(second, step);
        check("the second box answers on its own", r2 is { Response: 0 } && WinTaskDialog.Exists(h1) && !WinTaskDialog.IsEnabled(owner), r2);
        Click(h1, "First");
        var r1 = await Within(first, step);
        check("then the first answers and its window is enabled", r1 is { Response: 0 } && Eventually(() => WinTaskDialog.IsEnabled(owner), step), r1);
    }

    private static async Task SyncAsync(BrowserWindow win, CheckFn check, TimeSpan step)
    {
        var owner = win.GetNativeWindowHandle();
        var sync = Task.Run(() => Dialog.ShowMessageBoxSync(win, new MessageBoxOptions { Title = "Riftway mb sync", Message = "Sync", Buttons = ["OK", "Cancel"] }));
        var h = WaitFor("Riftway mb sync", step);
        check("ShowMessageBoxSync shows an owned box", h != 0 && WinTaskDialog.Owner(h) == owner);
        check("a Sync box runs on the UI thread", WinTaskDialog.ThreadOf(h) == WinTaskDialog.ThreadOf(owner), $"{WinTaskDialog.ThreadOf(h)} vs {WinTaskDialog.ThreadOf(owner)}");
        var query = Task.Run(win.GetTitle);
        var held = await Task.WhenAny(query, Task.Delay(1000)) != query;
        check("UI-thread work waits while a Sync box shows (Electron blocks too)", held, query.Status);
        Click(h, "OK");
        var result = await Within(sync, step);
        check("ShowMessageBoxSync returns the button index", result == 0, result);
        check("UI-thread work resumes once the Sync box closes", await Task.WhenAny(query, Task.Delay(step)) == query);

        using var early = new CancellationTokenSource();
        early.Cancel();
        // A box would close at once on the cancelled token too, so only its cost shows it was made.
        var none = Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var answer = Dialog.ShowMessageBoxSync(new MessageBoxOptions { Title = "Riftway mb sync aborted", Buttons = ["A", "No"], Signal = early.Token });
            return (answer, watch.Elapsed.TotalMilliseconds);
        });
        var quick = await Task.WhenAny(none, Task.Delay(step)) == none ? none.Result : (-1, -1.0);
        check("Sync with an already-cancelled Signal returns the cancel id without a box", quick.Item1 == 1 && quick.Item2 is >= 0 and < 15, quick);
        WinTaskDialog.CloseAll();

        using var signal = new CancellationTokenSource();
        var cancelled = Task.Run(() => Dialog.ShowMessageBoxSync(new MessageBoxOptions { Title = "Riftway mb sync signal", Buttons = ["A", "No"], Signal = signal.Token }));
        h = WaitFor("Riftway mb sync signal", step);
        await signal.CancelAsync();
        var answer = await Within(cancelled, step);
        check("Signal closes a Sync box too, with the cancel id", h != 0 && answer == 1, answer);
    }

    private static async Task ErrorBoxAsync(BrowserWindow win, CheckFn check, TimeSpan step)
    {
        var owner = win.GetNativeWindowHandle();
        var error = Task.Run(() => Dialog.ShowErrorBox("Bad thing", "It broke"));
        var h = WaitFor("Error", step);
        var els = WinTaskDialog.Elements(h);
        check("ShowErrorBox: title 'Error', its title highlighted over its content", h != 0 && Has(els, "MainInstruction", "Bad thing") && Has(els, "ContentText", "It broke"), Describe(els));
        check("ShowErrorBox: the error icon and a lone OK", els.Any(e => e.AutomationId == "MainIcon") && WinTaskDialog.Buttons(h).Count == 1 && Has(els, "CommandButton_1", "OK"), Describe(els));
        check("ShowErrorBox: no owner, on the UI thread once ready", WinTaskDialog.Owner(h) == 0 && WinTaskDialog.ThreadOf(h) == WinTaskDialog.ThreadOf(owner));
        Click(h, "OK");
        check("ShowErrorBox returns once OK is clicked", await Task.WhenAny(error, Task.Delay(step)) == error);
    }

    private static async Task UntitledAsync(CheckFn check, TimeSpan step)
    {
        var appName = RiftwayApp.CurrentOrNull!.Name;
        var box = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Message = "Untitled" });
        var h = WaitFor(appName, step);
        check("an empty Title uses the app's name", h != 0, appName);
        var els = WinTaskDialog.Elements(h);
        check("no Buttons shows the dialog's own OK", WinTaskDialog.Buttons(h).Count == 1 && Has(els, "CommandButton_1", "OK"), Describe(els));
        Click(h, "OK");
        var result = await Within(box, step);
        check("its OK answers the cancel id (0)", result is { Response: 0 }, result);

        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0, 0, 255, 255);
        var icon = NativeImage.CreateFromBitmap(pixels, new NativeImageBitmapOptions { Width = 16, Height = 16 });
        var iconBox = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Title = "Riftway mb icon", Message = "Icon", Icon = icon });
        h = WaitFor("Riftway mb icon", step);
        check("Icon shows even with Type None", WinTaskDialog.Elements(h).Any(e => e.AutomationId == "MainIcon"));
        WinTaskDialog.Escape(h);
        check("the icon box closes", await Within(iconBox, step) is { Response: 0 });
    }

    private static Task AboutPanelAsync(CheckFn check, TimeSpan step)
    {
        var app = RiftwayApp.CurrentOrNull!;
        var exe = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
        app.SetAboutPanelOptions(new AboutPanelOptions());
        app.ShowAboutPanel();
        var h = WaitFor(app.Name, step);
        var els = WinTaskDialog.Elements(h);
        check("ShowAboutPanel: an unowned information box titled by the app's name, with a lone OK, as Electron's",
            h != 0 && WinTaskDialog.Owner(h) == 0 && els.Any(e => e.AutomationId == "MainIcon") && WinTaskDialog.Buttons(h).Count == 1 && Has(els, "CommandButton_1", "OK"),
            Describe(els));
        check("ShowAboutPanel without options shows the exe's FileDescription and ProductVersion",
            Content(els) == $"{exe.FileDescription}\n{exe.ProductVersion}", $"{Content(els)} vs {exe.FileDescription}/{exe.ProductVersion}");
        Click(h, "OK");
        check("the about box closes on OK", Eventually(() => !WinTaskDialog.Exists(h), step));

        app.SetAboutPanelOptions(new AboutPanelOptions { ApplicationName = "Riftway About", ApplicationVersion = "9.9", Copyright = "(c) Riftway", Credits = "credits", Website = "https://riftway.test" });
        app.ShowAboutPanel();
        h = WaitFor(app.Name, step);
        els = WinTaskDialog.Elements(h);
        // Electron's Windows panel reads only these four of the string options; the website is dropped.
        check("ShowAboutPanel shows the name, version, copyright and credits set, one per line",
            Content(els) == "Riftway About\n9.9\n(c) Riftway\ncredits", Content(els));
        Click(h, "OK");
        check("the second about box closes on OK", Eventually(() => !WinTaskDialog.Exists(h), step));
        app.SetAboutPanelOptions(new AboutPanelOptions());
        return Task.CompletedTask;
    }

    // The box's content text, line breaks normalised and the trailing one dropped.
    private static string Content(List<WinTaskDialog.Element> elements)
        => (elements.Where(e => e.AutomationId == "ContentText").Select(e => e.Name).FirstOrDefault() ?? "").ReplaceLineEndings("\n").TrimEnd('\n');

    private static nint WaitFor(string title, TimeSpan step)
    {
        nint h = 0;
        // The buttons appear a moment after the window.
        Eventually(() => (h = WinTaskDialog.Find(title)) != 0 && WinTaskDialog.Buttons(h).Count > 0, step);
        Thread.Sleep(200);
        return h;
    }

    private static void Click(nint dialog, string label)
    {
        if (WinTaskDialog.Buttons(dialog).FirstOrDefault(b => WinTaskDialog.Plain(b.Text) == label) is { Hwnd: not 0 } button)
            WinTaskDialog.Click(dialog, button.Hwnd);
    }

    private static bool Has(List<WinTaskDialog.Element> elements, string automationId, string name)
        => elements.Any(e => e.AutomationId == automationId && e.Name == name);

    private static string Describe(List<WinTaskDialog.Element> elements)
        => string.Join("; ", elements.Where(e => e.AutomationId.Length > 0).Select(e => $"{e.AutomationId}={e.Name}"));

    private static async Task<T?> Within<T>(Task<T> task, TimeSpan step) where T : class => await Task.WhenAny(task, Task.Delay(step)) == task ? task.Result : null;

    private static async Task<int?> Within(Task<int> task, TimeSpan step) => await Task.WhenAny(task, Task.Delay(step)) == task ? task.Result : null;

    private static bool Throws<TException>(Action act, string? message = null) where TException : Exception
    {
        try { act(); return false; }
        catch (TException e) { return message is null || e.Message.StartsWith(message, StringComparison.Ordinal); }
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
/// <summary>Reads a live task dialog back and answers it, as a user would: child windows for its buttons and checkbox,
/// UI Automation for its text. Windows only; every call is safe on a box that has gone.</summary>
static unsafe class WinTaskDialog
{
    internal readonly record struct Child(nint Hwnd, string Class, string Text, int Id, uint Style);

    internal readonly record struct Element(string AutomationId, string Name, int ControlType, int Toggle);

    /// <summary>This process's visible top-level <c>#32770</c> titled <paramref name="title"/>, or 0.</summary>
    internal static nint Find(string title)
    {
        nint hwnd = 0;
        while ((hwnd = FindWindowExW(0, hwnd, "#32770", title)) != 0)
        {
            uint pid;
            _ = GetWindowThreadProcessId(hwnd, &pid);
            if (pid == (uint)Environment.ProcessId && IsWindowVisible(hwnd) != 0) return hwnd;
        }
        return 0;
    }

    /// <summary>Every top-level <c>#32770</c> of this process, visible or not.</summary>
    internal static List<nint> All()
    {
        var found = new List<nint>();
        nint hwnd = 0;
        while ((hwnd = FindWindowExW(0, hwnd, "#32770", null)) != 0)
        {
            uint pid;
            _ = GetWindowThreadProcessId(hwnd, &pid);
            if (pid == (uint)Environment.ProcessId) found.Add(hwnd);
        }
        return found;
    }

    [ThreadStatic] private static List<Child>? t_children;

    /// <summary>The box's descendant windows.</summary>
    internal static List<Child> Children(nint dialog)
    {
        t_children = [];
        _ = EnumChildWindows(dialog, &OnChild, 0);
        var list = t_children;
        t_children = null;
        return list;
    }

    [UnmanagedCallersOnly]
    private static int OnChild(nint hwnd, nint _)
    {
        var cls = stackalloc char[256];
        var text = stackalloc char[512];
        var clsLength = GetClassNameW(hwnd, cls, 256);
        var textLength = GetWindowTextW(hwnd, text, 512);
        t_children!.Add(new Child(hwnd, new string(cls, 0, clsLength), new string(text, 0, textLength), GetDlgCtrlID(hwnd), (uint)GetWindowLongW(hwnd, -16)));
        return 1;
    }

    /// <summary>The Button children: push buttons, command links and the checkbox.</summary>
    internal static List<Child> Buttons(nint dialog) => [.. Children(dialog).Where(c => c.Class == "Button")];

    /// <summary>Button text without the access key marker.</summary>
    internal static string Plain(string text) => text.Replace("&", "", StringComparison.Ordinal);

    internal static bool IsCommandLink(Child c) => (c.Style & 0xF) is 0xE or 0xF; // BS_COMMANDLINK, BS_DEFCOMMANDLINK

    internal static bool IsDefault(Child c) => (c.Style & 0xF) is 0x1 or 0xF; // BS_DEFPUSHBUTTON, BS_DEFCOMMANDLINK

    /// <summary>The checkbox (a DirectUI element, not a window): its toggle state, or null when the box has none.</summary>
    /// <remarks>UI Automation reports a checkbox that started checked as unchecked until it is first toggled.</remarks>
    internal static bool? IsChecked(nint dialog)
        => Elements(dialog).Where(e => e.AutomationId == "VerificationCheckBox").Select(e => (bool?)(e.Toggle == 1)).FirstOrDefault();

    /// <summary>Toggles the checkbox through UI Automation, as a click does.</summary>
    internal static void ToggleCheckbox(nint dialog) => Elements(dialog, toggle: "VerificationCheckBox");

    /// <summary>The window holding keyboard focus on the box's thread.</summary>
    internal static nint Focused(nint dialog)
    {
        var thread = GetWindowThreadProcessId(dialog, null);
        var info = new GuiThreadInfo { Size = (uint)sizeof(GuiThreadInfo) };
        return GetGUIThreadInfo(thread, &info) != 0 ? info.Focus : 0;
    }

    internal static nint Owner(nint hwnd) => GetWindow(hwnd, 4 /* GW_OWNER */);

    internal static uint ThreadOf(nint hwnd) => GetWindowThreadProcessId(hwnd, null);

    internal static bool IsEnabled(nint hwnd) => IsWindowEnabled(hwnd) != 0;

    internal static bool Exists(nint hwnd) => IsWindow(hwnd) != 0;

    /// <summary>Clicks a button as the mouse would: activates the box, then BM_CLICK.</summary>
    internal static void Click(nint dialog, nint button)
    {
        _ = SetForegroundWindow(dialog);
        _ = PostMessageW(button, 0xF5 /* BM_CLICK */, 0, 0);
    }

    /// <summary>Presses Esc in the box: the dialog manager turns it into IDCANCEL.</summary>
    internal static void Escape(nint dialog)
    {
        var target = Focused(dialog) is var f and not 0 ? f : dialog;
        _ = PostMessageW(target, 0x0100 /* WM_KEYDOWN */, 0x1B, 0x00010001);
        _ = PostMessageW(target, 0x0101 /* WM_KEYUP */, 0x1B, unchecked((nint)0xC0010001));
    }

    /// <summary>Clicks the button with command <paramref name="id"/> from outside: TDM_CLICK_BUTTON.</summary>
    internal static void ClickId(nint dialog, int id) => _ = PostMessageW(dialog, 0x0466 /* TDM_CLICK_BUTTON */, id, 0);

    /// <summary>Clicks the title bar's close button: SC_CLOSE.</summary>
    internal static void CloseButton(nint dialog) => _ = PostMessageW(dialog, 0x0112 /* WM_SYSCOMMAND */, 0xF060 /* SC_CLOSE */, 0);

    /// <summary>Closes every box this process still shows; for cleanup after a failed step.</summary>
    internal static void CloseAll()
    {
        foreach (var h in All()) _ = PostMessageW(h, 0x0010 /* WM_CLOSE */, 0, 0);
    }

    private static readonly Guid s_clsidAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid s_iidAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    private static readonly Guid s_iidTogglePattern = new("94CF8058-9B8D-4AB9-8BFD-4CD0A33C8C70");

    /// <summary>Every UI Automation element under the box: automation id, name, control type and toggle state (-1 for
    /// none); toggles the one whose automation id is <paramref name="toggle"/>.</summary>
    internal static List<Element> Elements(nint dialog, string? toggle = null)
    {
        var elements = new List<Element>();
        void* automation;
        var clsid = s_clsidAutomation;
        var iid = s_iidAutomation;
        if (CoCreateInstance(&clsid, null, 1, &iid, &automation) < 0) return elements;
        void* root = null, condition = null, all = null;
        try
        {
            if (((delegate* unmanaged<void*, nint, void**, int>)Slot(automation, 6))(automation, dialog, &root) < 0) return elements; // ElementFromHandle
            if (((delegate* unmanaged<void*, void**, int>)Slot(automation, 21))(automation, &condition) < 0) return elements; // CreateTrueCondition
            if (((delegate* unmanaged<void*, int, void*, void**, int>)Slot(root, 6))(root, 4 /* TreeScope_Descendants */, condition, &all) < 0) return elements; // FindAll
            int length;
            ((delegate* unmanaged<void*, int*, int>)Slot(all, 3))(all, &length);
            for (var i = 0; i < length; i++)
            {
                void* element;
                if (((delegate* unmanaged<void*, int, void**, int>)Slot(all, 4))(all, i, &element) < 0) continue;
                int type;
                if (((delegate* unmanaged<void*, int*, int>)Slot(element, 21))(element, &type) < 0) type = 0; // get_CurrentControlType
                var id = Bstr(element, 29); // get_CurrentAutomationId
                var state = -1;
                void* pattern = null;
                var toggleIid = s_iidTogglePattern;
                if (((delegate* unmanaged<void*, int, Guid*, void**, int>)Slot(element, 14))(element, 10015 /* UIA_TogglePatternId */, &toggleIid, &pattern) >= 0 && pattern is not null)
                {
                    if (id == toggle) ((delegate* unmanaged<void*, int>)Slot(pattern, 3))(pattern); // Toggle
                    int value;
                    if (((delegate* unmanaged<void*, int*, int>)Slot(pattern, 4))(pattern, &value) >= 0) state = value; // get_CurrentToggleState
                    Release(pattern);
                }
                elements.Add(new Element(id, Bstr(element, 23), type, state)); // get_CurrentName
                Release(element);
            }
        }
        finally
        {
            Release(all);
            Release(condition);
            Release(root);
            Release(automation);
        }
        return elements;
    }

    private static string Bstr(void* element, int slot)
    {
        nint value;
        if (((delegate* unmanaged<void*, nint*, int>)Slot(element, slot))(element, &value) < 0 || value == 0) return "";
        var text = Marshal.PtrToStringBSTR(value);
        Marshal.FreeBSTR(value);
        return text;
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com)
    {
        if (com is not null) ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* pid);
    [DllImport("user32.dll")] private static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern int IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern int IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> callback, nint param);
    [DllImport("user32.dll")] private static extern int GetClassNameW(nint hwnd, char* name, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(nint hwnd, char* text, int max);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern int GetGUIThreadInfo(uint thread, GuiThreadInfo* info);
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
}
