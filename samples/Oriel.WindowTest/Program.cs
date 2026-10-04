using Oriel.Cef.Platform;
using System.ComponentModel;
using Oriel;

// End-to-end BrowserWindow check, driven from C#: each step issues a command from a worker thread
// and waits for both the event and the state. Exit code = failures (-1 when the run never finishes).
var app = OrielApp.Create("Oriel.WindowTest");
var failures = -1;
var results = new List<string>();
var step = TimeSpan.FromSeconds(5);
var simpleReady = false;
BrowserWindow? childAlive = null;

void Check(string name, bool pass, object? detail = null)
{
    // Printed as it happens: on macOS the CEF shutdown watchdog can end the process before Run returns.
    var line = $"{(pass ? "PASS" : "FAIL")}  {name}  [{detail}]";
    results.Add(line);
    Console.WriteLine(line);
}

// Waits for `raise` to fire `event` once `act` runs; the state check follows separately.
static bool Fires(Action<EventHandler> subscribe, Action<EventHandler> unsubscribe, Action act, TimeSpan timeout)
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

static bool Eventually(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition()) return true;
        Thread.Sleep(50);
    }
    return condition();
}

async Task RunAsync(BrowserWindow win, BrowserWindow simple)
{
    await Task.Delay(1000);

    // Lookup.
    Check("ids are unique", win.Id != simple.Id, $"{win.Id} {simple.Id}");
    Check("FromId", BrowserWindow.FromId(win.Id) == win && BrowserWindow.FromId(-1) is null);
    Check("GetAllWindows", BrowserWindow.GetAllWindows().Count == 2, BrowserWindow.GetAllWindows().Count);
    Check("FromWebContents", BrowserWindow.FromWebContents(win.WebContents) == win);

    // Page title drives the window title unless PageTitleUpdated is cancelled; LoadFile, DidFinishLoad, GetUrl.
    var page = Path.Combine(Path.GetTempPath(), "oriel-window-test.html");
    File.WriteAllText(page, "<!doctype html><title>Page title</title><p>loaded</p>");
    string? seenTitle = null;
    EventHandler<PageTitleUpdatedEventArgs> onTitle = (_, e) => seenTitle = e.Title;
    win.PageTitleUpdated += onTitle;
    var loaded = Fires(h => win.WebContents.DidFinishLoad += h, h => win.WebContents.DidFinishLoad -= h,
        () => win.WebContents.LoadFile(page, new Dictionary<string, string> { ["q"] = "1 2" }, "top"), step);
    Check("LoadFile raises DidFinishLoad", loaded);
    Check("GetUrl reflects LoadFile (query, hash)", win.WebContents.GetUrl().EndsWith("oriel-window-test.html?q=1%202#top", StringComparison.Ordinal), win.WebContents.GetUrl());
    Check("PageTitleUpdated raised", Eventually(() => seenTitle == "Page title", step), seenTitle);
    Check("window title follows the page", Eventually(() => win.GetTitle() == "Page title", step), win.GetTitle());
    win.PageTitleUpdated -= onTitle;

    EventHandler<PageTitleUpdatedEventArgs> keep = (_, e) => e.Cancel = true;
    win.PageTitleUpdated += keep;
    File.WriteAllText(page, "<!doctype html><title>Ignored title</title>");
    Check("Reload raises DidFinishLoad",
        Fires(h => win.WebContents.DidFinishLoad += h, h => win.WebContents.DidFinishLoad -= h, win.WebContents.Reload, step));
    Check("cancelled PageTitleUpdated keeps the window title", Eventually(() => win.WebContents.GetTitle() == "Ignored title", step) && win.GetTitle() == "Page title", $"{win.WebContents.GetTitle()} / {win.GetTitle()}");
    win.PageTitleUpdated -= keep;

    // Content size, runtime limits, colour.
    win.SetContentSize(560, 420);
    Check("content size", Eventually(() => win.GetContentSize() == (560, 420), step), win.GetContentSize());
    Check("content fits inside the window", win.GetBounds() is var ob && win.GetContentBounds() is var cb && cb.Width <= ob.Width && cb.Height <= ob.Height, $"{win.GetBounds()} {win.GetContentBounds()}");
    win.SetResizable(false);
    Check("SetResizable(false)", !win.IsResizable);
    win.SetResizable(true);
    win.SetMinimumSize(500, 350);
    Check("runtime minimum size", win.GetMinimumSize() == (500, 350), win.GetMinimumSize());
    win.SetSize(300, 200);
    Check("runtime minimum size enforced", Eventually(() => win.GetSize().Width >= 500, step), win.GetSize());
    win.SetMinimumSize(400, 300);
    // Not 700x500: the next section must see a real resize.
    win.SetSize(660, 470);
    win.SetBackgroundColor("#202830");
    Check("background colour", win.GetBackgroundColor() == "#202830", win.GetBackgroundColor());

    win.SetTitle("Oriel window test: retitled");
    Check("title", Eventually(() => win.GetTitle() == "Oriel window test: retitled", step), win.GetTitle());

    var moved = Fires(h => win.Moved += h, h => win.Moved -= h, () => win.SetPosition(120, 140), step);
    Check("SetPosition raises Moved", moved);
    var resized = Fires(h => win.Resized += h, h => win.Resized -= h, () => win.SetSize(700, 500), step);
    Check("SetSize raises Resized", resized);
    Check("bounds read back", Eventually(() => win.GetBounds() is { Width: 700, Height: 500 }, step), win.GetBounds());

    win.SetSize(200, 150);
    Check("minimum size enforced", Eventually(() => win.GetBounds().Width >= 400, step), win.GetBounds());
    win.SetSize(700, 500);

    win.SetAlwaysOnTop(true);
    Check("always on top", Eventually(() => win.IsAlwaysOnTop, step), win.IsAlwaysOnTop);
    win.SetAlwaysOnTop(false);
    Check("always on top cleared", Eventually(() => !win.IsAlwaysOnTop, step), win.IsAlwaysOnTop);

    Check("Maximize raises Maximized", Fires(h => win.Maximized += h, h => win.Maximized -= h, win.Maximize, step));
    Check("IsMaximized", Eventually(() => win.IsMaximized, step), win.IsMaximized);
    Check("Unmaximize raises Unmaximized", Fires(h => win.Unmaximized += h, h => win.Unmaximized -= h, win.Unmaximize, step));
    Check("not maximized", Eventually(() => !win.IsMaximized, step), win.IsMaximized);

    Check("Minimize raises Minimized", Fires(h => win.Minimized += h, h => win.Minimized -= h, win.Minimize, step));
    Check("IsMinimized", Eventually(() => win.IsMinimized, step), win.IsMinimized);
    Check("Restore raises Restored", Fires(h => win.Restored += h, h => win.Restored -= h, win.Restore, step));
    Check("not minimized", Eventually(() => !win.IsMinimized, step), win.IsMinimized);

    Check("Hide raises Hidden", Fires(h => win.Hidden += h, h => win.Hidden -= h, win.Hide, step));
    Check("hidden", Eventually(() => !win.IsVisible, step), win.IsVisible);
    Check("Show raises Shown", Fires(h => win.Shown += h, h => win.Shown -= h, win.Show, step));
    Check("visible", Eventually(() => win.IsVisible, step), win.IsVisible);

    Check("SetFullScreen(true) raises EnteredFullScreen",
        Fires(h => win.EnteredFullScreen += h, h => win.EnteredFullScreen -= h, () => win.SetFullScreen(true), step * 2));
    Check("IsFullScreen", Eventually(() => win.IsFullScreen, step), win.IsFullScreen);
    Check("SetFullScreen(false) raises LeftFullScreen",
        Fires(h => win.LeftFullScreen += h, h => win.LeftFullScreen -= h, () => win.SetFullScreen(false), step * 2));
    Check("not full screen", Eventually(() => !win.IsFullScreen, step), win.IsFullScreen);

    // Window managers restore the bounds after reporting the state change.
    Eventually(() => win.GetBounds() is { Width: 700, Height: 500 }, step);
    var before = win.GetBounds();
    Check("SetSimpleFullScreen(true) raises EnteredFullScreen",
        Fires(h => win.EnteredFullScreen += h, h => win.EnteredFullScreen -= h, () => win.SetSimpleFullScreen(true), step * 2));
    Check("IsSimpleFullScreen", Eventually(() => win.IsSimpleFullScreen, step), win.IsSimpleFullScreen);
    var filled = win.GetBounds();
    Check("simple full screen fills the display", filled.Width > before.Width && filled.Height > before.Height, filled);
    if (OperatingSystem.IsMacOS())
        Check("simple full screen is not native full screen (no new Space)", !win.IsFullScreen, win.IsFullScreen);
    Check("SetSimpleFullScreen(false) raises LeftFullScreen",
        Fires(h => win.LeftFullScreen += h, h => win.LeftFullScreen -= h, () => win.SetSimpleFullScreen(false), step * 2));
    Check("simple full screen restores the bounds", Eventually(() => win.GetBounds() == before, step), $"{win.GetBounds()} vs {before}");

    // A window created with SimpleFullScreen routes SetFullScreen to simple full screen on macOS.
    simple.Show();
    simple.SetFullScreen(true);
    Check("SimpleFullScreen option: SetFullScreen reports full screen", Eventually(() => simple.IsFullScreen, step), simple.IsFullScreen);
    if (OperatingSystem.IsMacOS())
        Check("SimpleFullScreen option: uses simple full screen on macOS", simple.IsSimpleFullScreen, simple.IsSimpleFullScreen);
    simple.SetFullScreen(false);
    Check("SimpleFullScreen option: SetFullScreen(false) leaves it", Eventually(() => !simple.IsFullScreen, step), simple.IsFullScreen);
    Check("ReadyToShow raised for a hidden window", Eventually(() => simpleReady, step));
    simple.SetClosable(false);
    Check("SetClosable(false)", !simple.IsClosable);
    var simpleClosing = 0;
    simple.Closing += (_, _) => simpleClosing++;
    using (var simpleClosed = new ManualResetEventSlim())
    {
        simple.Closed += (_, _) => simpleClosed.Set();
        simple.Destroy();
        Check("Destroy closes a non-closable window", simpleClosed.Wait(step));
        Check("Destroy skips Closing", simpleClosing == 0, simpleClosing);
    }
    Check("GetAllWindows after Destroy", BrowserWindow.GetAllWindows().Count == 1, BrowserWindow.GetAllWindows().Count);

    await WebPreferencesAsync(win);
    await ParentAndModalAsync(win);
    await MenusAsync(win);

    // Closing cancelled once: the window must survive the first close.
    var closingCount = 0;
    EventHandler<CancelEventArgs> veto = (_, e) => { closingCount++; e.Cancel = true; };
    win.Closing += veto;
    win.Close();
    await Task.Delay(1500);
    Check("Closing can cancel", closingCount == 1 && !win.IsDestroyed, $"closing={closingCount} destroyed={win.IsDestroyed}");
    win.Closing -= veto;

    failures = results.Count(r => r.StartsWith("FAIL", StringComparison.Ordinal));
    Console.WriteLine($"RESULT: {failures} failure(s) before the final close");
    // Not closable by the user, but code can still close it.
    win.SetClosable(false);
    using var closed = new ManualResetEventSlim();
    win.Closed += (_, _) => closed.Set();
    using var childClosed = new ManualResetEventSlim();
    childAlive?.Closed += (_, _) => childClosed.Set();
    win.Close();
    Check("Close raises Closed", closed.Wait(step));
    Check("closing the parent closes its child", childClosed.Wait(step));
    failures = results.Count(r => r.StartsWith("FAIL", StringComparison.Ordinal));
}

