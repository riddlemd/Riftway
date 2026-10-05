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
var willQuit = 0;
ExitedEventArgs? exited = null;
app.Exited += (_, e) => exited = e;
void countWillQuit(object? _, CancelEventArgs e) => willQuit++;

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
    // BrowserWindow's own LoadFile, with Electron's raw search, which replaces the query.
    await win.LoadFile(page, new Dictionary<string, string> { ["ignored"] = "1" }, search: "?raw=a%20b&x");
    Check("BrowserWindow.LoadFile with search uses it as is", win.WebContents.GetUrl().EndsWith("oriel-window-test.html?raw=a%20b&x", StringComparison.Ordinal), win.WebContents.GetUrl());
    Check("BrowserWindow.Reload reloads", Fires(h => win.WebContents.DidFinishLoad += h, h => win.WebContents.DidFinishLoad -= h, win.Reload, step));
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
    if (OperatingSystem.IsMacOS())
        Check("SimpleFullScreen option: SetFullScreen goes simple; IsFullScreen is false there, as Electron",
            Eventually(() => simple.IsSimpleFullScreen, step) && !simple.IsFullScreen, $"{simple.IsSimpleFullScreen} {simple.IsFullScreen}");
    else
        Check("SimpleFullScreen option: SetFullScreen is native full screen elsewhere", Eventually(() => simple.IsFullScreen, step), simple.IsFullScreen);
    simple.SetFullScreen(false);
    Check("SimpleFullScreen option: SetFullScreen(false) leaves it", Eventually(() => !simple.IsFullScreen && !simple.IsSimpleFullScreen, step),
        $"{simple.IsSimpleFullScreen} {simple.IsFullScreen}");
    Check("GetNativeWindowHandle", simple.GetNativeWindowHandle() != 0);
    if (OperatingSystem.IsMacOS())
    {
        // The green button, its hover menu and Window > Enter Full Screen all send toggleFullScreen:.
        void PressGreenButton() => Oriel.Cef.CefThread.InvokeOnUi(() =>
        {
            if (OperatingSystem.IsMacOS()) Oriel.Cef.Platform.MacWindow.ToggleFullScreen(simple.GetNativeWindowHandle());
            return 0;
        });
        bool NativeFullScreen() => Oriel.Cef.CefThread.InvokeOnUi(() => OperatingSystem.IsMacOS() && Oriel.Cef.Platform.MacWindow.IsNativeFullScreen(simple.GetNativeWindowHandle()));
        Check("SimpleFullScreen option: the green button enters simple full screen",
            Fires(h => simple.EnteredFullScreen += h, h => simple.EnteredFullScreen -= h, PressGreenButton, step)
            && simple.IsSimpleFullScreen && !NativeFullScreen(), $"simple={simple.IsSimpleFullScreen} native={NativeFullScreen()}");
        Check("SimpleFullScreen option: the green button leaves it",
            Fires(h => simple.LeftFullScreen += h, h => simple.LeftFullScreen -= h, PressGreenButton, step) && !simple.IsSimpleFullScreen);
    }
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
    await ScreenAsync(win);
    await DialogsAsync(win);
    await MenusAsync(win);
    await ContextMenusAsync(win);
    await WindowOpenAsync(win);
    await LoadAndSizeAsync(win);
    await NavigationAsync();
    await FramesAsync();
    await TaskbarAsync(win);
    await WindowsApisAsync();
    await NativeImageAsync();

    // Closing cancelled once: the window must survive the first close.
    var closingCount = 0;
    EventHandler<CancelEventArgs> veto = (_, e) => { closingCount++; e.Cancel = true; };
    win.Closing += veto;
    win.Close();
    await Task.Delay(1500);
    Check("Closing can cancel", closingCount == 1 && !win.IsDestroyed, $"closing={closingCount} destroyed={win.IsDestroyed}");
    win.Closing -= veto;

    // Quit, cancelled two ways: by BeforeQuit, and by a window refusing to close (Electron's quit flow).
    var beforeQuit = 0;
    EventHandler<CancelEventArgs> cancelQuit = (_, e) => { beforeQuit++; e.Cancel = true; };
    app.BeforeQuit += cancelQuit;
    app.Quit();
    await Task.Delay(1000);
    Check("BeforeQuit can cancel a quit", beforeQuit == 1 && !win.IsDestroyed, $"before={beforeQuit} destroyed={win.IsDestroyed}");
    app.BeforeQuit -= cancelQuit;
    var refused = 0;
    EventHandler<CancelEventArgs> refuse = (_, e) => { refused++; e.Cancel = true; };
    // Every window refuses: a quit closes windows until one refuses, and later checks still need these open.
    var open = BrowserWindow.GetAllWindows();
    foreach (var w in open) w.Closing += refuse;
    app.WillQuit += countWillQuit;
    app.Quit();
    await Task.Delay(1500);
    Check("a window refusing to close cancels the quit", refused >= 1 && open.All(w => !w.IsDestroyed) && willQuit == 0, $"refused={refused} willQuit={willQuit}");
    foreach (var w in open) w.Closing -= refuse;

    failures = results.Count(r => r.StartsWith("FAIL", StringComparison.Ordinal));
    Console.WriteLine($"RESULT: {failures} failure(s) before the final close");
    // Not closable by the user, but code can still close it.
    win.SetClosable(false);
    // The app quits below from this worker thread, which CEF itself ignores; nothing else may quit it first.
    app.WindowAllClosed += (_, _) => { };
    using var closed = new ManualResetEventSlim();
    win.Closed += (_, _) => closed.Set();
    using var childClosed = new ManualResetEventSlim();
    childAlive?.Closed += (_, _) => childClosed.Set();
    win.Close();
    Check("Close raises Closed", closed.Wait(step));
    Check("closing the parent closes its child", childClosed.Wait(step));
    failures = results.Count(r => r.StartsWith("FAIL", StringComparison.Ordinal));
    // Run returns only if Quit works off the UI thread; otherwise the run never prints its result.
    app.Quit();
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

async Task ScreenAsync(BrowserWindow win)
{
    static bool Inside(Rectangle outer, Rectangle inner)
        => inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;
    static bool Contains(Rectangle r, Point p) => p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;

    var primary = Screen.GetPrimaryDisplay();
    Check("primary display has a size and scale", primary.Bounds.Width > 0 && primary.Bounds.Height > 0 && primary.ScaleFactor > 0, primary);
    Check("work area lies inside the bounds", Inside(primary.Bounds, primary.WorkArea) && primary.WorkArea.Width > 0, $"{primary.WorkArea} in {primary.Bounds}");
    if (OperatingSystem.IsLinux())
        Check("Linux (Xvfb 1600x1000): exact primary bounds and scale", primary.Bounds == new Rectangle(0, 0, 1600, 1000) && primary.ScaleFactor == 1, primary);
    if (OperatingSystem.IsMacOS())
        Check("macOS: the primary display's work area leaves room for the menu bar", primary.WorkArea.Y > primary.Bounds.Y, $"{primary.WorkArea} in {primary.Bounds}");

    var all = Screen.GetAllDisplays();
    Check("GetAllDisplays includes the primary", all.Count >= 1 && all.Any(d => d.Id == primary.Id), all.Count);

    var bounds = win.GetBounds();
    var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    var nearest = Screen.GetDisplayNearestPoint(center);
    Check("GetDisplayNearestPoint: the display under the window", Contains(nearest.Bounds, center), $"{center} on {nearest.Bounds}");
    Check("GetDisplayMatching: the display the window overlaps", Screen.GetDisplayMatching(bounds).Id == nearest.Id);
    var far = new Point(nearest.Bounds.X - 100_000, nearest.Bounds.Y);
    Check("GetDisplayNearestPoint off every display picks the nearest", all.Any(d => d.Id == Screen.GetDisplayNearestPoint(far).Id));

    var scale = primary.ScaleFactor;
    // At fractional scales (125%) Chromium floors each conversion, so a round trip may lose a pixel.
    static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;
    static bool NearRect(Rectangle a, Rectangle b) => Near(new(a.X, a.Y), new(b.X, b.Y)) && Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1;
    var dip = new Point(100, 50);
    var pixels = Screen.DipToScreenPoint(dip);
    Check("DipToScreenPoint scales by the display's factor", Near(pixels, new Point((int)(100 * scale), (int)(50 * scale))), $"{pixels} at scale {scale}");
    Check("ScreenToDipPoint inverts it", Near(Screen.ScreenToDipPoint(pixels), dip), Screen.ScreenToDipPoint(pixels));
    var dipRect = new Rectangle(100, 50, 200, 100);
    var pixelRect = Screen.DipToScreenRect(null, dipRect);
    Check("DipToScreenRect scales by the display's factor", NearRect(pixelRect, new Rectangle((int)(100 * scale), (int)(50 * scale), (int)(200 * scale), (int)(100 * scale))), $"{pixelRect} at scale {scale}");
    Check("ScreenToDipRect inverts it, also by a window's display", NearRect(Screen.ScreenToDipRect(null, pixelRect), dipRect) && NearRect(Screen.ScreenToDipRect(win, pixelRect), dipRect),
        $"{Screen.ScreenToDipRect(null, pixelRect)} / {Screen.ScreenToDipRect(win, pixelRect)}");

    if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/xdotool"))
    {
        System.Diagnostics.Process.Start("/usr/bin/xdotool", "mousemove 321 234")!.WaitForExit();
        Check("GetCursorScreenPoint follows the X pointer", Eventually(() => Screen.GetCursorScreenPoint() == new Point(321, 234), step), Screen.GetCursorScreenPoint());
    }
    if (OperatingSystem.IsWindows())
    {
        // SetCursorPos takes physical pixels; GetCursorScreenPoint answers in DIPs.
        var target = new Point(321, 234);
        var moved = WinCursor.SetCursorPos(target.X, target.Y);
        var expected = Screen.ScreenToDipPoint(target);
        Check("GetCursorScreenPoint follows SetCursorPos", moved && Eventually(() => Screen.GetCursorScreenPoint() == expected, step), $"{Screen.GetCursorScreenPoint()} vs {expected} (pixels {target})");
    }
    if (OperatingSystem.IsMacOS())
    {
        var cursor = Screen.GetCursorScreenPoint();
        var expected = MacMouseLocation();
        Check("GetCursorScreenPoint matches AppKit's mouse location", Math.Abs(cursor.X - expected.X) <= 1 && Math.Abs(cursor.Y - expected.Y) <= 1, $"{cursor} vs {expected}");
    }

    // Display changes cannot be made here; subscribing starts the poll and a steady setup raises nothing.
    var raised = 0;
    EventHandler<DisplayMetricsChangedEventArgs> onChanged = (_, _) => raised++;
    EventHandler<DisplayEventArgs> onAdded = (_, _) => raised++;
    Screen.DisplayMetricsChanged += onChanged;
    Screen.DisplayAdded += onAdded;
    await Task.Delay(2500);
    Screen.DisplayMetricsChanged -= onChanged;
    Screen.DisplayAdded -= onAdded;
    Check("no display events while nothing changes", raised == 0, raised);
}

// AppKit's mouseLocation is bottom-left based; flip it by the main display's height into CEF's top-left DIPs.
static Point MacMouseLocation()
{
    var location = MacInterop.MouseLocation(MacInterop.objc_getClass("NSEvent"), MacInterop.sel_registerName("mouseLocation"));
    var main = MacInterop.CGDisplayBounds(MacInterop.CGMainDisplayID());
    return new Point((int)Math.Floor(location.X), (int)Math.Floor(main.Height - location.Y));
}

async Task DialogsAsync(BrowserWindow win)
{
    var dir = Directory.CreateTempSubdirectory("oriel-dialog-").FullName;
    var target = Path.Combine(dir, "saved.txt");
    static string Real(string path) => Path.GetFullPath(new FileInfo(path).Directory!.ResolveLinkTarget(true)?.FullName ?? path);
    static bool SamePath(string a, string b) => a.Length > 0 && b.Length > 0 && string.Equals(
        Path.Combine(Real(Path.GetDirectoryName(a)!), Path.GetFileName(a)), Path.Combine(Real(Path.GetDirectoryName(b)!), Path.GetFileName(b)), StringComparison.Ordinal);

    // Answers the dialog the way a user would: macOS through the panel itself, Linux through X key presses.
    bool Answer(string title, bool accept)
    {
        if (OperatingSystem.IsMacOS())
        {
            // The panel's UI loads out of process after its window appears; answering earlier can be lost.
            if (!Eventually(() => Oriel.Cef.CefThread.InvokeOnUi(() => MacPanel.Find() != 0), step)) return false;
            Thread.Sleep(1000);
            return Oriel.Cef.CefThread.InvokeOnUi(() => MacPanel.Answer(accept));
        }
        if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/xdotool"))
        {
            // GTK's dialog is its own X window. Chromium sets no default button, so Return does not accept:
            // press Escape to cancel, or click the bottom-right button (Save / Open / Upload) to accept, as a user would.
            string X(string args)
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/timeout", "10 /usr/bin/xdotool " + args) { RedirectStandardOutput = true })!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 ? output.Trim() : "";
            }
            var id = X($"search --sync --name \"{title}\"").Split('\n')[0];
            if (id.Length == 0) return false;
            if (!accept)
            {
                X($"windowactivate --sync {id} key --clearmodifiers Escape");
                return true;
            }
            var geometry = X($"getwindowgeometry --shell {id}").Split('\n').Select(l => l.Split('=')).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));
            X($"windowactivate --sync {id} mousemove --window {id} {geometry["WIDTH"] - 50} {geometry["HEIGHT"] - 28} click 1");
            return true;
        }
        if (OperatingSystem.IsWindows())
        {
            // IFileDialog is a top-level #32770 titled by Title; IDOK presses its default button (Save / Open / Select Folder).
            var hwnd = (nint)0;
            if (!Eventually(() => (hwnd = WinDialog.FindWindowW("#32770", title)) != 0 && WinDialog.IsWindowVisible(hwnd), step)) return false;
            // Its shell view fills in after the window shows; IDOK before that can be dropped.
            Thread.Sleep(1000);
            return WinDialog.PostMessageW(hwnd, 0x0111 /* WM_COMMAND */, accept ? 1 /* IDOK */ : 2 /* IDCANCEL */, 0);
        }
        return false;
    }

    if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;

    var save = Dialog.ShowSaveDialogAsync(win, new SaveDialogOptions { Title = "Oriel save test", DefaultPath = target, Filters = [new("Text", "txt")] });
    Check("save dialog opens and accepts", Answer("Oriel save test", accept: true));
    var saved = await Task.WhenAny(save, Task.Delay(step)) == save ? save.Result : null;
    Check("ShowSaveDialogAsync returns the chosen path", saved is { Canceled: false } && SamePath(saved.FilePath, target), saved);

    var open = Dialog.ShowOpenDialogAsync(win, new OpenDialogOptions { Title = "Oriel open test", DefaultPath = dir });
    Check("open dialog opens and cancels", Answer("Oriel open test", accept: false));
    var opened = await Task.WhenAny(open, Task.Delay(step)) == open ? open.Result : null;
    Check("a cancelled open dialog reports Canceled and no paths", opened is { Canceled: true, FilePaths.Count: 0 }, opened);

    var folder = Dialog.ShowOpenDialogAsync(win, new OpenDialogOptions { Title = "Oriel folder test", DefaultPath = dir, Properties = OpenDialogProperties.OpenDirectory });
    Check("folder dialog opens and accepts", Answer("Oriel folder test", accept: true));
    var chosen = await Task.WhenAny(folder, Task.Delay(step)) == folder ? folder.Result : null;
    Check("OpenDirectory returns the directory it started in", chosen is { Canceled: false, FilePaths: [var only] } && Real(only) == Real(dir),
        chosen is null ? "none" : string.Join(",", chosen.FilePaths));

    Check("Sync dialogs refuse the UI thread",
        Oriel.Cef.CefThread.InvokeOnUi(() => Throws<InvalidOperationException>(() => Dialog.ShowSaveDialogSync(win, new SaveDialogOptions()))));
    Directory.Delete(dir, recursive: true);
}