void PressF12(BrowserWindow w)
{
    w.Focus();
    Thread.Sleep(300);
    w.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "F12"));
    w.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "F12"));
}

async Task<object?> Js(BrowserWindow w, string code)
{
    var task = w.WebContents.ExecuteJavaScriptAsync(code);
    if (await Task.WhenAny(task, Task.Delay(step)) != task) return "timeout";
    return task.Result?.ToJsonString();
}

async Task WebPreferencesAsync(BrowserWindow win)
{
    // ExecuteJavaScriptAsync: values, promises, undefined, exceptions.
    Check("ExecuteJavaScript returns a value", await Js(win, "1 + 2") is "3", await Js(win, "1 + 2"));
    Check("ExecuteJavaScript awaits a promise", await Js(win, "Promise.resolve('done')") is "\"done\"");
    Check("ExecuteJavaScript: undefined is null", await Js(win, "undefined") is null);
    var threw = false;
    try { await win.WebContents.ExecuteJavaScriptAsync("throw new Error('boom')"); }
    catch (JavaScriptException e) { threw = e.Message.Contains("boom", StringComparison.Ordinal); }
    Check("ExecuteJavaScript surfaces exceptions", threw);

    // Zoom, set at runtime: devicePixelRatio scales with it.
    var dpr = double.Parse((await Js(win, "devicePixelRatio"))!.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
    win.WebContents.SetZoomFactor(2.0);
    Check("SetZoomFactor / GetZoomFactor", Eventually(() => Math.Abs(win.WebContents.GetZoomFactor() - 2.0) < 0.01, step), win.WebContents.GetZoomFactor());
    Check("zoom reaches the page", Eventually(() => Js(win, "devicePixelRatio").Result is string r && Math.Abs(double.Parse(r, System.Globalization.CultureInfo.InvariantCulture) / dpr - 2.0) < 0.01, step));
    win.WebContents.SetZoomLevel(0);
    Check("SetZoomLevel(0) resets", Eventually(() => win.WebContents.GetZoomLevel() == 0, step), win.WebContents.GetZoomLevel());

    // DevTools.
    win.WebContents.OpenDevTools();
    Check("OpenDevTools", Eventually(() => win.WebContents.IsDevToolsOpened(), step));
    win.WebContents.CloseDevTools();
    Check("CloseDevTools", Eventually(() => !win.WebContents.IsDevToolsOpened(), step));
    PressF12(win);
    Check("F12 opens DevTools (Chrome's shortcut)", Eventually(() => win.WebContents.IsDevToolsOpened(), step));
    win.WebContents.CloseDevTools();
    Eventually(() => !win.WebContents.IsDevToolsOpened(), step);

    // SendInputEvent: a typed character reaches a focused input; modifiers reach key listeners.
    await Js(win, "document.body.innerHTML = '<input id=k>'; window.keys = []; document.getElementById('k').focus(); addEventListener('keydown', e => keys.push(e.key + (e.shiftKey ? ' shift' : '')))");
    win.Focus();
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.Char, "x"));
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "Enter") { Modifiers = InputModifiers.Shift });
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "Enter") { Modifiers = InputModifiers.Shift });
    Check("SendInputEvent Char types into the page", Eventually(() => Js(win, "document.getElementById('k').value").Result is "\"x\"", step), Js(win, "document.getElementById('k').value").Result);
    Check("SendInputEvent KeyDown carries key and modifiers", Eventually(() => Js(win, "keys.join()").Result is "\"Enter shift\"", step), Js(win, "keys.join()").Result);

    // A window built with non-default preferences.
    var dir = Path.Combine(Path.GetTempPath(), "oriel-window-test-prefs");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "dot.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
    File.WriteAllText(Path.Combine(dir, "prefs.html"), """
        <!doctype html><title>prefs</title>
        <p id=p>text</p><code id=c>code</code>
        <img id=img src="dot.png"><textarea id=t></textarea>
        <div><span id=narrow>iiiiiiiiii</span></div><div><span id=wide>MMMMMMMMMM</span></div>
        """);
    var monospace = OperatingSystem.IsMacOS() ? "Menlo" : OperatingSystem.IsWindows() ? "Consolas" : "Liberation Mono";
    var prefs = new BrowserWindow(new BrowserWindowOptions
    {
        Title = "Oriel window test: preferences",
        Width = 500,
        Height = 400,
        WebPreferences = new WebPreferences
        {
            DevTools = false,
            ZoomFactor = 1.5,
            Images = false,
            WebGL = false,
            TextAreasAreResizable = false,
            DefaultFontSize = 20,
            DefaultMonospaceFontSize = 17,
            DisableDialogs = true,
            DefaultFontFamily = new DefaultFontFamily { Standard = monospace },
        },
    });
    var prefsLoaded = Fires(h => prefs.WebContents.DidFinishLoad += h, h => prefs.WebContents.DidFinishLoad -= h,
        () => prefs.WebContents.LoadFile(Path.Combine(dir, "prefs.html")), step);
    Check("preferences window loads", prefsLoaded);
    Check("ZoomFactor preference", Eventually(() => Math.Abs(prefs.WebContents.GetZoomFactor() - 1.5) < 0.01, step), prefs.WebContents.GetZoomFactor());
    Check("ZoomFactor preference reaches the page", Js(prefs, "devicePixelRatio").Result is string pr && Math.Abs(double.Parse(pr, System.Globalization.CultureInfo.InvariantCulture) / dpr - 1.5) < 0.01, Js(prefs, "devicePixelRatio").Result);
    Check("Images = false", await Js(prefs, "document.getElementById('img').naturalWidth") is "0", await Js(prefs, "document.getElementById('img').naturalWidth"));
    Check("WebGL = false", await Js(prefs, "document.createElement('canvas').getContext('webgl') === null") is "true");
    Check("TextAreasAreResizable = false", await Js(prefs, "getComputedStyle(document.getElementById('t')).resize") is "\"none\"", await Js(prefs, "getComputedStyle(document.getElementById('t')).resize"));
    Check("DefaultFontSize", await Js(prefs, "getComputedStyle(document.getElementById('p')).fontSize") is "\"20px\"", await Js(prefs, "getComputedStyle(document.getElementById('p')).fontSize"));
    Check("DefaultMonospaceFontSize", await Js(prefs, "getComputedStyle(document.getElementById('c')).fontSize") is "\"17px\"", await Js(prefs, "getComputedStyle(document.getElementById('c')).fontSize"));
    Check("DefaultFontFamily.Standard", await Js(prefs, "document.getElementById('narrow').offsetWidth === document.getElementById('wide').offsetWidth") is "true",
        await Js(prefs, "[document.getElementById('narrow').offsetWidth, document.getElementById('wide').offsetWidth].join()"));
    Check("DisableDialogs", await Js(prefs, "[confirm('a'), prompt('b'), alert('c') === undefined].join()") is "\"false,,true\"", await Js(prefs, "[confirm('a'), prompt('b'), alert('c') === undefined].join()"));
    prefs.WebContents.OpenDevTools();
    await Task.Delay(1000);
    Check("DevTools = false blocks OpenDevTools", !prefs.WebContents.IsDevToolsOpened());
    PressF12(prefs);
    await Task.Delay(1500);
    Check("DevTools = false blocks F12", !prefs.WebContents.IsDevToolsOpened());

    var noJs = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: no JS", Width = 300, Height = 200, WebPreferences = new WebPreferences { Javascript = false } });
    File.WriteAllText(Path.Combine(dir, "nojs.html"), "<!doctype html><title>before</title><script>document.title = 'after'</script>");
    var noJsLoaded = Fires(h => noJs.WebContents.DidFinishLoad += h, h => noJs.WebContents.DidFinishLoad -= h,
        () => noJs.WebContents.LoadFile(Path.Combine(dir, "nojs.html")), step);
    await Task.Delay(500);
    Check("Javascript = false: page scripts do not run", noJsLoaded && noJs.WebContents.GetTitle() == "before", noJs.WebContents.GetTitle());

    foreach (var w in new[] { prefs, noJs })
    {
        using var gone = new ManualResetEventSlim();
        w.Closed += (_, _) => gone.Set();
        w.Close();
        gone.Wait(step);
    }
}