async Task ContextMenusAsync(BrowserWindow win)
{
    win.Focus();
    await Js(win, "document.body.innerHTML = '<textarea id=t>hello world</textarea>'; t.focus(); t.select(); 1");
    async Task<ContextMenuParams?> AskForMenu(Action ask)
    {
        ContextMenuParams? seen = null;
        using var asked = new ManualResetEventSlim();
        EventHandler<ContextMenuParams> onMenu = (_, p) => { seen = p; asked.Set(); };
        win.WebContents.ContextMenu += onMenu;
        ask();
        await Task.Run(() => asked.Wait(step));
        win.WebContents.ContextMenu -= onMenu;
        return seen;
    }
    static string Describe(ContextMenuParams? p) => p is null ? "none"
        : $"at={p.X},{p.Y} editable={p.IsEditable} selection={p.SelectionText} copy={p.EditFlags.CanCopy} paste={p.EditFlags.CanPaste} page={p.PageUrl.Length > 0}";
    static bool OnTheSelectedField(ContextMenuParams? p)
        => p is { IsEditable: true, SelectionText: "hello world", EditFlags.CanCopy: true, EditFlags.CanPaste: true } && p.PageUrl.Length > 0;

    var x = int.Parse((string)(await Js(win, "Math.round(t.getBoundingClientRect().left + 20)"))!);
    var y = int.Parse((string)(await Js(win, "Math.round(t.getBoundingClientRect().top + 10)"))!);
    var clicked = await AskForMenu(() =>
    {
        win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, x, y) { Button = MouseButton.Right });
        win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseUp, x, y) { Button = MouseButton.Right });
    });
    Check("a right click raises ContextMenu with its params", OnTheSelectedField(clicked) && Math.Abs(clicked!.X - x) <= 1 && Math.Abs(clicked.Y - y) <= 1, Describe(clicked));


    if (!OperatingSystem.IsMacOS())
    {
        // Shift+F10 asks for the context menu from the keyboard (not a macOS convention).
        var keyed = await AskForMenu(() =>
        {
            win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "F10") { Modifiers = InputModifiers.Shift });
            win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "F10") { Modifiers = InputModifiers.Shift });
        });
        Check("Shift+F10 raises ContextMenu", OnTheSelectedField(keyed), Describe(keyed));
    }

    // Chromium offers no media menu for a video without a source; a missing one still counts (in error).
    await Js(win, "document.body.insertAdjacentHTML('beforeend', '<video id=v src=missing.webm muted loop controls style=width:200px;height:120px></video>'); 1");
    // Only once it has tried its source does Chromium treat it as media.
    Eventually(() => Js(win, "v.error !== null").Result is "true", step);
    var vx = int.Parse((string)(await Js(win, "Math.round(v.getBoundingClientRect().left + 100)"))!);
    var vy = int.Parse((string)(await Js(win, "Math.round(v.getBoundingClientRect().top + 30)"))!);
    var onVideo = await AskForMenu(() =>
    {
        win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, vx, vy) { Button = MouseButton.Right });
        win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseUp, vx, vy) { Button = MouseButton.Right });
    });
    Check("a right click on a video reports its media flags",
        onVideo is { MediaType: ContextMenuMediaType.Video, MediaFlags: { IsMuted: true, IsLooping: true, IsControlsVisible: true } },
        onVideo is null ? "none" : $"{onVideo.MediaType} src={onVideo.SrcUrl} {onVideo.MediaFlags}");

    var popup = new Menu { new MenuItem { Label = "One" }, new MenuItem { Type = MenuItemType.Separator }, new MenuItem { Role = MenuItemRole.Copy } };
    using (var closed = new ManualResetEventSlim())
    {
        popup.Popup(new PopupOptions { Window = win, X = 20, Y = 20, Closed = closed.Set });
        await Task.Delay(500);
        Check("a popup stays open until closed", !closed.IsSet);
        popup.ClosePopup(win);
        Check("ClosePopup closes it and runs Closed", closed.Wait(step));
    }
    using (var closed = new ManualResetEventSlim())
    {
        // Without X/Y it opens at the pointer.
        popup.Popup(new PopupOptions { Window = win, Closed = closed.Set });
        await Task.Delay(300);
        popup.ClosePopup(win);
        Check("a popup without X/Y opens (at the pointer)", closed.Wait(step));
    }
    Check("Popup with X but no Y throws", Throws<ArgumentException>(() => popup.Popup(new PopupOptions { Window = win, X = 1 })));
}

static bool Throws<T>(Action act) where T : Exception
{
    try { act(); return false; }
    catch (T) { return true; }
}

async Task MenusAsync(BrowserWindow win)
{
    var defaultMenu = Menu.GetApplicationMenu();
    MenuItemRole[] expected = OperatingSystem.IsMacOS()
        ? [MenuItemRole.AppMenu, MenuItemRole.FileMenu, MenuItemRole.EditMenu, MenuItemRole.ViewMenu, MenuItemRole.WindowMenu]
        : [MenuItemRole.FileMenu, MenuItemRole.EditMenu, MenuItemRole.ViewMenu, MenuItemRole.WindowMenu];
    Check("the default application menu is Electron's on every OS", defaultMenu?.Items.Select(i => i.Role).SequenceEqual(expected) == true
        && (OperatingSystem.IsMacOS() || win.IsMenuBarVisible), $"bar={win.IsMenuBarVisible}");

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
    // Measured from no bar at all: the default application menu gives every window one.
    win.RemoveMenu();
    Eventually(() => !win.IsMenuBarVisible, step);
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

    // Keys aimed at the page reach the bar: Alt alone toggles an auto-hidden bar, Alt+letter opens that menu.
    void Key(string key, KeyboardInputEventType type, InputModifiers modifiers = InputModifiers.None)
        => win.WebContents.SendInputEvent(new KeyboardInputEvent(type, key) { Modifiers = modifiers });
    void PressAlt()
    {
        Key("Alt", KeyboardInputEventType.KeyDown, InputModifiers.Alt);
        Key("Alt", KeyboardInputEventType.KeyUp);
    }
    win.AutoHideMenuBar = true;
    win.Focus();
    await Task.Delay(300);
    PressAlt();
    Check("Alt shows an auto-hidden bar", Eventually(() => win.IsMenuBarVisible, step));
    PressAlt();
    Check("Alt again hides it", Eventually(() => !win.IsMenuBarVisible, step));
    PressAlt();
    Eventually(() => win.IsMenuBarVisible, step);
    Key("A", KeyboardInputEventType.KeyDown);
    Key("A", KeyboardInputEventType.KeyUp);
    Check("typing into the page hides an Alt-shown bar", Eventually(() => !win.IsMenuBarVisible, step));
    PressAlt();
    Eventually(() => win.IsMenuBarVisible, step);
    var thief = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: focus thief", Width = 200, Height = 150 });
    thief.Focus();
    Check("losing focus hides an Alt-shown bar", Eventually(() => !win.IsMenuBarVisible, step));
    thief.Destroy();
    win.Focus();
    await Task.Delay(300);
    win.AutoHideMenuBar = false;
    Key("Alt", KeyboardInputEventType.KeyDown, InputModifiers.Alt);
    Key("F", KeyboardInputEventType.KeyDown, InputModifiers.Alt);
    Key("F", KeyboardInputEventType.KeyUp, InputModifiers.Alt);
    Key("Alt", KeyboardInputEventType.KeyUp);
    Check("Alt+F opens the &File menu", Eventually(() => win.IsMenuBarMenuOpen, step));
    win.CancelMenuBarMenu();
    Check("the menu closes", Eventually(() => !win.IsMenuBarMenuOpen, step));
    Key("F", KeyboardInputEventType.KeyDown);
    Key("F", KeyboardInputEventType.KeyUp);
    await Task.Delay(500);
    Check("F without Alt opens nothing", !win.IsMenuBarMenuOpen);
    win.CancelMenuBarMenu();

    // An auto-hidden bar that Alt+letter brought up hides again once its menu closes.
    win.AutoHideMenuBar = true;
    await Task.Delay(300);
    Key("Alt", KeyboardInputEventType.KeyDown, InputModifiers.Alt);
    Key("F", KeyboardInputEventType.KeyDown, InputModifiers.Alt);
    Key("F", KeyboardInputEventType.KeyUp, InputModifiers.Alt);
    Key("Alt", KeyboardInputEventType.KeyUp);
    Check("Alt+F shows an auto-hidden bar with its menu", Eventually(() => win.IsMenuBarMenuOpen && win.IsMenuBarVisible, step));
    win.CancelMenuBarMenu();
    Check("the bar hides again when its menu closes", Eventually(() => !win.IsMenuBarMenuOpen && !win.IsMenuBarVisible, step));
    win.AutoHideMenuBar = false;

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


async Task WindowOpenAsync(BrowserWindow win)
{
    var dir = Directory.CreateTempSubdirectory("oriel-open-").FullName;
    var opener = Path.Combine(dir, "opener.html");
    var popupPage = Path.Combine(dir, "popup.html");
    File.WriteAllText(opener, "<!doctype html><title>opener</title><a id=blank href=popup.html target=_blank>blank</a>");
    File.WriteAllText(popupPage, "<!doctype html><title>popup page</title><p>popup</p>");
    var popupUrl = new Uri(popupPage).AbsoluteUri;
    Fires(h => win.WebContents.DidFinishLoad += h, h => win.WebContents.DidFinishLoad -= h, () => win.WebContents.LoadFile(opener), step);
    async Task<string?> Run(BrowserWindow w, string code, bool gesture = false)
    {
        var task = w.WebContents.ExecuteJavaScriptAsync(code, gesture);
        return await Task.WhenAny(task, Task.Delay(step)) == task ? task.Result?.ToJsonString() : "timeout";
    }

    // Default: the window opens as a BrowserWindow sized by the features, and the opener hears about it.
    DidCreateWindowEventArgs? created = null;
    using var createdEvent = new ManualResetEventSlim();
    EventHandler<DidCreateWindowEventArgs> onCreated = (_, e) => { created = e; createdEvent.Set(); };
    win.WebContents.DidCreateWindow += onCreated;
    var opened = await Run(win, "window.__popup = window.open('popup.html', 'pop', 'width=320,height=240'); window.__popup !== null");
    Check("window.open returns a window by default", opened == "true", opened);
    Check("DidCreateWindow is raised", await Task.Run(() => createdEvent.Wait(step)));
    var details = created?.Details;
    Check("details: url, frame name, features, disposition",
        details is { FrameName: "pop", Disposition: WindowOpenDisposition.NewWindow } && details.Url == popupUrl && details.Features.Contains("width=320") && details.Features.Contains("height=240"),
        details);
    var popup = created?.Window;
    Check("the popup is a BrowserWindow in GetAllWindows", popup is not null && BrowserWindow.GetAllWindows().Contains(popup));
    if (popup is not null)
    {
        Check("the popup loads its page", Eventually(() => popup.WebContents.GetTitle() == "popup page", step), popup.WebContents.GetTitle());
        Check("the features size the popup's window, as Electron", Eventually(() => popup.GetSize() == (320, 240), step), popup.GetSize());
        Check("the popup sees its opener", await Run(popup, "window.opener !== null") == "true");
        // file: pages are opaque origins, so the opener may only use cross-origin members such as closed.
        Check("the opener holds a live handle to the popup", await Run(win, "window.__popup.closed") == "false", await Run(win, "window.__popup.closed"));
        using var popupClosed = new ManualResetEventSlim();
        popup.Closed += (_, _) => popupClosed.Set();
        popup.Close();
        Check("the popup closes", popupClosed.Wait(step));
    }
    win.WebContents.DidCreateWindow -= onCreated;

    // Deny: window.open returns null and no window appears.
    WindowOpenDetails? asked = null;
    win.WebContents.SetWindowOpenHandler(d => { asked = d; return WindowOpenHandlerResponse.Deny; });
    var windows = BrowserWindow.GetAllWindows().Count;
    Check("a denied window.open returns null", await Run(win, "window.open('popup.html') === null") == "true");
    await Task.Delay(500);
    Check("a denied window opens nothing", BrowserWindow.GetAllWindows().Count == windows, BrowserWindow.GetAllWindows().Count);

    // A target=_blank link clicked by the user asks for a foreground tab.
    asked = null;
    await Run(win, "document.getElementById('blank').click(); 1", gesture: true);
    Check("a target=_blank link reaches the handler as ForegroundTab", Eventually(() => asked is { Disposition: WindowOpenDisposition.ForegroundTab, UserGesture: true }, step), asked);

    // Override options; and popups close with their opener unless they outlive it.
    var preload = Path.Combine(dir, "preload.js");
    File.WriteAllText(preload, "oriel.contextBridge.exposeInMainWorld('probe', { ping: () => 'pong' });");
    var second = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: opener", Width = 400, Height = 300, WebPreferences = new WebPreferences { Preload = preload } });
    Fires(h => second.WebContents.DidFinishLoad += h, h => second.WebContents.DidFinishLoad -= h, () => second.WebContents.LoadFile(opener), step);
    var popups = new List<BrowserWindow>();
    second.WebContents.DidCreateWindow += (_, e) => { lock (popups) popups.Add(e.Window); };
    // Without overrides a popup inherits the opener's web preferences, preload included.
    await Run(second, "window.open('popup.html'); 1");
    Check("a popup opens from the preloaded opener", Eventually(() => { lock (popups) return popups.Count == 1; }, step), popups.Count);
    if (popups.Count == 1)
    {
        var inheriting = popups[0];
        Check("a popup inherits the opener's preload", Eventually(() => Run(inheriting, "typeof probe").Result == "\"object\"", step), Run(inheriting, "typeof probe").Result);
        inheriting.Destroy();
        lock (popups) popups.Clear();
    }

    // Electron spreads the handler's options over the features': the page's width stays, the handler's height wins.
    second.WebContents.SetWindowOpenHandler(_ => WindowOpenHandlerResponse.Allow(options => options with { Height = 250 }));
    await Run(second, "window.open('popup.html', '', 'width=320'); 1");
    Check("a configured popup keeps the features and takes the handler's changes, as Electron merges them",
        Eventually(() => { lock (popups) return popups.Count == 1 && popups[0].GetSize() == (320, 250); }, step),
        popups.Count == 1 ? popups[0].GetSize() : popups.Count);
    lock (popups)
    {
        foreach (var p in popups) p.Destroy();
        popups.Clear();
    }

    var outlive = false;
    second.WebContents.SetWindowOpenHandler(_ => WindowOpenHandlerResponse.Allow(new BrowserWindowOptions { Width = 360, Height = 260 }, outlivesOpener: outlive));
    await Run(second, "window.open('popup.html'); 1");
    outlive = true;
    await Run(second, "window.open('popup.html'); 1");
    Check("both popups open", Eventually(() => { lock (popups) return popups.Count == 2; }, step), popups.Count);
    if (popups.Count == 2)
    {
        var (closesWithOpener, outlives) = (popups[0], popups[1]);
        var reference = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: reference", Width = 360, Height = 260 });
        await Task.Delay(500);
        Check("OverrideBrowserWindowOptions sizes the popup as the same options size a window", Eventually(() => closesWithOpener.GetSize() == reference.GetSize(), step),
            $"popup {closesWithOpener.GetSize()} window {reference.GetSize()} asked (360, 260)");
        reference.Destroy();
        second.Destroy();
        Check("a popup closes with its opener", Eventually(() => closesWithOpener.IsDestroyed, step));
        await Task.Delay(500);
        Check("OutlivesOpener keeps it open", !outlives.IsDestroyed);
        outlives.Destroy();
    }
    // Every path closes what it opened, or the app never quits.
    if (!second.IsDestroyed) second.Destroy();
    foreach (var p in popups.ToArray()) if (!p.IsDestroyed) p.Destroy();
    win.WebContents.SetWindowOpenHandler(null);
    await Task.Delay(300);
    Directory.Delete(dir, recursive: true);
}

async Task LoadAndSizeAsync(BrowserWindow win)
{
    var dir = Directory.CreateTempSubdirectory("oriel-load-").FullName;
    var one = Path.Combine(dir, "one.html");
    var two = Path.Combine(dir, "two.html");
    File.WriteAllText(one, "<!doctype html><title>one</title><p id=a>one</p>");
    File.WriteAllText(two, "<!doctype html><title>two</title>");
    async Task<(bool Done, Exception? Error)> Outcome(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(step)) != task) return (false, null);
        return (true, task.Exception?.InnerException);
    }

    var loaded = await Outcome(win.WebContents.LoadFile(one));
    Check("LoadFile's task completes when the page has loaded", loaded is (true, null) && win.WebContents.GetTitle() == "one", $"{loaded} title={win.WebContents.GetTitle()}");
    var fragment = await Outcome(win.WebContents.LoadUrl(new Uri(one).AbsoluteUri + "#a"));
    Check("a fragment-only LoadUrl completes", fragment is (true, null), fragment);
    var missing = await Outcome(win.WebContents.LoadUrl(new Uri(Path.Combine(dir, "missing.html")).AbsoluteUri));
    Check("a failed load fails the task with LoadFailedException", missing is (true, LoadFailedException { ErrorCode: < 0 }), missing);
    var first = win.WebContents.LoadFile(one);
    var second = win.WebContents.LoadFile(two);
    var superseded = await Outcome(first);
    var latest = await Outcome(second);
    Check("a load replaced by a newer one fails with ERR_ABORTED", superseded is (true, LoadFailedException { ErrorCode: -3 }), superseded);
    Check("the newer load completes", latest is (true, null), latest);

    var sized = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: size", Width = 360, Height = 260 });
    Check("a new window has the outer size asked for", Eventually(() => sized.GetSize() == (360, 260), step), sized.GetSize());
    sized.Destroy();
    await Task.Delay(300);
    Directory.Delete(dir, recursive: true);
}