async Task ParentAndModalAsync(BrowserWindow win)
{
    var threw = false;
    try { _ = new BrowserWindow(new BrowserWindowOptions { Modal = true, Show = false }); }
    catch (ArgumentException) { threw = true; }
    Check("Modal without Parent throws", threw);
    foreach (var (name, unsupported) in new[] { ("MinimumFontSize", new WebPreferences { MinimumFontSize = 12 }), ("DefaultEncoding", new WebPreferences { DefaultEncoding = "UTF-8" }) })
    {
        var rejected = false;
        try { _ = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = unsupported }); }
        catch (NotSupportedException) { rejected = true; }
        Check($"{name} throws NotSupportedException", rejected);
    }

    var child = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: child", Width = 300, Height = 200, Parent = win });
    Check("GetParentWindow", child.GetParentWindow() == win && win.GetParentWindow() is null);
    Check("GetChildWindows", win.GetChildWindows() is [var only] && only == child, win.GetChildWindows().Count);
    Check("child is not modal", !child.IsModal);
    if (OperatingSystem.IsMacOS())
    {
        // AppKit child windows follow their parent; an unparented window stays put.
        await Task.Delay(300);
        var (parentBefore, childBefore) = (win.GetBounds(), child.GetBounds());
        win.SetPosition(parentBefore.X + 40, parentBefore.Y + 30);
        Check("child moves with its parent (macOS)", Eventually(() => child.GetBounds() is var c && c.X == childBefore.X + 40 && c.Y == childBefore.Y + 30, step), $"{childBefore} -> {child.GetBounds()}");
        win.SetPosition(parentBefore.X, parentBefore.Y);
    }

    var modal = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: modal", Width = 300, Height = 200, Parent = win, Modal = true });
    Check("IsModal", modal.IsModal);
    await Task.Delay(500);
    win.Focus();
    await Task.Delay(800);
    // Linux window managers may ignore window-modal hints (openbox does); a known gap there.
    if (!OperatingSystem.IsLinux())
        Check("a modal window keeps focus from its parent", !win.IsFocused && modal.IsFocused, $"parent={win.IsFocused} modal={modal.IsFocused}");
    child.Focus();
    Check("a sibling of the modal can still take focus", Eventually(() => child.IsFocused, step), $"child={child.IsFocused}");
    Check("GetChildWindows lists both", win.GetChildWindows().Count == 2, win.GetChildWindows().Count);
    using (var modalClosed = new ManualResetEventSlim())
    {
        modal.Closed += (_, _) => modalClosed.Set();
        modal.Close();
        Check("modal closes", modalClosed.Wait(step));
    }
    Check("closed child leaves GetChildWindows", win.GetChildWindows() is [var left] && left == child, win.GetChildWindows().Count);
    await Task.Delay(200);
    childAlive = child;
}