async Task NavigationAsync()
{
    var dir = Directory.CreateTempSubdirectory("oriel-history-").FullName;
    string Page(string name)
    {
        var path = Path.Combine(dir, $"{name}.html");
        File.WriteAllText(path, $"<!doctype html><title>{name}</title>");
        return path;
    }
    var (one, two, three) = (Page("one"), Page("two"), Page("three"));
    var nav = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: history", Width = 400, Height = 300 });
    var history = nav.WebContents.NavigationHistory;
    bool Load(string path) => Fires(h => nav.WebContents.DidFinishLoad += h, h => nav.WebContents.DidFinishLoad -= h, () => nav.WebContents.LoadFile(path), step);
    string Titles() => string.Join(",", history.GetAllEntries().Select(e => e.Title));
    bool At(int index, string title) => Eventually(() => history.GetActiveIndex() == index && nav.WebContents.GetTitle() == title, step);

    Check("history starts with the first page, not the about:blank start", Load(one) && Eventually(() => history.Length() == 1, step), Titles());
    Load(two);
    Load(three);
    Check("each load adds an entry", Eventually(() => Titles() == "one,two,three", step), Titles());
    Check("GetEntryAtIndex", history.GetEntryAtIndex(0) is { Title: "one" } e && e.Url == new Uri(one).AbsoluteUri && history.GetEntryAtIndex(3) is null && history.GetEntryAtIndex(-1) is null);
    Check("at the end: back yes, forward no", history.GetActiveIndex() == 2 && history.CanGoBack() && !history.CanGoForward());
    Check("CanGoToOffset", history.CanGoToOffset(-2) && !history.CanGoToOffset(-3) && !history.CanGoToOffset(1));

    history.GoBack();
    Check("GoBack", At(1, "two") && history.CanGoForward(), $"{history.GetActiveIndex()} {nav.WebContents.GetTitle()}");
    history.GoForward();
    Check("GoForward", At(2, "three"), $"{history.GetActiveIndex()} {nav.WebContents.GetTitle()}");
    history.GoToIndex(0);
    Check("GoToIndex", At(0, "one"), $"{history.GetActiveIndex()} {nav.WebContents.GetTitle()}");
    history.GoToOffset(2);
    Check("GoToOffset", At(2, "three"), $"{history.GetActiveIndex()} {nav.WebContents.GetTitle()}");
    history.GoToOffset(-1);
    Check("GoToOffset moves relative to the page showing", At(1, "two"), $"{history.GetActiveIndex()} {nav.WebContents.GetTitle()}");
    history.GoToIndex(2);
    At(2, "three");

    // Directly, as Electron: a page whose scripts are off still moves.
    var noScript = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Javascript = false } });
    await noScript.WebContents.LoadFile(one);
    await noScript.WebContents.LoadFile(two);
    noScript.WebContents.NavigationHistory.GoToIndex(0);
    Check("GoToIndex works without the page's scripts", Eventually(() => noScript.WebContents.NavigationHistory.GetActiveIndex() == 0, step),
        noScript.WebContents.NavigationHistory.GetActiveIndex());
    noScript.WebContents.NavigationHistory.GoToOffset(1);
    Check("GoToOffset works without the page's scripts", Eventually(() => noScript.WebContents.NavigationHistory.GetActiveIndex() == 1, step),
        noScript.WebContents.NavigationHistory.GetActiveIndex());
    noScript.Destroy();

    history.Clear();
    Check("Clear keeps only the page showing", Eventually(() => history.Length() == 1 && history.GetActiveIndex() == 0 && !history.CanGoBack(), step), Titles());
    Check("the page showing is kept", history.GetEntryAtIndex(0) is { Title: "three" });

    nav.Destroy();
    await Task.Delay(300);
    Directory.Delete(dir, recursive: true);
}

async Task TaskbarAsync(BrowserWindow win)
{
    if (OperatingSystem.IsMacOS())
    {
        await MacTaskbarAsync();
        return;
    }
    if (OperatingSystem.IsLinux())
    {
        await LinuxTaskbarAsync();
        return;
    }
    if (!OperatingSystem.IsWindows()) return;

    // The shell groups an app's windows under one button named "<app> - N running windows"; DeleteTab lowers N.
    var app = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
    int? Buttons() => WindowsShell.TaskbarNames().FirstOrDefault(n => n.StartsWith(app + " - ", StringComparison.Ordinal)) is { } name
        && System.Text.RegularExpressions.Regex.Match(name[(app.Length + 3)..], @"\d+") is { Success: true } m ? int.Parse(m.Value) : null;

    var tb = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: taskbar", Width = 300, Height = 200 });
    await Task.Delay(1500);
    var withButton = Buttons();
    Check("the taskbar lists this app's windows (UI Automation)", withButton is > 0, withButton);
    tb.SetSkipTaskbar(true);
    Check("SetSkipTaskbar(true) removes the window's taskbar button", Eventually(() => Buttons() == withButton - 1, step), $"{withButton} -> {Buttons()}");
    tb.Hide();
    tb.Show();
    await Task.Delay(1500);
    Check("SkipTaskbar holds when the window is shown again", Buttons() == withButton - 1, $"{withButton} -> {Buttons()}");
    tb.SetSkipTaskbar(false);
    Check("SetSkipTaskbar(false) gives the button back", Eventually(() => Buttons() == withButton, step), $"{withButton} -> {Buttons()}");
    var skipped = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: skip taskbar", Width = 300, Height = 200, SkipTaskbar = true });
    await Task.Delay(1500);
    Check("the SkipTaskbar option opens a window without a button", skipped.IsVisible && Buttons() == withButton, $"visible={skipped.IsVisible} {withButton} -> {Buttons()}");
    skipped.Destroy();
    var later = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: skip taskbar, shown later", Width = 300, Height = 200, Show = false, SkipTaskbar = true });
    await Task.Delay(500);
    later.Show();
    await Task.Delay(1500);
    Check("the SkipTaskbar option holds for a window shown later", later.IsVisible && Buttons() == withButton, $"visible={later.IsVisible} {withButton} -> {Buttons()}");
    later.Destroy();

    // ITaskbarList3 is write-only, and the shell answers S_OK to progress calls even for a wrong window, so
    // progress has no check here. The overlay can at least fail to build its icon; Oriel logs that to stderr.
    var refusals = new StringWriter();
    var stderr = Console.Error;
    Console.SetError(refusals);
    tb.SetOverlayIcon(NativeImage.CreateFromPath(OverlayPng()), "Oriel overlay test");
    tb.SetOverlayIcon(null, "");
    await Task.Delay(500);
    Console.SetError(stderr);
    Check("the shell takes the overlay icon (weak: no read-back)", refusals.ToString().Length == 0, refusals.ToString().Trim());

    // The shell hook's HSHELL_FLASH is the only read-back of a flash. The first comes about 0.5 s after the call;
    // a later one can come from the shell itself, so only an early one counts.
    WindowsShell.StartShellHook();
    var hwnd = tb.GetNativeWindowHandle();
    win.Focus();
    await Task.Delay(500);
    tb.FlashFrame(true);
    Check("FlashFrame(true) flashes the window (HSHELL_FLASH)", Eventually(() => WindowsShell.Count(WindowsShell.FlashCode, hwnd) > 0, TimeSpan.FromMilliseconds(1500)), WindowsShell.Count(WindowsShell.FlashCode, hwnd));
    tb.FlashFrame(false);
    var flashes = WindowsShell.Count(WindowsShell.FlashCode, hwnd);
    await Task.Delay(2500);
    Check("FlashFrame(false) stops it", WindowsShell.Count(WindowsShell.FlashCode, hwnd) == flashes, $"{flashes} -> {WindowsShell.Count(WindowsShell.FlashCode, hwnd)}");
    tb.Destroy();
    await Task.Delay(300);
}

async Task WindowsApisAsync()
{
    if (!OperatingSystem.IsWindows()) return;

    var app = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
    int? Buttons() => WindowsShell.TaskbarNames().FirstOrDefault(n => n.StartsWith(app + " - ", StringComparison.Ordinal)) is { } name
        && System.Text.RegularExpressions.Regex.Match(name[(app.Length + 3)..], @"\d+") is { Success: true } m ? int.Parse(m.Value) : null;

    // The property store is the read-back; the taskbar moving the window out of the app's group shows the shell used it.
    var w = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: app details", Width = 300, Height = 200, BackgroundMaterial = BackgroundMaterial.Tabbed });
    await Task.Delay(1500);
    var hwnd = w.GetNativeWindowHandle();
    var grouped = Buttons();
    w.SetAppDetails(new AppDetailsOptions
    {
        AppId = "Riddlemd.Oriel.WindowTest.Details",
        AppIconPath = Environment.ProcessPath,
        AppIconIndex = 0,
        RelaunchCommand = $"\"{Environment.ProcessPath}\" --relaunched",
        RelaunchDisplayName = "Oriel details test",
    });
    string? Read(AppUserModelProperty p) => WindowsShell.AppUserModel(hwnd, p);
    Check("SetAppDetails sets the App User Model ID (property store)", Eventually(() => Read(AppUserModelProperty.Id) == "Riddlemd.Oriel.WindowTest.Details", step), Read(AppUserModelProperty.Id));
    Check("SetAppDetails sets the relaunch command, name and icon",
        Read(AppUserModelProperty.RelaunchCommand) == $"\"{Environment.ProcessPath}\" --relaunched"
        && Read(AppUserModelProperty.RelaunchDisplayNameResource) == "Oriel details test"
        && Read(AppUserModelProperty.RelaunchIconResource) == $"{Environment.ProcessPath},0",
        $"{Read(AppUserModelProperty.RelaunchCommand)} | {Read(AppUserModelProperty.RelaunchDisplayNameResource)} | {Read(AppUserModelProperty.RelaunchIconResource)}");
    Check("a new App User Model ID moves the window out of the app's taskbar group", Eventually(() => Buttons() == grouped - 1, step), $"{grouped} -> {Buttons()}");
    w.SetAppDetails(new AppDetailsOptions { RelaunchDisplayName = "Renamed" });
    Check("SetAppDetails leaves options it is not given",
        Eventually(() => Read(AppUserModelProperty.RelaunchDisplayNameResource) == "Renamed", step) && Read(AppUserModelProperty.Id) == "Riddlemd.Oriel.WindowTest.Details",
        $"{Read(AppUserModelProperty.Id)} | {Read(AppUserModelProperty.RelaunchDisplayNameResource)}");

    // DWM reads the backdrop back. Chromium already gives its windows Mica, so the option is checked with Tabbed.
    Check("the BackgroundMaterial option sets the backdrop (DWM read-back)", WindowsShell.Backdrop(hwnd) == WindowsBackdrop.TabbedWindow, WindowsShell.Backdrop(hwnd));
    w.SetBackgroundMaterial(BackgroundMaterial.Acrylic);
    Check("SetBackgroundMaterial(Acrylic) sets the transient-window backdrop", Eventually(() => WindowsShell.Backdrop(hwnd) == WindowsBackdrop.TransientWindow, step), WindowsShell.Backdrop(hwnd));
    w.SetBackgroundMaterial(BackgroundMaterial.None);
    Check("SetBackgroundMaterial(None) removes it", Eventually(() => WindowsShell.Backdrop(hwnd) == WindowsBackdrop.None, step), WindowsShell.Backdrop(hwnd));

    // ITaskbarList3's thumbnail calls have no read-back; only a refusal is visible.
    var refusals = new StringWriter();
    var stderr = Console.Error;
    Console.SetError(refusals);
    w.SetThumbnailToolTip("Oriel thumbnail tooltip");
    w.SetThumbnailClip(new Rectangle(0, 0, 100, 50));
    w.SetThumbnailClip(default);
    w.SetThumbnailToolTip("");
    await Task.Delay(500);
    Console.SetError(stderr);
    Check("the shell takes the thumbnail clip and tooltip (weak: no read-back)", refusals.ToString().Length == 0, refusals.ToString().Trim());
    w.Destroy();
    await Task.Delay(300);
}

async Task NativeImageAsync()
{
    // A solid premultiplied BGRA square.
    static byte[] Solid(int size, byte b, byte g, byte r, byte a = 255)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
        return pixels;
    }
    static string Centre(byte[] bgra, int size) { var i = ((size / 2) * size + size / 2) * 4; return bgra.Length != size * size * 4 ? $"{bgra.Length} bytes" : $"{bgra[i]},{bgra[i + 1]},{bgra[i + 2]},{bgra[i + 3]}"; }

    // Decoding goes through CEF's codecs; the red PNG (220, 30, 30) comes from outside Oriel's encoder.
    var dir = Path.Combine(Path.GetTempPath(), "oriel-image-" + Path.GetRandomFileName());
    Directory.CreateDirectory(dir);
    var red = Path.Combine(dir, "icon.png");
    File.Copy(OverlayPng(), red);
    var fromPath = NativeImage.CreateFromPath(red);
    Check("CreateFromPath decodes a PNG", !fromPath.IsEmpty() && fromPath.GetSize() == (16, 16) && Centre(fromPath.ToBitmap(), 16) == "30,30,220,255", $"{fromPath.GetSize()} {Centre(fromPath.ToBitmap(), 16)}");
    File.WriteAllBytes(Path.Combine(dir, "icon@2x.png"), NativeImage.CreateFromBitmap(Solid(32, 255, 0, 0), new NativeImageBitmapOptions { Width = 32, Height = 32 }).ToPng());
    var hidpi = NativeImage.CreateFromPath(red);
    Check("CreateFromPath adds an @2x sibling as scale factor 2", hidpi.GetScaleFactors().SequenceEqual([1.0, 2.0]) && hidpi.GetSize(2) == (16, 16) && Centre(hidpi.ToBitmap(2), 32) == "255,0,0,255",
        $"{string.Join(",", hidpi.GetScaleFactors())} {hidpi.GetSize(2)} {Centre(hidpi.ToBitmap(2), 32)}");
    Check("CreateFromPath of a missing file is empty", NativeImage.CreateFromPath(Path.Combine(dir, "missing.png")).IsEmpty());
    var cropped = hidpi.Crop(new Rectangle(0, 0, 8, 4));
    Check("Crop keeps every scale factor, each at its own scale", cropped.GetScaleFactors().Count == 2 && cropped.GetSize() == (8, 4) && cropped.ToBitmap(2).Length == 16 * 8 * 4, $"{cropped.GetSize()} {cropped.ToBitmap(2).Length}");

    // Opaque and fully transparent pixels survive a PNG round trip exactly; partial alpha may round.
    var mixed = Solid(4, 10, 20, 30);
    Array.Clear(mixed, 0, 4);
    var image = NativeImage.CreateFromBitmap(mixed, new NativeImageBitmapOptions { Width = 4, Height = 4 });
    var png = image.ToPng();
    Check("ToPng writes a PNG that decodes to the same pixels", png is [0x89, 0x50, 0x4E, 0x47, ..] && NativeImage.CreateFromBuffer(png).ToBitmap().SequenceEqual(mixed), png.Length);
    var jpeg = NativeImage.CreateFromBitmap(Solid(8, 40, 80, 160), new NativeImageBitmapOptions { Width = 8, Height = 8 }).ToJpeg(95);
    var fromJpeg = NativeImage.CreateFromBuffer(jpeg);
    var jc = fromJpeg.ToBitmap();
    Check("ToJpeg writes a JPEG that decodes to about the same colour", jpeg is [0xFF, 0xD8, 0xFF, ..] && fromJpeg.GetSize() == (8, 8) && jc.Length == 8 * 8 * 4 && Math.Abs(jc[0] - 40) <= 6 && Math.Abs(jc[1] - 80) <= 6 && Math.Abs(jc[2] - 160) <= 6,
        $"{jpeg.Length} {fromJpeg.GetSize()} {(jc.Length == 8 * 8 * 4 ? Centre(jc, 8) : "no pixels")}");
    var url = image.ToDataUrl();
    Check("ToDataUrl and CreateFromDataUrl round-trip", url.StartsWith("data:image/png;base64,", StringComparison.Ordinal) && NativeImage.CreateFromDataUrl(url).ToBitmap().SequenceEqual(mixed), url.Length);
    Check("CreateFromDataUrl of a non-image is empty", NativeImage.CreateFromDataUrl("data:text/plain,hello").IsEmpty());
    Check("CreateFromBuffer takes a raw bitmap of the given size when it is not a PNG or JPEG",
        NativeImage.CreateFromBuffer(mixed, new NativeImageBitmapOptions { Width = 4, Height = 4, ScaleFactor = 2 }) is var raw && raw.GetScaleFactors().SequenceEqual([2.0]) && raw.GetSize(2) == (2, 2)
        && NativeImage.CreateFromBuffer([1, 2, 3]).IsEmpty());
    var added = NativeImage.CreateEmpty();
    added.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 1, Buffer = png });
    added.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, DataUrl = NativeImage.CreateFromBitmap(Solid(8, 1, 2, 3), new NativeImageBitmapOptions { Width = 8, Height = 8 }).ToDataUrl() });
    Check("AddRepresentation takes a PNG buffer and a data URL", added.GetScaleFactors().SequenceEqual([1.0, 2.0]) && added.GetSize(2) == (4, 4), string.Join(",", added.GetScaleFactors()));

    // Windows reads the icon back through WM_GETICON; CEF scales it to the system size, which keeps a solid colour.
    if (OperatingSystem.IsWindows())
    {
        static string Colour((byte B, byte G, byte R, byte A)? p) => p is { } c ? $"{c.B},{c.G},{c.R},{c.A}" : "none";
        var green = NativeImage.CreateFromBitmap(Solid(32, 0, 200, 0), new NativeImageBitmapOptions { Width = 32, Height = 32 });
        var w = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: icons", Width = 300, Height = 200, IconImage = green });
        await Task.Delay(1500);
        var hwnd = w.GetNativeWindowHandle();
        Check("the IconImage option sets the window icon (WM_GETICON)", Colour(WindowsShell.IconCentre(hwnd, big: true)) == "0,200,0,255", Colour(WindowsShell.IconCentre(hwnd, big: true)));
        w.SetIcon(NativeImage.CreateFromBitmap(Solid(32, 200, 0, 0), new NativeImageBitmapOptions { Width = 32, Height = 32 }));
        Check("SetIcon(NativeImage) replaces it", Eventually(() => Colour(WindowsShell.IconCentre(hwnd, big: true)) == "200,0,0,255", step), Colour(WindowsShell.IconCentre(hwnd, big: true)));
        w.SetIcon(red);
        Check("SetIcon(path) still works", Eventually(() => Colour(WindowsShell.IconCentre(hwnd, big: false)) == "30,30,220,255", step), Colour(WindowsShell.IconCentre(hwnd, big: false)));
        var pathed = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: icon path", Width = 300, Height = 200, Icon = red });
        await Task.Delay(1500);
        Check("the Icon option sets the window icon", Colour(WindowsShell.IconCentre(pathed.GetNativeWindowHandle(), big: true)) == "30,30,220,255", Colour(WindowsShell.IconCentre(pathed.GetNativeWindowHandle(), big: true)));
        pathed.Destroy();

        // The overlay has no read-back; an empty image is refused, which shows the image reaches the taskbar code.
        var refusals = new StringWriter();
        var stderr = Console.Error;
        Console.SetError(refusals);
        w.SetOverlayIcon(green, "Oriel overlay image test");
        await Task.Delay(500);
        var taken = refusals.ToString().Length == 0;
        w.SetOverlayIcon(NativeImage.CreateEmpty(), "empty");
        await Task.Delay(500);
        Console.SetError(stderr);
        Check("SetOverlayIcon(NativeImage) is taken, an empty image refused (weak: no read-back)", taken && refusals.ToString().Contains("overlay"), refusals.ToString().Trim());
        w.SetOverlayIcon(null, "");

        // The shell draws the thumbnail toolbar where nothing can read it; the clicks come back as WM_COMMAND.
        var clicks = new List<string>();
        var redIcon = NativeImage.CreateFromPath(red);
        var addedButtons = w.SetThumbarButtons([
            new ThumbarButton { Icon = green, Tooltip = "Play", Click = () => clicks.Add("play") },
            new ThumbarButton { Icon = redIcon, Tooltip = "Stop", Flags = ThumbarButtonFlags.DismissOnClick, Click = () => clicks.Add("stop") },
        ]);
        WindowsShell.ClickThumbButton(hwnd, 1);
        WindowsShell.ClickThumbButton(hwnd, 0);
        Check("SetThumbarButtons adds buttons whose clicks reach their handlers", addedButtons && clicks.SequenceEqual(["stop", "play"]), $"{addedButtons} {string.Join(",", clicks)}");
        clicks.Clear();
        var updated = w.SetThumbarButtons([new ThumbarButton { Icon = redIcon, Click = () => clicks.Add("next") }]);
        WindowsShell.ClickThumbButton(hwnd, 0);
        WindowsShell.ClickThumbButton(hwnd, 1);
        Check("a second SetThumbarButtons updates them; a removed button's handler is gone", updated && clicks.SequenceEqual(["next"]), $"{updated} {string.Join(",", clicks)}");
        Check("SetThumbarButtons refuses more than seven", !w.SetThumbarButtons([.. Enumerable.Range(0, 8).Select(_ => new ThumbarButton { Icon = green })]));
        clicks.Clear();
        var cleared = w.SetThumbarButtons([]);
        WindowsShell.ClickThumbButton(hwnd, 0);
        Check("SetThumbarButtons([]) removes them", cleared && clicks.Count == 0, $"{cleared} {string.Join(",", clicks)}");
        w.Destroy();
        await Task.Delay(300);
    }
    Directory.Delete(dir, recursive: true);
}
// A 16x16 red PNG, written once to a temp file.
static string OverlayPng()
{
    var path = Path.Combine(Path.GetTempPath(), "oriel-overlay-test.png");
    File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAdSURBVDhPY7gjJ/efEsyALkAqHjVg1IBRAwaLAQDHMhcfPZpcYgAAAABJRU5ErkJggg=="));
    return path;
}