async Task MenusAsync(BrowserWindow win)
{
    var defaultMenu = Menu.GetApplicationMenu();
    if (OperatingSystem.IsMacOS())
        Check("macOS gets a default application menu", defaultMenu?.Items.Select(i => i.Role).SequenceEqual(
            [MenuItemRole.AppMenu, MenuItemRole.FileMenu, MenuItemRole.EditMenu, MenuItemRole.ViewMenu, MenuItemRole.WindowMenu]) == true);
    else
        Check("no default application menu on Windows/Linux", defaultMenu is null && !win.IsMenuBarVisible, $"menu={defaultMenu is not null} bar={win.IsMenuBarVisible}");

    var clicks = new List<string>();
    var save = new MenuItem { Id = "save", Label = "&Save", Accelerator = "CmdOrCtrl+Shift+K", Click = (_, w) => clicks.Add($"save:{w == win}") };
    var check = new MenuItem { Type = MenuItemType.Checkbox, Label = "Check" };
    var radioA = new MenuItem { Type = MenuItemType.Radio, Label = "A", Checked = true };
    var radioB = new MenuItem { Type = MenuItemType.Radio, Label = "B" };
    var go = new MenuItem { Label = "&Go", Click = (_, w) => clicks.Add($"go:{w == win}") };
    var file = new MenuItem { Label = "&File", Submenu = [save, new MenuItem { Type = MenuItemType.Separator }, check, radioA, radioB] };
    var menu = OperatingSystem.IsMacOS()
        ? new Menu { new MenuItem { Role = MenuItemRole.AppMenu }, file, new MenuItem { Role = MenuItemRole.EditMenu } }
        : new Menu { file, new MenuItem { Role = MenuItemRole.EditMenu }, go };

    if (OperatingSystem.IsMacOS()) await MacMenusAsync(win, menu, save, check, radioA, radioB, clicks);
    else await BarMenusAsync(win, menu, save, go, clicks);
}