// macOS: the Dock tile carries one progress bar for the whole app; read back from the tile's indicator.
#pragma warning disable CA1416 // Called only on macOS.
async Task MacTaskbarAsync()
{
    var tb = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: dock", Width = 300, Height = 200 });
    await Task.Delay(500);
    (double Value, bool Indeterminate)? Dock() => Oriel.Cef.CefThread.InvokeOnUi(Oriel.Cef.Platform.MacDock.Progress);
    tb.SetProgressBar(0.5);
    Check("SetProgressBar shows the value on the Dock icon", Eventually(() => Dock() is (0.5, false), step), Dock());
    tb.SetProgressBar(0.25, new ProgressBarOptions { Mode = ProgressBarMode.Error });
    Check("error mode still shows the value (macOS has no colours)", Eventually(() => Dock() is (0.25, false), step), Dock());
    tb.SetProgressBar(2);
    Check("above 1 the Dock bar cycles", Eventually(() => Dock() is (_, true), step), Dock());
    tb.SetProgressBar(-1);
    Check("below 0 removes the Dock bar", Eventually(() => Dock() is null, step), Dock());
    tb.SetProgressBar(0.5, new ProgressBarOptions { Mode = ProgressBarMode.None });
    Check("mode None removes it too", Eventually(() => Dock() is null, step), Dock());
    // A bounce only happens while the app is inactive and cannot be read back; this only shows the calls are safe.
    tb.FlashFrame(true);
    tb.FlashFrame(false);
    tb.Destroy();
    await Task.Delay(300);
}
#pragma warning restore CA1416

// Linux: read WM_HINTS and _NET_WM_STATE straight from the X server, independently of Oriel's own X calls.
async Task LinuxTaskbarAsync()
{
    var tb = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: taskbar", Width = 300, Height = 200 });
    await Task.Delay(800);
    var xid = tb.GetNativeWindowHandle();
    tb.FlashFrame(true);
    Check("FlashFrame(true) sets the urgency hint", Eventually(() => LinuxX11.IsUrgent(xid), step), xid);
    tb.FlashFrame(false);
    Check("FlashFrame(false) clears it", Eventually(() => !LinuxX11.IsUrgent(xid), step));
    tb.SetSkipTaskbar(true);
    Check("SetSkipTaskbar(true): the window manager adds _NET_WM_STATE_SKIP_TASKBAR", Eventually(() => LinuxX11.HasState(xid, "_NET_WM_STATE_SKIP_TASKBAR"), step));
    tb.SetSkipTaskbar(false);
    Check("SetSkipTaskbar(false) removes it", Eventually(() => !LinuxX11.HasState(xid, "_NET_WM_STATE_SKIP_TASKBAR"), step));
    var skipped = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: skip taskbar", Width = 300, Height = 200, SkipTaskbar = true });
    await Task.Delay(800);
    Check("the SkipTaskbar option applies when the window shows", Eventually(() => LinuxX11.HasState(skipped.GetNativeWindowHandle(), "_NET_WM_STATE_SKIP_TASKBAR"), step));
    skipped.Destroy();
    tb.Destroy();
    await Task.Delay(300);
}