// Called only on macOS; the analyzer cannot see through the local functions' lambdas.
#pragma warning disable CA1416
async Task MacMenusAsync(BrowserWindow win, Menu menu, MenuItem save, MenuItem check, MenuItem radioA, MenuItem radioB, List<string> clicks)
{
    // AppKit objects are main-thread only (CEF's UI thread on macOS), and every menu change rebuilds the
    // menu bar and frees the old NSMenus: each read resolves its menu and reads it in one UI-thread call.
    static T Ui<T>(Func<T> func) => Oriel.Cef.CefThread.InvokeOnUi(func);
    static string[] Titles(nint m) => [.. Enumerable.Range(0, m == 0 ? 0 : MacMenu.ItemCount(m)).Select(i => MacMenu.Title(MacMenu.ItemAt(m, i)))];
    static nint Item(nint m, string title) => Array.IndexOf(Titles(m), title) is var i and >= 0 ? MacMenu.ItemAt(m, i) : 0;
    static nint Sub(string title) => Item(MacMenu.MainMenu(), title) is var item and not 0 ? MacMenu.Submenu(item) : 0;
    static string BarTitles() => Ui(() => string.Join(",", Titles(MacMenu.MainMenu())));
    static T OnItem<T>(string menuTitle, string itemTitle, Func<nint, T> read, T missing)
        => Ui(() => Item(Sub(menuTitle), itemTitle) is var item and not 0 ? read(item) : missing);
    static void Choose(string menuTitle, string itemTitle) => Ui(() =>
    {
        var m = Sub(menuTitle);
        MacMenu.PerformAction(m, Array.IndexOf(Titles(m), itemTitle));
        return 0;
    });

    Check("default menu is installed natively", Eventually(() => BarTitles() == "Oriel.WindowTest,File,Edit,View,Window", step), BarTitles());
    Check("default Edit menu: Copy sends copy: with Cmd+C", OnItem("Edit", "Copy",
        copy => MacMenu.ActionName(copy) == "copy:" && MacMenu.KeyEquivalent(copy) == "c" && MacMenu.ModifierMask(copy) == MacMenu.CommandKeyMask, false));

    Menu.SetApplicationMenu(menu);
    Check("SetApplicationMenu replaces the menu bar", Eventually(() => BarTitles() == "Oriel.WindowTest,File,Edit", step), BarTitles());
    Check("GetApplicationMenu", Menu.GetApplicationMenu() == menu);
    Check("accelerator becomes the key equivalent",
        OnItem("File", "Save", item => MacMenu.KeyEquivalent(item) == "k" && MacMenu.ModifierMask(item) == (MacMenu.CommandKeyMask | MacMenu.ShiftKeyMask), false),
        OnItem("File", "Save", item => $"{MacMenu.KeyEquivalent(item)} {MacMenu.ModifierMask(item):X}", "missing"));

    win.Focus();
    await Task.Delay(500);
    Choose("File", "Save");
    Check("choosing an item runs Click with the focused window", Eventually(() => clicks.Contains("save:True"), step), string.Join(",", clicks));

    Choose("File", "Check");
    Check("a checkbox toggles when chosen", check.Checked);
    Check("the native item shows the check", Eventually(() => OnItem("File", "Check", MacMenu.IsChecked, false), step));

    Choose("File", "B");
    Check("choosing a radio unchecks its group", radioB.Checked && !radioA.Checked, $"A={radioA.Checked} B={radioB.Checked}");

    save.Enabled = false;
    Check("disabling an item disables it natively", Eventually(() => OnItem("File", "Save", item => !MacMenu.IsEnabled(item), false), step));
    save.Label = "Save As";
    Check("relabelling an item updates it natively", Eventually(() => OnItem("File", "Save As", _ => true, false), step));
    check.Visible = false;
    Check("hiding an item hides it natively", Eventually(() => OnItem("File", "Check", MacMenu.IsHidden, false), step));

    // The editing roles go along AppKit's responder chain to the page.
    await Js(win, "document.body.innerHTML = '<p>select me</p>'; getSelection().removeAllRanges(); 1");
    Choose("Edit", "Select All");
    Check("Select All reaches the page", Eventually(() => Js(win, "getSelection().toString().trim()").Result is "\"select me\"", step), Js(win, "getSelection().toString()").Result);

    win.SetMenu(new Menu { new MenuItem { Label = "Ignored" } });
    await Task.Delay(300);
    Check("window menus are ignored on macOS", !win.IsMenuBarVisible && BarTitles() == "Oriel.WindowTest,File,Edit", BarTitles());
    Menu.SetApplicationMenu(null);
    // AppKit keeps its own application-name menu when the main menu is cleared.
    Check("SetApplicationMenu(null) clears the menu bar", Eventually(() => BarTitles() is "" or "Oriel.WindowTest", step), BarTitles());
}