async Task FramesAsync()
{
    var dir = Directory.CreateTempSubdirectory("oriel-frames-").FullName;
    var preload = Path.Combine(dir, "preload.js");
    // Runs in every frame: lets each frame send, and remember what it was sent.
    File.WriteAllText(preload, string.Join("\n",
        "const { ipcRenderer } = oriel;",
        "let onceCount = 0;",
        "const offListener = () => document.title = 'off-listener-ran';",
        "oriel.contextBridge.exposeInMainWorld('bridge', {",
        "  send: (m) => ipcRenderer.send('frame-test', m),",
        "  ask: () => ipcRenderer.invoke('ask'),",
        "  listen: () => ipcRenderer.on('to-frame', (_e, v) => document.title = 'got:' + v),",
        "  listenOnce: () => ipcRenderer.once('count', () => document.title = 'once:' + (++onceCount)),",
        "  listenThenOff: () => { ipcRenderer.on('off-test', offListener); ipcRenderer.off('off-test', offListener); },",
        "  removeAll: () => ipcRenderer.removeAllListeners('to-frame'),",
        "});"));
    var page = Path.Combine(dir, "frames.html");
    File.WriteAllText(page, "<!doctype html><title>top</title><iframe name=a srcdoc=\"<title>a</title><iframe name=b srcdoc='<title>b</title>'></iframe>\"></iframe>");
    var host = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: frames", Width = 400, Height = 300, WebPreferences = new WebPreferences { Preload = preload } });
    var domReady = 0;
    host.WebContents.DomReady += (_, _) => Interlocked.Increment(ref domReady);
    Fires(h => host.WebContents.DidFinishLoad += h, h => host.WebContents.DidFinishLoad -= h, () => host.WebContents.LoadFile(page), step);
    Check("WebContents.DomReady is raised for the page", Eventually(() => domReady == 1, step), domReady);

    var main = host.WebContents.MainFrame;
    Check("MainFrame is the page's top frame", main is { Parent: null } && main.Url == new Uri(page).AbsoluteUri && main.Top == main, main?.Url);
    Check("Frames lists the iframe", Eventually(() => main!.Frames is [{ Name: "a" }], step), string.Join(",", main!.Frames.Select(f => f.Name)));
    var a = main.Frames[0];
    Check("a nested iframe has its parent and top", Eventually(() => a.Frames is [{ Name: "b" } b] && b.Parent == a && b.Top == main, step));
    Check("FramesInSubtree includes the frame itself", main.FramesInSubtree.Select(f => f.Name).SequenceEqual(["", "a", "b"]), string.Join(",", main.FramesInSubtree.Select(f => f.Name)));
    Check("the same frame is the same object", main.Frames[0] == a && host.WebContents.MainFrame == main);

    using (var frameReady = new ManualResetEventSlim())
    {
        a.DomReady += (_, _) => frameReady.Set();
        a.Reload();
        Check("WebFrameMain.DomReady is raised when the frame loads", await Task.Run(() => frameReady.Wait(step)));
        Eventually(() => a.ExecuteJavaScriptAsync("1").Status != TaskStatus.Faulted, step);
    }
    var ran = await a.ExecuteJavaScriptAsync("window.name");
    Check("ExecuteJavaScriptAsync runs in that frame", ran?.ToString() == "a", ran);

    // IPC: the sender frame is the iframe; Send reaches only the frame it is called on.
    WebFrameMain? sender = null;
    using var heard = new ManualResetEventSlim();
    Action<IpcMainEvent> onMessage = e => { sender = e.SenderFrame; heard.Set(); };
    IpcMain.On("frame-test", onMessage);
    await a.ExecuteJavaScriptAsync("bridge.send('hello'); 1");
    Check("IpcMainEvent.SenderFrame is the frame that sent", await Task.Run(() => heard.Wait(step)) && sender == a, sender?.Name);
    IpcMain.RemoveListener("frame-test", onMessage);

    // ipcMain.once, handleOnce, removeAllListeners and event.reply.
    var onceHeard = 0;
    IpcMain.Once("frame-test", _ => Interlocked.Increment(ref onceHeard));
    await a.ExecuteJavaScriptAsync("bridge.send(1); bridge.send(2); 1");
    await Task.Delay(500);
    Check("IpcMain.Once hears one message only", onceHeard == 1, onceHeard);
    IpcMain.HandleOnce("ask", _ => System.Text.Json.Nodes.JsonValue.Create("first"));
    var firstAsk = await a.ExecuteJavaScriptAsync("bridge.ask()");
    var secondAsk = await a.ExecuteJavaScriptAsync("bridge.ask().then(() => 'answered', e => 'rejected')");
    Check("IpcMain.HandleOnce answers once, then no handler", firstAsk?.ToString() == "first" && secondAsk?.ToString() == "rejected", $"{firstAsk} {secondAsk}");
    var cleared = 0;
    IpcMain.On("frame-test", _ => Interlocked.Increment(ref cleared));
    IpcMain.RemoveAllListeners("frame-test");
    await a.ExecuteJavaScriptAsync("bridge.send(3); 1");
    await Task.Delay(500);
    Check("IpcMain.RemoveAllListeners", cleared == 0, cleared);
    await a.ExecuteJavaScriptAsync("bridge.listen(); 1");
    Action<IpcMainEvent> replyBack = e => e.Reply("to-frame", "pong");
    IpcMain.On("frame-test", replyBack);
    await a.ExecuteJavaScriptAsync("bridge.send(4); 1");
    Check("IpcMainEvent.Reply answers the frame that sent", Eventually(() => a.ExecuteJavaScriptAsync("document.title").Result?.ToString() == "got:pong", step));
    IpcMain.RemoveListener("frame-test", replyBack);

    // The page side: ipcRenderer.once, off and removeAllListeners.
    await a.ExecuteJavaScriptAsync("bridge.listenOnce(); bridge.listenThenOff(); 1");
    a.Send("count");
    a.Send("count");
    a.Send("off-test");
    await Task.Delay(500);
    Check("ipcRenderer.once hears one message; off removes a listener", (await a.ExecuteJavaScriptAsync("document.title"))?.ToString() == "once:1",
        await a.ExecuteJavaScriptAsync("document.title"));
    await a.ExecuteJavaScriptAsync("bridge.removeAll(); document.title = 'cleared'; 1");
    a.Send("to-frame", "late");
    await Task.Delay(500);
    Check("ipcRenderer.removeAllListeners", (await a.ExecuteJavaScriptAsync("document.title"))?.ToString() == "cleared", await a.ExecuteJavaScriptAsync("document.title"));
    await a.ExecuteJavaScriptAsync("bridge.listen(); 1");
    await main.ExecuteJavaScriptAsync("bridge.listen(); 1");
    a.Send("to-frame", "hi");
    Check("WebFrameMain.Send reaches that frame only", Eventually(() => a.ExecuteJavaScriptAsync("document.title").Result?.ToString() == "got:hi", step)
        && (await main.ExecuteJavaScriptAsync("document.title"))?.ToString() == "top");

    await main.ExecuteJavaScriptAsync("document.querySelector('iframe').remove(); 1");
    Check("a removed frame is detached", Eventually(() => a.Detached && a.IsDestroyed() && main.Frames.Count == 0, step));
    Check("script in a detached frame fails", await Task.Run(async () =>
    {
        try { await a.ExecuteJavaScriptAsync("1"); return false; }
        catch (InvalidOperationException) { return true; }
    }));

    host.Destroy();
    await Task.Delay(300);
    Directory.Delete(dir, recursive: true);
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
if (failures >= 0)
{
    // Checked once Run has returned: the final Quit must have raised WillQuit on its way out.
    Check("WillQuit is raised when the app quits", willQuit == 1, willQuit);
    Check("Exited is raised with the exit code, as Electron's quit event", exited is { ExitCode: 0 }, exited);
    failures = results.Count(r => r.StartsWith("FAIL", StringComparison.Ordinal));
}
Console.WriteLine(failures < 0 ? "RESULT: did not finish" : $"RESULT: {failures} failure(s)");
return failures;

static class MacInterop
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct NSPoint { public double X; public double Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct CGRect { public double X; public double Y; public double Width; public double Height; }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] internal static extern nint objc_getClass(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] internal static extern nint sel_registerName(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static extern NSPoint MouseLocation(nint cls, nint selector);
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] internal static extern uint CGMainDisplayID();
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] internal static extern CGRect CGDisplayBounds(uint display);
}

static class MacPanel
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [System.Runtime.InteropServices.DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [System.Runtime.InteropServices.DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint arg);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendEnd(nint receiver, nint selector, nint sheet, nint code);

    /// <summary>Answers the visible open or save sheet with OK or Cancel; false while none shows. Main thread only.</summary>
    internal static bool Answer(bool accept)
    {
        var panel = Find();
        if (panel == 0) return false;
        // The panel's controls run out of process and ignore posted events; ending its sheet is what its buttons do.
        var parent = Send(panel, sel_registerName("sheetParent"));
        if (parent == 0) return false;
        SendEnd(parent, sel_registerName("endSheet:returnCode:"), panel, accept ? 1 : 0);
        return true;
    }

    /// <summary>The visible open or save panel, or 0. Main thread only.</summary>
    internal static nint Find()
    {
        var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var windows = Send(app, sel_registerName("windows"));
        var savePanel = objc_getClass("NSSavePanel");
        var count = (int)Send(windows, sel_registerName("count"));
        for (var i = 0; i < count; i++)
        {
            var window = Send(windows, sel_registerName("objectAtIndex:"), i);
            if (Send(window, sel_registerName("isKindOfClass:"), savePanel) == 0) continue;
            if ((byte)Send(window, sel_registerName("isVisible")) == 0) continue;
            return window;
        }
        return 0;
    }
}

static class WinCursor
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
}

static class WinDialog
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] internal static extern nint FindWindowW(string? className, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}

static unsafe class LinuxX11
{
    private const string Lib = "libX11.so.6";
    private static nint s_display;

    private static nint Display() => s_display != 0 ? s_display : s_display = XOpenDisplay(null);

    internal static bool IsUrgent(nint window)
    {
        var hints = XGetWMHints(Display(), window);
        if (hints == null) return false;
        var urgent = (*hints & (1 << 8)) != 0;
        _ = XFree(hints);
        return urgent;
    }

    internal static bool HasState(nint window, string state)
    {
        var display = Display();
        var wanted = XInternAtom(display, state, 0);
        if (XGetWindowProperty(display, window, XInternAtom(display, "_NET_WM_STATE", 0), 0, 64, 0, 4 /* XA_ATOM */,
            out _, out _, out var count, out _, out var data) != 0 || data == null) return false;
        var found = new ReadOnlySpan<nint>(data, (int)count).Contains(wanted);
        _ = XFree(data);
        return found;
    }

    [System.Runtime.InteropServices.DllImport(Lib)] private static extern nint XOpenDisplay(byte* name);
    // The first field of XWMHints is its flags.
    [System.Runtime.InteropServices.DllImport(Lib)] private static extern long* XGetWMHints(nint display, nint window);
    [System.Runtime.InteropServices.DllImport(Lib)] private static extern int XFree(void* data);
    [System.Runtime.InteropServices.DllImport(Lib)] private static extern nint XInternAtom(nint display, string name, int onlyIfExists);
    [System.Runtime.InteropServices.DllImport(Lib)]
    private static extern int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length, int delete, nint type,
        out nint actualType, out int actualFormat, out nuint count, out nuint bytesAfter, out nint* data);
}