#pragma warning restore CA1416

async Task BarMenusAsync(BrowserWindow win, Menu menu, MenuItem save, MenuItem go, List<string> clicks)
{
    var bounds = win.GetBounds();
    var content = win.GetContentBounds();
    // The page's own height shows whether CEF really laid the bar out, not just Oriel's bookkeeping.
    int PageHeight() => int.TryParse(Js(win, "innerHeight").Result as string, out var h) ? h : -1;
    var fullHeight = PageHeight();
    win.SetMenu(menu);
    Check("SetMenu shows the menu bar", Eventually(() => win.IsMenuBarVisible, step));
    Check("the bar takes room from the page, not the window",
        Eventually(() => win.GetContentBounds() is var c && c.Height < content.Height && c.Y > content.Y && win.GetBounds() == bounds, step),
        $"{content} -> {win.GetContentBounds()}, {bounds} -> {win.GetBounds()}");
    Check("the page shrinks by the bar", Eventually(() => PageHeight() is var h && h > 0 && h < fullHeight, step), $"{fullHeight} -> {PageHeight()}");

    Check("a bar item without a submenu acts as a button", win.TriggerMenuBarItem(2) && Eventually(() => clicks.Contains("go:True"), step), string.Join(",", clicks));

    win.Focus();
    await Task.Delay(300);
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    Check("an accelerator the page ignores runs the item", Eventually(() => clicks.Contains("save:True"), step), string.Join(",", clicks));

    save.Enabled = false;
    clicks.Clear();
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    await Task.Delay(500);
    Check("a disabled item's accelerator does nothing", clicks.Count == 0, string.Join(",", clicks));
    save.Enabled = true;

    win.AutoHideMenuBar = true;
    Check("AutoHideMenuBar hides the bar", Eventually(() => !win.IsMenuBarVisible && win.GetContentBounds() == content && PageHeight() == fullHeight, step), $"{win.GetContentBounds()} page={PageHeight()}/{fullHeight}");
    Check("AutoHideMenuBar reads back", win.AutoHideMenuBar);
    win.SetMenuBarVisibility(true);
    Check("SetMenuBarVisibility shows it", Eventually(() => win.IsMenuBarVisible, step));
    win.AutoHideMenuBar = false;
    Check("clearing AutoHideMenuBar keeps it shown", Eventually(() => win.IsMenuBarVisible && !win.AutoHideMenuBar, step));

    win.RemoveMenu();
    Check("RemoveMenu hides the bar", Eventually(() => !win.IsMenuBarVisible && win.GetContentBounds() == content && PageHeight() == fullHeight, step), $"{win.GetContentBounds()} page={PageHeight()}/{fullHeight}");

    Menu.SetApplicationMenu(new Menu { new MenuItem { Label = "App" } });
    await Task.Delay(300);
    Check("a removed menu stays removed under an application menu", !win.IsMenuBarVisible);
    var other = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: app menu", Width = 300, Height = 200 });
    Check("new windows get the application menu", Eventually(() => other.IsMenuBarVisible, step));
    var autoHidden = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: auto-hide", Width = 300, Height = 200, AutoHideMenuBar = true });
    Check("AutoHideMenuBar option starts hidden", Eventually(() => !autoHidden.IsMenuBarVisible && autoHidden.AutoHideMenuBar, step));
    Menu.SetApplicationMenu(null);
    Check("SetApplicationMenu(null) removes it", Eventually(() => !other.IsMenuBarVisible, step));
    other.Destroy();
    autoHidden.Destroy();
    await Task.Delay(300);
}


app.Ready += (_, _) =>
{
    var win = new BrowserWindow(new BrowserWindowOptions
    {
        Title = "Oriel window test",
        Width = 640,
        Height = 480,
        MinWidth = 400,
        MinHeight = 300,
    });
    win.LoadUrl("data:text/html,<h1>Oriel window test</h1>");
    var simple = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: simple", Show = false, SimpleFullScreen = true, BackgroundColor = "#102030" });
    simple.ReadyToShow += (_, _) => simpleReady = true;
    simple.LoadUrl("data:text/html,<title>simple</title>");

    // Off the UI thread: the steps wait for events the UI thread raises.
    _ = Task.Run(() => RunAsync(win, simple)).ContinueWith(t =>
    {
        if (t.Exception is { } e) Check("test driver", false, e.GetBaseException().Message);
        if (!win.IsDestroyed) win.Close();
    }, TaskScheduler.Default);
};

app.Run();
Console.WriteLine(failures < 0 ? "RESULT: did not finish" : $"RESULT: {failures} failure(s)");
return failures;
