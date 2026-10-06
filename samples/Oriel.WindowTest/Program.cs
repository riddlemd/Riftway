using Oriel.Cef.Platform;
using System.ComponentModel;
using Oriel;

// End-to-end BrowserWindow check, driven from C#: each step issues a command from a worker thread
// and waits for both the event and the state. Exit code = failures (-1 when the run never finishes).
// The process checks crash or hang a process of their own, started with ProcessChild.
const string ProcessChild = "--oriel-process-child";
if (args.Length >= 2 && args[0] == ProcessChild) return ProcessChildMain(args[1], args.Length > 2 ? args[2] : "");
var app = OrielApp.Create("Oriel.WindowTest");
// Chromium's DevTools server on a free port: a switch only Chromium acts on, read back over HTTP.
var debugPort = FreePort();
app.CommandLine.AppendSwitch("remote-debugging-port", debugPort.ToString());
var failures = -1;
var results = new List<string>();
var step = TimeSpan.FromSeconds(5);
var simpleReady = false;
BrowserWindow? childAlive = null;
var willQuit = 0;
KeyboardEvent? saveKeys = null;
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
    // The first load also starts the renderer process, which takes longer than step on a loaded machine.
    var loaded = Fires(h => win.WebContents.DidFinishLoad += h, h => win.WebContents.DidFinishLoad -= h,
        () => win.WebContents.LoadFile(page, new Dictionary<string, string> { ["q"] = "1 2" }, "top"), step * 4);
    Check("LoadFile raises DidFinishLoad", loaded);
    var navigatorAgent = (await win.WebContents.ExecuteJavaScriptAsync("navigator.userAgent"))?.ToString();
    Check("the default user agent is Electron's format, with the app's and Oriel's tokens",
        navigatorAgent == app.UserAgentFallback && navigatorAgent.Contains(" Oriel.WindowTest/") && navigatorAgent.Contains(" Oriel/")
        && win.WebContents.GetUserAgent() == navigatorAgent, navigatorAgent);
    var fresh = new BrowserWindow(new BrowserWindowOptions { Show = false });
    Check("GetUserAgent answers before the page has loaded", fresh.WebContents.GetUserAgent() == app.UserAgentFallback, fresh.WebContents.GetUserAgent());
    fresh.Destroy();
    var original = app.UserAgentFallback;
    app.UserAgentFallback = "OrielFallbackTest/1.0";
    var later = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await later.WebContents.LoadFile(page);
    var laterAgent = (await later.WebContents.ExecuteJavaScriptAsync("navigator.userAgent"))?.ToString();
    Check("a fallback changed after start-up applies to new pages, as Electron", laterAgent == "OrielFallbackTest/1.0"
        && later.WebContents.GetUserAgent() == "OrielFallbackTest/1.0", laterAgent);
    later.Destroy();
    app.UserAgentFallback = original;
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
#pragma warning disable CA1416 // macOS only, inside the IsMacOS check
    if (OperatingSystem.IsMacOS())
    {
        win.SetAlwaysOnTop(true, "status", 1);
        Check("SetAlwaysOnTop level and relativeLevel set the window level, as Electron",
            Eventually(() => Oriel.Cef.CefThread.InvokeOnUi(() => Oriel.Cef.Platform.MacWindow.GetLevel(win.GetNativeWindowHandle())) == 26, step) && win.IsAlwaysOnTop,
            Oriel.Cef.CefThread.InvokeOnUi(() => Oriel.Cef.Platform.MacWindow.GetLevel(win.GetNativeWindowHandle())));
        win.SetAlwaysOnTop(false);
        Check("SetAlwaysOnTop(false) returns to the normal level",
            Eventually(() => Oriel.Cef.CefThread.InvokeOnUi(() => Oriel.Cef.Platform.MacWindow.GetLevel(win.GetNativeWindowHandle())) == 0, step) && !win.IsAlwaysOnTop);

        // AppKit animates the frame on the main thread, so a query right after waits for the animation to end.
        var start = win.GetBounds();
        var target = start with { X = start.X + 120, Width = start.Width + 80 };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        win.SetBounds(target, animate: true);
        var landed = win.GetBounds();
        var animated = timer.ElapsedMilliseconds;
        win.SetBounds(start);
        timer.Restart();
        win.GetBounds();
        var instant = timer.ElapsedMilliseconds;
        Check("SetBounds with animate animates the move on macOS, as Electron", landed == target && animated >= 100 && instant < 100,
            $"{landed} animated={animated}ms instant={instant}ms");
        win.SetBounds(start);
        Eventually(() => win.GetBounds() == start, step);
    }
#pragma warning restore CA1416

    Check("Maximize raises Maximized", Fires(h => win.Maximized += h, h => win.Maximized -= h, win.Maximize, step));
    Check("IsMaximized", Eventually(() => win.IsMaximized, step), win.IsMaximized);
    Check("Unmaximize raises Unmaximized", Fires(h => win.Unmaximized += h, h => win.Unmaximized -= h, win.Unmaximize, step));
    Check("not maximized", Eventually(() => !win.IsMaximized, step), win.IsMaximized);

    Check("Minimize raises Minimized", Fires(h => win.Minimized += h, h => win.Minimized -= h, win.Minimize, step));
    Check("IsMinimized", Eventually(() => win.IsMinimized, step), win.IsMinimized);
    Check("Restore raises Restored", Fires(h => win.Restored += h, h => win.Restored -= h, win.Restore, step));
    Check("not minimized", Eventually(() => !win.IsMinimized, step), win.IsMinimized);
    if (OperatingSystem.IsWindows()) await WindowStateEventsAsync(win);

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
    await MouseEventsAsync();
    await InputEventAsync();
    await PreferredSizeAsync();
    await WindowOpenAsync(win);
    await LoadAndSizeAsync(win);
    await NavigationAsync();
    await FramesAsync();
    await TaskbarAsync(win);
    await CommandLineAsync();
    await ProcessChildrenAsync();
    await BadgeAsync(win);
    await WindowsApisAsync();
    await TransparencyAsync();
    await WindowsStateChecks.RunAsync(Check, step);
    await ShellApisAsync();
    await NativeImageAsync();
    await WebContentsLifecycleAsync(win);
    await PageEventsAsync();
    await MessagePortsAsync();
    await MacWindowApisAsync();
    await WebFrameAsync();
    await MenuRolesAsync(win);
    await WebContentsMethodsAsync();
    await MessageBoxTest.RunAsync(win, Check, step);
    await ShellAsync();
    await ClipboardAsync();
    await WindowPolishAsync(win);
    await TitleBarAsync(win);
    await SystemPreferencesChecks.RunAsync(Check, step);
    await StartDragTests.RunAsync(Check);
    if (OperatingSystem.IsWindows()) await SafeStorageAsync();
    if (OperatingSystem.IsWindows()) await NotificationAsync();
    await PowerAndShortcutsAsync();
    await TrayAsync();

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
        <div><span id=narrow>iiiiiiiiii</span></div><div><span id=wide>MMMMMMMMMM</span></div><div><span id=math style='font-family:math'>iiiiiiiiii</span></div>
        """);
    var monospace = OperatingSystem.IsMacOS() ? "Menlo" : OperatingSystem.IsWindows() ? "Consolas" : "Liberation Mono";
    var serif = OperatingSystem.IsLinux() ? "Liberation Serif" : "Times New Roman";
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
            DefaultFontFamily = new DefaultFontFamily { Standard = monospace, Math = serif },
        },
    });
    var prefsLoaded = Fires(h => prefs.WebContents.DidFinishLoad += h, h => prefs.WebContents.DidFinishLoad -= h,
        () => prefs.WebContents.LoadFile(Path.Combine(dir, "prefs.html")), step);
    Check("preferences window loads", prefsLoaded);
    Check("ZoomFactor preference", Eventually(() => Math.Abs(prefs.WebContents.GetZoomFactor() - 1.5) < 0.01, step), prefs.WebContents.GetZoomFactor());
    Check("ZoomFactor preference reaches the page", Js(prefs, "devicePixelRatio").Result is string pr && Math.Abs(double.Parse(pr, System.Globalization.CultureInfo.InvariantCulture) / dpr - 1.5) < 0.01, Js(prefs, "devicePixelRatio").Result);
    // As Electron, a zoom the app set, back to 100% included, stays for that origin.
    prefs.WebContents.SetZoomLevel(0);
    Fires(h => prefs.WebContents.DidFinishLoad += h, h => prefs.WebContents.DidFinishLoad -= h, prefs.WebContents.Reload, step);
    await Task.Delay(300);
    Check("a zoom reset to 100% stays after a reload", Math.Abs(prefs.WebContents.GetZoomFactor() - 1.0) < 0.01, prefs.WebContents.GetZoomFactor());
    Check("Images = false", await Js(prefs, "document.getElementById('img').naturalWidth") is "0", await Js(prefs, "document.getElementById('img').naturalWidth"));
    Check("WebGL = false", await Js(prefs, "document.createElement('canvas').getContext('webgl') === null") is "true");
    Check("TextAreasAreResizable = false", await Js(prefs, "getComputedStyle(document.getElementById('t')).resize") is "\"none\"", await Js(prefs, "getComputedStyle(document.getElementById('t')).resize"));
    Check("DefaultFontSize", await Js(prefs, "getComputedStyle(document.getElementById('p')).fontSize") is "\"20px\"", await Js(prefs, "getComputedStyle(document.getElementById('p')).fontSize"));
    Check("DefaultMonospaceFontSize", await Js(prefs, "getComputedStyle(document.getElementById('c')).fontSize") is "\"17px\"", await Js(prefs, "getComputedStyle(document.getElementById('c')).fontSize"));
    Check("DefaultFontFamily.Standard", await Js(prefs, "document.getElementById('narrow').offsetWidth === document.getElementById('wide').offsetWidth") is "true",
        await Js(prefs, "[document.getElementById('narrow').offsetWidth, document.getElementById('wide').offsetWidth].join()"));
    // A proportional math font makes the row of i narrower than the standard (monospace) one.
    Check("DefaultFontFamily.Math", await Js(prefs, "document.getElementById('math').offsetWidth < document.getElementById('narrow').offsetWidth") is "true",
        await Js(prefs, "[document.getElementById('math').offsetWidth, document.getElementById('narrow').offsetWidth].join()"));
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

    // Display changes cannot be made here; subscribing starts watching and a steady setup raises nothing.
    var raised = 0;
    EventHandler<DisplayMetricsChangedEventArgs> onChanged = (_, _) => raised++;
    EventHandler<DisplayEventArgs> onAdded = (_, _) => raised++;
    Screen.DisplayMetricsChanged += onChanged;
    Screen.DisplayAdded += onAdded;
    var checksBefore = Screen.ChecksRun;
    await Task.Delay(2500);
    if (OperatingSystem.IsMacOS())
        Check("macOS: displays are not polled", Screen.ChecksRun == checksBefore, Screen.ChecksRun - checksBefore);
    Screen.DisplayMetricsChanged -= onChanged;
    Screen.DisplayAdded -= onAdded;
    Check("no display events while nothing changes", raised == 0, raised);

    if (OperatingSystem.IsMacOS())
    {
        await MacDisplayFieldsAsync(primary);
        MacDisplayEvents(primary);
    }
    if (OperatingSystem.IsWindows()) await WindowsDisplayAsync(win);
}

// The Display fields Chromium's BuildDisplayForScreen fills, against system_profiler, AppKit and Chromium's own chrome://gpu.
async Task MacDisplayFieldsAsync(Display primary)
{
    var profiler = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/sbin/system_profiler", "SPDisplaysDataType -json") { RedirectStandardOutput = true })!;
    var json = System.Text.Json.JsonDocument.Parse(profiler.StandardOutput.ReadToEnd());
    profiler.WaitForExit();
    var main = json.RootElement.GetProperty("SPDisplaysDataType").EnumerateArray()
        .SelectMany(gpu => gpu.TryGetProperty("spdisplays_ndrvs", out var d) ? d.EnumerateArray() : Enumerable.Empty<System.Text.Json.JsonElement>())
        .FirstOrDefault(d => d.TryGetProperty("spdisplays_main", out var m) && m.GetString() == "spdisplays_yes");
    string Field(string name) => main.ValueKind == System.Text.Json.JsonValueKind.Object && main.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    Check("macOS: the primary display's id is its CGDirectDisplayID (system_profiler)", Field("_spdisplays_displayID") == primary.Id.ToString(), $"{primary.Id} vs {Field("_spdisplays_displayID")}");
    Check("macOS: internal matches system_profiler's connection type", primary.Internal == (Field("spdisplays_connection_type") == "spdisplays_internal"), $"{primary.Internal} vs {Field("spdisplays_connection_type")}");
    var hz = System.Text.RegularExpressions.Regex.Match(Field("_spdisplays_resolution"), @"@ ([\d.]+)Hz");
    Check("macOS: displayFrequency matches system_profiler's refresh rate", hz.Success && Math.Abs(primary.DisplayFrequency - double.Parse(hz.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) < 0.01,
        $"{primary.DisplayFrequency} vs {Field("_spdisplays_resolution")}");
    var (label, gray) = Oriel.Cef.CefThread.InvokeOnUi(() => (MacInterop.ScreenName((uint)primary.Id), MacInterop.CGDisplayUsesForceToGray() != 0));
    Check("macOS: label is NSScreen's localizedName", primary.Label.Length > 0 && primary.Label == label, $"{primary.Label} vs {label}");
    Check("macOS: monochrome is CoreGraphics' force-to-gray", primary.Monochrome == gray, primary.Monochrome);
    Check("macOS: fields Chromium leaves unset", primary is { TouchSupport: DisplayFeatureSupport.Unknown, AccelerometerSupport: DisplayFeatureSupport.Unknown, Detected: true }
        && primary.NativeOrigin == new Point(0, 0) && primary.MaximumCursorSize == (0, 0), primary);

    // chrome://gpu prints the display as Chromium built it, from the same CEF process.
    var gpu = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await gpu.LoadUrl("chrome://gpu");
    var text = "";
    Eventually(() =>
    {
        const string walk = "(function t(n){let s='';for(const c of [...(n.shadowRoot?[n.shadowRoot]:[]),...n.childNodes])s+=c.nodeType===3?c.textContent+'\\n':t(c);return s})(document.body)";
        text = gpu.WebContents.ExecuteJavaScriptAsync(walk).GetAwaiter().GetResult()?.ToString() ?? "";
        return text.Contains($"Display[{primary.Id}]") && text.Contains("Bits per pixel");
    }, step);
    gpu.Destroy();
    var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    var start = lines.FindIndex(l => l.Contains($"Display[{primary.Id}]"));
    var block = start < 0 ? [] : lines.Skip(start).TakeWhile((l, i) => i == 0 || !l.Contains("Display[")).ToList();
    // Each label and value is its own text node, with a ":" node between them.
    string After(Func<string, bool> label) => block.SkipWhile(l => !label(l)).Skip(1).FirstOrDefault(l => l != ":") ?? "";
    var info = start < 0 ? "" : lines[start];
    Check("macOS: internal and detected match chrome://gpu", info.Contains(primary.Internal ? " internal " : " external ") && info.EndsWith(" detected", StringComparison.Ordinal), info);
    // The WCG/alpha configuration is the one Electron prints: a group named all, SDR, WCG or listing it.
    var colorSpace = After(l => l is "Color space (all)" or "Color space (SDR)" or "Color space (WCG)" || (l.StartsWith("Color space (", StringComparison.Ordinal) && l.Contains("WCG/alpha")));
    Check("macOS: colorSpace matches chrome://gpu", colorSpace.Length > 0 && primary.ColorSpace == colorSpace, $"{primary.ColorSpace} vs {colorSpace}");
    Check("macOS: colorDepth and depthPerComponent match chrome://gpu",
        After(l => l == "Bits per pixel") == primary.ColorDepth.ToString() && After(l => l == "Bits per color component") == primary.DepthPerComponent.ToString(),
        $"{primary.ColorDepth}/{primary.DepthPerComponent} vs {After(l => l == "Bits per pixel")}/{After(l => l == "Bits per color component")}");
    Check("macOS: displayFrequency matches chrome://gpu", float.TryParse(After(l => l == "Refresh Rate in Hz"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate)
        && rate == (float)primary.DisplayFrequency, $"{primary.DisplayFrequency} vs {After(l => l == "Refresh Rate in Hz")}");
}

// No display can be attached or moved here, so each check stages the change in the last snapshot and then posts
// the notification AppKit sends; what arrives proves the notification path end to end, not that AppKit sends it.
void MacDisplayEvents(Display primary)
{
    var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
    EventHandler<DisplayMetricsChangedEventArgs> onChanged = (_, e) => events.Enqueue($"changed {e.Display.Id} {e.ChangedMetrics}");
    EventHandler<DisplayEventArgs> onAdded = (_, e) => events.Enqueue($"added {e.Display.Id}");
    EventHandler<DisplayEventArgs> onRemoved = (_, e) => events.Enqueue($"removed {e.Display.Id}");
    Screen.DisplayMetricsChanged += onChanged;
    Screen.DisplayAdded += onAdded;
    Screen.DisplayRemoved += onRemoved;
    try
    {
        string[] Post(Func<IReadOnlyList<Display>, IReadOnlyList<Display>> stage, int expected)
        {
            events.Clear();
            Oriel.Cef.CefThread.InvokeOnUi(() =>
            {
                Screen.EditSnapshot(stage);
                Oriel.Cef.Platform.MacScreen.PostScreenParametersChanged();
                return 0;
            });
            Eventually(() => events.Count >= expected, step);
            Thread.Sleep(300);
            return [.. events];
        }
        var checks = Screen.ChecksRun;
        var none = Post(list => list, 0);
        Check("macOS: NSApplicationDidChangeScreenParametersNotification triggers a display check", Eventually(() => Screen.ChecksRun > checks, step) && none.Length == 0, $"{Screen.ChecksRun - checks} checks, [{string.Join(", ", none)}]");
        var moved = Post(list => [.. list.Select(d => d.Id == primary.Id ? d with { Bounds = d.Bounds with { X = d.Bounds.X + 7 }, ScaleFactor = d.ScaleFactor + 1 } : d)], 1);
        Check("macOS: a metrics change arrives through the notification", moved is [var m] && m == $"changed {primary.Id} {DisplayMetrics.Bounds | DisplayMetrics.ScaleFactor}", string.Join(", ", moved));
        var recolored = Post(list => [.. list.Select(d => d.Id == primary.Id ? d with { ColorSpace = "staged" } : d)], 1);
        Check("macOS: a color-space change arrives with no changed metrics", recolored is [var c] && c == $"changed {primary.Id} None", string.Join(", ", recolored));
        var swapped = Post(list => [.. list.Where(d => d.Id != primary.Id), primary with { Id = 999_999 }], 2);
        Check("macOS: removals arrive before additions", swapped.SequenceEqual(["removed 999999", $"added {primary.Id}"]), string.Join(", ", swapped));
    }
    finally
    {
        Screen.DisplayMetricsChanged -= onChanged;
        Screen.DisplayAdded -= onAdded;
        Screen.DisplayRemoved -= onRemoved;
    }
}

// Electron raises these from WM_SIZE (native_window_views_win.cc HandleSizeEvent). Each step waits 600 ms, past the
// 250 ms poll, so a late or duplicate event would show; latency runs from the call to the last event.
async Task WindowStateEventsAsync(BrowserWindow win)
{
    var log = new List<(string Name, long Ms)>();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    EventHandler Record(string name) => (_, _) => { lock (log) log.Add((name, clock.ElapsedMilliseconds)); };
    EventHandler onMaximize = Record("maximize"), onUnmaximize = Record("unmaximize"), onMinimize = Record("minimize"), onRestore = Record("restore");
    win.Maximized += onMaximize; win.Unmaximized += onUnmaximize; win.Minimized += onMinimize; win.Restored += onRestore;
    var latencies = new List<long>();
    async Task<string> Step(Action act, Func<bool> settled)
    {
        lock (log) log.Clear();
        var start = clock.ElapsedMilliseconds;
        act();
        Eventually(settled, step);
        await Task.Delay(600);
        lock (log)
        {
            if (log.Count > 0) latencies.Add(log.Max(e => e.Ms) - start);
            return string.Join(",", log.Select(e => e.Name));
        }
    }
    try
    {
        var maximize = await Step(win.Maximize, () => win.IsMaximized);
        var unmaximize = await Step(win.Unmaximize, () => !win.IsMaximized);
        var minimize = await Step(win.Minimize, () => win.IsMinimized);
        var restore = await Step(win.Restore, () => !win.IsMinimized);
        Check("Windows: maximize, unmaximize, minimize, restore each raise their event exactly once",
            (maximize, unmaximize, minimize, restore) == ("maximize", "unmaximize", "minimize", "restore"), $"{maximize} | {unmaximize} | {minimize} | {restore}");

        var maximizedAgain = await Step(win.Maximize, () => win.IsMaximized);
        var minimizedFromMaximized = await Step(win.Minimize, () => win.IsMinimized);
        var restoredToMaximized = await Step(win.Restore, () => win.IsMaximized);
        Check("Windows: restoring a minimized maximized window raises restore then maximize, as Electron",
            (maximizedAgain, minimizedFromMaximized, restoredToMaximized) == ("maximize", "minimize", "restore,maximize"), $"{maximizedAgain} | {minimizedFromMaximized} | {restoredToMaximized}");
        var leftMaximized = await Step(win.Unmaximize, () => !win.IsMaximized);

        var minimizedFromNormal = await Step(win.Minimize, () => win.IsMinimized);
        var maximizedFromMinimized = await Step(win.Maximize, () => win.IsMaximized);
        var unmaximizedAfter = await Step(win.Unmaximize, () => !win.IsMaximized);
        Check("Windows: maximize() on a minimized window raises restore then maximize; unmaximize follows once",
            (leftMaximized, minimizedFromNormal, maximizedFromMinimized, unmaximizedAfter) == ("unmaximize", "minimize", "restore,maximize", "unmaximize"),
            $"{leftMaximized} | {minimizedFromNormal} | {maximizedFromMinimized} | {unmaximizedAfter}");
        Check("Windows: state events arrive within 100 ms of the call (the poll could take 250 ms)",
            latencies.Count == 11 && latencies.Max() < 100, $"max {(latencies.Count > 0 ? latencies.Max() : -1)} ms, mean {(latencies.Count > 0 ? latencies.Average() : -1):F0} ms over {latencies.Count}");

        // The window stays zoomed through full screen, so both ways send SIZE_MAXIMIZED; Electron sets its state from
        // SetFullScreen before that, so each raises maximize again (native_window_views.cc SetFullScreen).
        var maximizedBeforeFull = await Step(win.Maximize, () => win.IsMaximized);
        var intoFull = await Step(() => win.SetFullScreen(true), () => win.IsFullScreen);
        var outOfFull = await Step(() => win.SetFullScreen(false), () => !win.IsFullScreen && win.IsMaximized);
        var unmaximizedAfterFull = await Step(win.Unmaximize, () => !win.IsMaximized);
        Check("Windows: entering and leaving full screen from maximized each raise maximize, as Electron (no unmaximize)",
            (maximizedBeforeFull, intoFull, outOfFull, unmaximizedAfterFull) == ("maximize", "maximize", "maximize", "unmaximize"),
            $"{maximizedBeforeFull} | {intoFull} | {outOfFull} | {unmaximizedAfterFull}");
    }
    finally
    {
        win.Maximized -= onMaximize; win.Unmaximized -= onUnmaximize; win.Minimized -= onMinimize; win.Restored -= onRestore;
    }
}

// Display fields against Windows read on the test's side (GDI, DisplayConfig, WMI) and against Chromium's own view in the
// page; display events through Oriel's observer window, synthesized and from a real work-area change.
async Task WindowsDisplayAsync(BrowserWindow win)
{
    var displays = Screen.GetAllDisplays();
    var probes = displays.Select(d =>
    {
        var centre = Screen.DipToScreenPoint(new Point(d.Bounds.X + d.Bounds.Width / 2, d.Bounds.Y + d.Bounds.Height / 2));
        return (Display: d, Monitor: WindowsDisplayProbe.At(centre.X, centre.Y));
    }).ToList();
    static bool InternalTech(uint? t) => t is 0x80000000 or 11 or 13;
    var firstInternal = probes.FirstOrDefault(p => InternalTech(p.Monitor.OutputTechnology)).Display;
    string Describe(Func<(Display Display, WindowsDisplayProbe.Monitor Monitor), object?> pick) => string.Join("; ", probes.Select(p => $"{p.Monitor.Device}: {pick(p)}"));

    Check("Windows: Display.Label is DisplayConfig's EDID friendly name (empty without one)", probes.All(p => p.Display.Label == (p.Monitor.EdidName ?? "")),
        Describe(p => $"'{p.Display.Label}' vs '{p.Monitor.EdidName}'"));
    var wmiNames = WindowsDisplayProbe.Wmi("Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorID | ForEach-Object { -join ($_.UserFriendlyName | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) }");
    Check("Windows: every non-empty label is a WMI WmiMonitorID friendly name", probes.All(p => p.Display.Label.Length == 0 || wmiNames.Contains(p.Display.Label)),
        $"{Describe(p => p.Display.Label)} vs WMI [{string.Join(", ", wmiNames)}]");
    Check("Windows: Display.DisplayFrequency is EnumDisplaySettings' dmDisplayFrequency", probes.All(p => p.Display.DisplayFrequency == p.Monitor.Frequency && p.Monitor.Frequency > 0),
        Describe(p => $"{p.Display.DisplayFrequency} vs {p.Monitor.Frequency}"));
    var wmiRates = WindowsDisplayProbe.Wmi("Get-CimInstance Win32_VideoController | ForEach-Object { $_.CurrentRefreshRate }");
    Check("Windows: the refresh rates agree with WMI's Win32_VideoController.CurrentRefreshRate", probes.All(p => wmiRates.Contains(p.Display.DisplayFrequency.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        $"{Describe(p => p.Display.DisplayFrequency)} vs WMI [{string.Join(", ", wmiRates)}]");
    Check("Windows: Display.Internal marks the first internal or embedded output (DisplayConfig output technology)",
        probes.All(p => p.Display.Internal == (firstInternal is not null && p.Display.Id == firstInternal.Id)), Describe(p => $"{p.Display.Internal} tech 0x{p.Monitor.OutputTechnology:X}"));
    var wmiTech = WindowsDisplayProbe.Wmi("Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorConnectionParams | ForEach-Object { [uint32]$_.VideoOutputTechnology }");
    Check("Windows: an internal display exactly when WMI reports an internal or embedded connection",
        displays.Any(d => d.Internal) == wmiTech.Any(t => uint.TryParse(t, out var v) && InternalTech(v)), $"[{string.Join(", ", wmiTech)}]");
    Check("Windows: Display.NativeOrigin is the monitor's pixel origin over its scale, floored",
        probes.All(p => p.Display.NativeOrigin == new Point((int)Math.Floor(p.Monitor.Left / p.Display.ScaleFactor), (int)Math.Floor(p.Monitor.Top / p.Display.ScaleFactor))),
        Describe(p => $"{p.Display.NativeOrigin} from ({p.Monitor.Left},{p.Monitor.Top}) at {p.Display.ScaleFactor}"));

    // Chromium's own numbers reach the page: screen.colorDepth, the (color) and (monochrome) media features.
    var mine = Screen.GetDisplayMatching(win.GetBounds());
    var pageDepth = await Js(win, "screen.colorDepth + ',' + screen.pixelDepth");
    var perComponent = await Js(win, $"matchMedia('(color: {mine.DepthPerComponent})').matches && matchMedia('(monochrome: 0)').matches");
    Check("Windows: ColorDepth and DepthPerComponent are what Chromium gives the page (screen.colorDepth, (color: n))",
        pageDepth?.ToString() == $"\"{mine.ColorDepth},{mine.ColorDepth}\"" && perComponent?.ToString() == "true", $"{mine.ColorDepth}/{mine.DepthPerComponent} vs page {pageDepth} {perComponent}");
    var icc = probes.First(p => p.Display.Id == mine.Id).Monitor.IccPath;
    // The profile is found and read here, on the test's side; the primaries arithmetic is unit-tested.
    var expectedSpace = mine.ColorDepth == 30 ? "{primaries:P3, transfer:SRGB, matrix:RGB, range:FULL}"
        : icc is null ? "{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}" : DisplayMapping.SdrColorSpace(File.ReadAllBytes(icc));
    Check("Windows: ColorSpace is sRGB without an ICC profile, else the primaries of the profile GetICMProfile names",
        mine.ColorSpace == expectedSpace, $"{mine.ColorSpace} (profile {icc ?? "none"})");
    Check("Windows: the fields Chromium leaves at their defaults on Windows",
        displays.All(d => !d.Monochrome && d.TouchSupport == DisplayFeatureSupport.Unknown && d.AccelerometerSupport == DisplayFeatureSupport.Unknown && d.Detected && d.MaximumCursorSize == (0, 0)));

    var observer = WindowsDisplayProbe.ObserverWindow();
    Check("Windows: display changes are heard by a hidden top-level window of this process (broadcasts skip message-only windows)",
        observer != 0 && WindowsDisplayProbe.IsTopLevel(observer) && !WindowsDisplayProbe.IsVisible(observer), observer);
    var events = new List<(string Name, Display Display, long Ms)>();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    EventHandler<DisplayEventArgs> onAdded = (_, e) => { lock (events) events.Add(("added", e.Display, clock.ElapsedMilliseconds)); };
    EventHandler<DisplayEventArgs> onRemoved = (_, e) => { lock (events) events.Add(("removed", e.Display, clock.ElapsedMilliseconds)); };
    EventHandler<DisplayMetricsChangedEventArgs> onChanged = (_, e) => { lock (events) events.Add(($"metrics:{e.ChangedMetrics}", e.Display, clock.ElapsedMilliseconds)); };
    string Names() { lock (events) return string.Join(",", events.Select(e => e.Name)); }
    Screen.DisplayAdded += onAdded; Screen.DisplayRemoved += onRemoved; Screen.DisplayMetricsChanged += onChanged;
    var original = WindowsDisplayProbe.WorkArea();
    try
    {
        var dpiRect = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        System.Runtime.InteropServices.Marshal.Copy(new int[4], 0, dpiRect, 4);
        WindowsDisplayProbe.Send(observer, 0x007E /* WM_DISPLAYCHANGE */, 32, (nint)((1080 << 16) | 1920));
        WindowsDisplayProbe.Send(observer, 0x001A /* WM_SETTINGCHANGE */, 0x002F /* SPI_SETWORKAREA */, 0);
        WindowsDisplayProbe.Send(observer, 0x02E0 /* WM_DPICHANGED */, 0x00600060, dpiRect);
        WindowsDisplayProbe.Send(observer, 0x001C /* WM_ACTIVATEAPP */, 1, 0);
        await Task.Delay(600);
        System.Runtime.InteropServices.Marshal.FreeHGlobal(dpiRect);
        Check("Windows: synthesized display messages with nothing changed raise no display events, as Chromium's diff", Names().Length == 0, Names());

        // An app bar makes Explorer shrink the work area and broadcast WM_SETTINGCHANGE(SPI_SETWORKAREA); removing it undoes both.
        var primary = Screen.GetPrimaryDisplay();
        var start = clock.ElapsedMilliseconds;
        var bar = WindowsDisplayProbe.ReserveTopEdge(original.Right - original.Left, 24);
        try
        {
            Eventually(() => Names().Length > 0, step);
            await Task.Delay(800);
        }
        finally { bar.Dispose(); }
        Eventually(() => Names().Split(',').Length >= 2, step);
        await Task.Delay(800);
        (string Name, Display Display, long Ms)[] seen;
        lock (events) seen = [.. events];
        string Seen() => string.Join(" ", seen.Select(e => $"{e.Name}:{e.Display.WorkArea}@{e.Ms - start}ms"));
        Check("Windows: a real work-area change (an app bar, SPI_SETWORKAREA broadcast) raises display-metrics-changed ['workArea'] for the primary with the new area",
            seen.Length > 0 && seen[0].Name == "metrics:WorkArea" && seen[0].Display.Id == primary.Id && seen[0].Display.WorkArea.Y > primary.WorkArea.Y && seen[0].Display.Bounds == primary.Bounds,
            $"{Seen()} (was {primary.WorkArea})");
        Check("Windows: removing it raises it exactly once more, back to the original, and nothing else", seen.Length == 2 && seen[1].Name == "metrics:WorkArea" && seen[1].Display.WorkArea == primary.WorkArea,
            Seen());
        Check("Windows: the work-area event comes as Chromium hears the broadcast (under 100 ms)", seen.Length > 0 && seen[0].Ms - start < 100, Seen());    }
    finally
    {
        Screen.DisplayAdded -= onAdded; Screen.DisplayRemoved -= onRemoved; Screen.DisplayMetricsChanged -= onChanged;
    }
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
    // AppKit answers with resolved paths (/private/var for /var); compare the real paths.
    static string Real(string path) => OperatingSystem.IsMacOS() && Posix.RealPath(path) is { } real ? real
        : Path.GetFullPath(new FileInfo(path).Directory!.ResolveLinkTarget(true)?.FullName ?? path);
    static bool SamePath(string a, string b) => a.Length > 0 && b.Length > 0 && string.Equals(
        Path.Combine(Real(Path.GetDirectoryName(a)!), Path.GetFileName(a)), Path.Combine(Real(Path.GetDirectoryName(b)!), Path.GetFileName(b)), StringComparison.Ordinal);
    static bool SameDir(string? a, string b) => a is { Length: > 0 } && Real(a) == Real(b);
    var winNSWindow = OperatingSystem.IsMacOS() ? MacMain.Invoke(() => MacPanel.Obj(win.GetNativeWindowHandle(), "window"), step) : 0;
    MacPanelState? panel = null;

    // Answers the dialog the way a user would: macOS through the panel itself (after reading its setup back from
    // AppKit into `panel`), Linux through X key presses. `paths` are what a macOS user picks in a free-standing panel.
    bool Answer(string title, bool accept, params string[] paths)
    {
        if (OperatingSystem.IsMacOS())
        {
            panel = null;
            // The main thread's run loop, not CEF's task queue: CEF holds its tasks back while a Sync panel runs modally.
            if (!Eventually(() => MacMain.Invoke(() => MacPanel.Find() != 0, step), step)) return false;
            // The panel's UI loads out of process after its window appears; answering earlier can be lost.
            Thread.Sleep(1000);
            panel = MacMain.Invoke(() => MacPanel.State(MacPanel.Find()), step);
            return MacMain.Invoke(() => MacPanel.Answer(accept, paths), step);
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
        return false;
    }

    if (OperatingSystem.IsWindows())
    {
        await WindowsDialogsAsync(win, dir);
        Directory.Delete(dir, recursive: true);
        return;
    }
    if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;

    var saveOptions = new SaveDialogOptions { Title = "Oriel save test", DefaultPath = target, Filters = [new("Text", "txt")] };
    if (OperatingSystem.IsMacOS())
        saveOptions = saveOptions with
        {
            Message = "Save the test file", ButtonLabel = "Keep", NameFieldLabel = "Call it:", ShowsTagField = false, SecurityScopedBookmarks = true,
            Filters = [new("Text", "txt"), new("Archives", "tar.gz", ".zip", "gz"), new("Everything", "*")],
            Properties = SaveDialogProperties.CreateDirectory | SaveDialogProperties.ShowHiddenFiles | SaveDialogProperties.TreatPackageAsDirectory,
        };
    var save = Dialog.ShowSaveDialogAsync(win, saveOptions);
    if (OperatingSystem.IsMacOS())
    {
        Eventually(() => MacMain.Invoke(() => MacPanel.Find() != 0, step), step);
        // Read before picking: the picks end back on the first filter, which would hide whether it applied at first.
        var picked = MacMain.Invoke(() => $"{MacPanel.State(MacPanel.Find()).AllowedTypes} | {MacPanel.PickFormat(1)} | {MacPanel.PickFormat(2)} | {MacPanel.PickFormat(0)}", step);
        Check("macOS: the first filter applies; picking a Format applies its types; '*' allows every type", picked == "txt | gz,zip | (all) | txt", picked);
    }
    Check("save dialog opens and accepts", Answer("Oriel save test", accept: true));
    var saved = await Task.WhenAny(save, Task.Delay(step)) == save ? save.Result : null;
    Check("ShowSaveDialogAsync returns the chosen path", saved is { Canceled: false } && SamePath(saved.FilePath, target), saved);
    if (OperatingSystem.IsMacOS())
    {
        Check("macOS: save panel set up as Electron's (title, message, prompt, name label, tags, properties)",
            panel is { Title: "Oriel save test", Message: "Save the test file", Prompt: "Keep", NameFieldLabel: "Call it:", ShowsTagField: false,
                ShowsHiddenFiles: true, CanCreateDirectories: true, TreatsPackagesAsDirectories: true, CanSelectHiddenExtension: true, ExtensionHidden: false }, panel);
        Check("macOS: save panel starts in defaultPath's directory with its name", panel is { NameField: "saved.txt" } && SameDir(panel.Directory, dir), panel);
        Check("macOS: async dialog with a window is a sheet on it", panel is { IsSheet: true } && panel.SheetParent == winNSWindow && panel.SheetParent != 0, panel);
        Check("macOS: filters: the first one's types, a Format popup of all names", panel is { AllowedTypes: "txt", Formats: "Text,Archives,Everything", AllowsOtherTypes: false }, panel);
        Check("macOS: securityScopedBookmarks returns the save path's bookmark, creating the file",
            saved?.Bookmark is { Length: > 0 } mark && MacMain.Invoke(() => MacPanel.ResolveBookmark(mark), step) is { } marked && SamePath(marked, target) && File.Exists(target), saved?.Bookmark?.Length);
    }

    var openOptions = new OpenDialogOptions { Title = "Oriel open test", DefaultPath = dir };
    if (OperatingSystem.IsMacOS())
        openOptions = openOptions with
        {
            Message = "Pick anything", ButtonLabel = "Choose", Filters = [new("Any", "*")],
            Properties = OpenDialogProperties.OpenFile | OpenDialogProperties.OpenDirectory | OpenDialogProperties.MultiSelections
                | OpenDialogProperties.ShowHiddenFiles | OpenDialogProperties.NoResolveAliases | OpenDialogProperties.CreateDirectory,
        };
    var open = Dialog.ShowOpenDialogAsync(win, openOptions);
    Check("open dialog opens and cancels", Answer("Oriel open test", accept: false));
    var opened = await Task.WhenAny(open, Task.Delay(step)) == open ? open.Result : null;
    Check("a cancelled open dialog reports Canceled and no paths", opened is { Canceled: true, FilePaths.Count: 0 }, opened);
    if (OperatingSystem.IsMacOS())
    {
        Check("macOS: open panel set up as Electron's (files and directories, several, hidden, aliases, create)",
            panel is { Title: "Oriel open test", Message: "Pick anything", Prompt: "Choose", CanChooseFiles: true, CanChooseDirectories: true, MultipleSelection: true,
                ShowsHiddenFiles: true, ResolvesAliases: false, CanCreateDirectories: true, TreatsPackagesAsDirectories: false, ShowsTagField: true } && SameDir(panel.Directory, dir), panel);
        Check("macOS: a lone '*' filter sets no types and no Format popup", panel is { AllowedTypes: "(all)", Formats: "", AllowsOtherTypes: false }, panel);
    }

    var folder = Dialog.ShowOpenDialogAsync(win, new OpenDialogOptions { Title = "Oriel folder test", DefaultPath = dir, Properties = OpenDialogProperties.OpenDirectory });
    Check("folder dialog opens and accepts", Answer("Oriel folder test", accept: true));
    var chosen = await Task.WhenAny(folder, Task.Delay(step)) == folder ? folder.Result : null;
    Check("OpenDirectory returns the directory it started in", chosen is { Canceled: false, FilePaths: [var only] } && SameDir(only, dir),
        chosen is null ? "none" : string.Join(",", chosen.FilePaths));
    if (OperatingSystem.IsMacOS())
        Check("macOS: OpenDirectory alone cannot choose files, nor several; no filters allow any type",
            panel is { CanChooseFiles: false, CanChooseDirectories: true, MultipleSelection: false, AllowedTypes: "(all)", AllowsOtherTypes: true }, panel);

    if (OperatingSystem.IsMacOS())
    {
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        var several = Dialog.ShowOpenDialogAsync(new OpenDialogOptions { Title = "Oriel several", SecurityScopedBookmarks = true, Properties = OpenDialogProperties.OpenFile | OpenDialogProperties.MultiSelections });
        Check("macOS: window-less open dialog opens and accepts two files", Answer("Oriel several", accept: true, a, b));
        var both = await Task.WhenAny(several, Task.Delay(step)) == several ? several.Result : null;
        string? Resolved(IReadOnlyList<string>? marks, int i) => marks is { } m && m.Count > i ? MacMain.Invoke(() => MacPanel.ResolveBookmark(m[i]), step) : null;
        Check("macOS: several chosen files come back in order, each with a bookmark that resolves to it",
            both is { Canceled: false, FilePaths: [var p1, var p2], Bookmarks.Count: 2 } && SamePath(p1, a) && SamePath(p2, b)
                && Resolved(both.Bookmarks, 0) is { } r1 && SamePath(r1, a) && Resolved(both.Bookmarks, 1) is { } r2 && SamePath(r2, b), both is null ? "none" : string.Join(",", both.FilePaths) + $" marks={both.Bookmarks?.Count}");
        Check("macOS: window-less async dialog is a free-standing, non-modal panel", panel is { IsSheet: false, SheetParent: 0, IsModal: false }, panel);
        Check("macOS: an empty defaultPath starts in Downloads, as Electron's GetDefaultPath",
            SameDir(panel?.Directory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")), panel);

        var cancelled = Dialog.ShowOpenDialogAsync(new OpenDialogOptions { Title = "Oriel window-less cancel", SecurityScopedBookmarks = true });
        Check("macOS: window-less open dialog opens and cancels", Answer("Oriel window-less cancel", accept: false));
        var none = await Task.WhenAny(cancelled, Task.Delay(step)) == cancelled ? cancelled.Result : null;
        Check("macOS: a cancelled window-less dialog reports Canceled, no paths and no bookmarks", none is { Canceled: true, FilePaths.Count: 0, Bookmarks.Count: 0 }, none);

        // A window does not close with a sheet attached; Close ends the sheet first, as Electron, so the dialog answers.
        var sheetHost = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel sheet host", Width = 500, Height = 400 });
        Eventually(() => sheetHost.GetNativeWindowHandle() != 0, step);
        var orphan = Dialog.ShowOpenDialogAsync(sheetHost, new OpenDialogOptions { Title = "Oriel orphan sheet" });
        var shown = Eventually(() => MacMain.Invoke(() => MacPanel.Find() != 0, step), step);
        Thread.Sleep(1000);
        using (var hostClosed = new ManualResetEventSlim())
        {
            sheetHost.Closed += (_, _) => hostClosed.Set();
            sheetHost.Close();
            var answered = await Task.WhenAny(orphan, Task.Delay(step)) == orphan;
            Check("macOS: closing a window with a dialog sheet up ends the sheet and closes the window",
                shown && answered && hostClosed.Wait(step), $"shown={shown} answered={answered} result={(answered ? orphan.Result : null)}");
        }

        // AppKit can hand back files from a directory-only panel; Electron drops them (crbug.com/1357523).
        var dirsOnly = Dialog.ShowOpenDialogAsync(new OpenDialogOptions { Title = "Oriel dirs only", Properties = OpenDialogProperties.OpenDirectory });
        Check("macOS: directory-only dialog opens and accepts a file and a directory", Answer("Oriel dirs only", accept: true, a, dir));
        var dirs = await Task.WhenAny(dirsOnly, Task.Delay(step)) == dirsOnly ? dirsOnly.Result : null;
        Check("macOS: a directory-only dialog drops chosen files", dirs is { Canceled: false, FilePaths: [var d], Bookmarks: null } && SameDir(d, dir),
            dirs is null ? "none" : string.Join(",", dirs.FilePaths));

        var syncOpen = Task.Run(() => Oriel.Cef.CefThread.InvokeOnUi(() => Dialog.ShowOpenDialogSync(null, new OpenDialogOptions { Title = "Oriel sync open", DefaultPath = a })));
        Check("macOS: window-less Sync open dialog opens and accepts", Answer("Oriel sync open", accept: true, a));
        var syncOpened = await Task.WhenAny(syncOpen, Task.Delay(step)) == syncOpen ? syncOpen.Result : null;
        Check("macOS: ShowOpenDialogSync on the UI thread returns the chosen file", syncOpened is [var s1] && SamePath(s1, a), syncOpened is null ? "none" : string.Join(",", syncOpened));
        // An open panel reads its name field back as "Untitled" whatever it selects, so only the directory is checked.
        Check("macOS: window-less Sync dialog is app-modal (runModal), starting in defaultPath's directory",
            panel is { IsSheet: false, IsModal: true } && SameDir(panel.Directory, dir), panel);

        var syncSave = Task.Run(() => Oriel.Cef.CefThread.InvokeOnUi(() => Dialog.ShowSaveDialogSync(win, new SaveDialogOptions { Title = "Oriel sync save", DefaultPath = Path.Combine(dir, "sync.txt") })));
        Check("macOS: Sync save dialog with a window opens and accepts", Answer("Oriel sync save", accept: true));
        var syncSaved = await Task.WhenAny(syncSave, Task.Delay(step)) == syncSave ? syncSave.Result : null;
        Check("macOS: ShowSaveDialogSync returns the chosen path", syncSaved is not null && SamePath(syncSaved, Path.Combine(dir, "sync.txt")), syncSaved);
        Check("macOS: Sync dialog with a window is a sheet with the window run modally; no hidden-extension choice",
            panel is { IsSheet: true, CanSelectHiddenExtension: false, NameField: "sync.txt" } && panel.SheetParent == winNSWindow && panel.ModalWindow == winNSWindow, panel);

        var syncCancel = Task.Run(() => Oriel.Cef.CefThread.InvokeOnUi(() => Dialog.ShowOpenDialogSync(win, new OpenDialogOptions { Title = "Oriel sync cancel", DefaultPath = dir })));
        Check("macOS: Sync open dialog with a window opens and cancels", Answer("Oriel sync cancel", accept: false));
        var syncCancelled = await Task.WhenAny(syncCancel, Task.Delay(step)) == syncCancel ? "done" : "pending";
        Check("macOS: a cancelled ShowOpenDialogSync returns null", syncCancelled == "done" && syncCancel.Result is null, syncCancelled);
    }

    // A download with no save path asks where to save it, with the item's dialog options, as Electron.
    var source = Path.Combine(dir, "source.txt");
    File.WriteAllText(source, "download me");
    async Task<(DownloadState? State, string SavePath)> DownloadThroughDialog(string title, string defaultPath, bool accept)
    {
        var done = new TaskCompletionSource<DownloadState>(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadItem? item = null;
        void OnWillDownload(object? _, WillDownloadEventArgs e)
        {
            item = e.Item;
            e.Item.SetSaveDialogOptions(new SaveDialogOptions { Title = title, DefaultPath = defaultPath });
            e.Item.Done += (_, d) => done.TrySetResult(d.State);
        }
        win.WebContents.Session.WillDownload += OnWillDownload;
        win.WebContents.DownloadUrl(new Uri(source).AbsoluteUri);
        var answered = Answer(title, accept);
        var state = answered && await Task.WhenAny(done.Task, Task.Delay(step)) == done.Task ? done.Task.Result : (DownloadState?)null;
        win.WebContents.Session.WillDownload -= OnWillDownload;
        return (state, item?.GetSavePath() ?? "");
    }
    var downloadTarget = Path.Combine(dir, "downloaded.txt");
    var accepted = await DownloadThroughDialog("Oriel download test", downloadTarget, accept: true);
    Check("a download with no save path shows the save dialog and saves where it was accepted",
        accepted.State == DownloadState.Completed && SamePath(accepted.SavePath, downloadTarget) && File.Exists(downloadTarget) && File.ReadAllText(downloadTarget) == "download me",
        $"{accepted.State} {accepted.SavePath}");
    var refused = await DownloadThroughDialog("Oriel download cancel test", Path.Combine(dir, "refused.txt"), accept: false);
    Check("cancelling the download's save dialog cancels the download", refused.State == DownloadState.Cancelled && !File.Exists(Path.Combine(dir, "refused.txt")), refused.State);

    if (!OperatingSystem.IsMacOS())
        Check("Sync dialogs refuse the UI thread",
            Oriel.Cef.CefThread.InvokeOnUi(() => Throws<InvalidOperationException>(() => Dialog.ShowSaveDialogSync(win, new SaveDialogOptions()))));
    Directory.Delete(dir, recursive: true);
}

async Task PageEventsAsync()
{
    var dir = Directory.CreateTempSubdirectory("oriel-page-").FullName;
    var page = Path.Combine(dir, "find.html");
    File.WriteAllText(page, "<!doctype html><p id=p>apple banana apple Apple</p><iframe srcdoc='<p>inner</p>'></iframe><div id=full style='background:#0a0'>full</div>");
    var w = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: page events", Width = 500, Height = 300 });
    var frameLoads = new System.Collections.Concurrent.ConcurrentQueue<bool>();
    w.WebContents.DidFrameFinishLoad += (_, isMain) => frameLoads.Enqueue(isMain);
    Fires(h => w.WebContents.DidFinishLoad += h, h => w.WebContents.DidFinishLoad -= h, () => w.WebContents.LoadFile(page), step);
    Check("DidFrameFinishLoad is raised for the iframe and for the main frame, the main frame last",
        Eventually(() => frameLoads.Count >= 2, step) && frameLoads.Contains(false) && frameLoads.Last(), string.Join(",", frameLoads));

    // findInPage: a new search, follow-ups both ways, case, and stopping.
    FoundInPageEventArgs? found = null;
    w.WebContents.FoundInPage += (_, e) => { if (e.FinalUpdate) found = e; };
    var lastId = 0;
    async Task<FoundInPageEventArgs?> Find(string text, FindInPageOptions options)
    {
        found = null;
        lastId = w.WebContents.FindInPage(text, options);
        await Task.Yield();
        return Eventually(() => found is not null, step) ? found : null;
    }
    w.Focus();
    var first = await Find("apple", new FindInPageOptions { FindNext = true });
    Check("FindInPage starts a search: FoundInPage has its request id, the matches and the first match", first is { Matches: 3, ActiveMatchOrdinal: 1 } && first.RequestId == lastId
        && first.SelectionArea.Width > 0, first);
    var next = await Find("apple", new FindInPageOptions());
    Check("FindInPage with findNext false moves to the next match", next is { Matches: 3, ActiveMatchOrdinal: 2 }, next);
    var back = await Find("apple", new FindInPageOptions { Forward = false });
    Check("Forward false moves back", back is { ActiveMatchOrdinal: 1 }, back);
    var cased = await Find("Apple", new FindInPageOptions { FindNext = true, MatchCase = true });
    Check("MatchCase finds only the matching case", cased is { Matches: 1 }, cased);
    // Stop with the case-sensitive search's match active: clear, then a fresh search kept. The DOM selection only
    // changes when the find stops, so each is read after a pause.
    string Selection() => (string?)Js(w, "getSelection().toString()").Result ?? "";
    w.WebContents.StopFindInPage(StopFindAction.ClearSelection);
    await Task.Delay(500);
    var cleared = Selection();
    await w.WebContents.ExecuteJavaScriptAsync("getSelection().removeAllRanges(); 1");
    await Find("banana", new FindInPageOptions { FindNext = true });
    w.WebContents.StopFindInPage(StopFindAction.KeepSelection);
    await Task.Delay(500);
    var kept = Selection();
    Check("StopFindInPage clears the active match's selection, or keeps it", cleared == "\"\"" && kept == "\"banana\"", $"{cleared} / {kept}");
    Check("FindInPage refuses empty text, as Electron's", Throws<ArgumentException>(() => w.WebContents.FindInPage("")));

    // HTML full screen: the page asks; both the page and the window hear it.
    var fullEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
    w.WebContents.EnterHtmlFullScreen += (_, _) => fullEvents.Enqueue("page-enter");
    w.WebContents.LeaveHtmlFullScreen += (_, _) => fullEvents.Enqueue("page-leave");
    w.EnterHtmlFullScreen += (_, _) => fullEvents.Enqueue("window-enter");
    w.LeaveHtmlFullScreen += (_, _) => fullEvents.Enqueue("window-leave");
    await w.WebContents.ExecuteJavaScriptAsync("full.requestFullscreen().then(() => 1, () => 0)", userGesture: true);
    var entered = Eventually(() => fullEvents.Contains("page-enter") && fullEvents.Contains("window-enter"), step);
    await w.WebContents.ExecuteJavaScriptAsync("document.exitFullscreen().then(() => 1, () => 0)", userGesture: true);
    var left = Eventually(() => fullEvents.Contains("page-leave") && fullEvents.Contains("window-leave"), step);
    Check("requestFullscreen raises EnterHtmlFullScreen and exitFullscreen LeaveHtmlFullScreen, on the page and the window", entered && left, string.Join(",", fullEvents));

    // did-fail-provisional-load for every failure; did-fail-load leaves out a cancelled one.
    var provisional = new System.Collections.Concurrent.ConcurrentQueue<int>();
    var failed = new System.Collections.Concurrent.ConcurrentQueue<int>();
    w.WebContents.DidFailProvisionalLoad += (_, e) => provisional.Enqueue(e.ErrorCode);
    w.WebContents.DidFailLoad += (_, e) => failed.Enqueue(e.ErrorCode);
    _ = w.LoadUrl("http://oriel-nowhere.invalid/").ContinueWith(_ => { });
    var unreachable = Eventually(() => !failed.IsEmpty, step);
    Check("a failed load raises DidFailProvisionalLoad and DidFailLoad", unreachable && provisional.SequenceEqual(failed) && failed.All(c => c < 0),
        $"{string.Join(",", provisional)} / {string.Join(",", failed)}");
    provisional.Clear();
    failed.Clear();
    _ = w.LoadUrl("http://10.255.255.1/").ContinueWith(_ => { });
    await Task.Delay(300);
    Fires(h => w.WebContents.DidFinishLoad += h, h => w.WebContents.DidFinishLoad -= h, () => w.WebContents.LoadFile(page), step);
    var aborted = Eventually(() => provisional.Contains(-3), step);
    Check("a cancelled load raises DidFailProvisionalLoad (ERR_ABORTED) but not DidFailLoad, as Electron's", aborted && !failed.Contains(-3),
        $"{string.Join(",", provisional)} / {string.Join(",", failed)}");
    w.Destroy();
    Directory.Delete(dir, recursive: true);
}

async Task MessagePortsAsync()
{
    var dir = Directory.CreateTempSubdirectory("oriel-ports-").FullName;
    var preload = Path.Combine(dir, "preload.js");
    File.WriteAllText(preload, string.Join("\n",
        "const { ipcRenderer } = oriel;",
        // A port from the main process echoes what it hears, and answers on any port sent through it.
        "ipcRenderer.on('port', (event, msg) => {",
        "  const [port] = event.ports;",
        "  port.onmessage = (e) => {",
        "    if (e.ports.length) { e.ports[0].postMessage('via-second:' + e.data); return; }",
        "    port.postMessage({ echo: e.data, msg, top: window === window.top });",
        "  };",
        "});",
        // A port the page makes goes to the main process, which talks to this end.
        "ipcRenderer.on('make', () => {",
        "  const { port1, port2 } = new MessageChannel();",
        "  port1.onmessage = (e) => port1.postMessage('pong:' + e.data);",
        "  ipcRenderer.postMessage('given', 'from-page', [port2]);",
        "});",
        ""));
    var page = Path.Combine(dir, "ports.html");
    File.WriteAllText(page, "<!doctype html><title>ports</title><iframe srcdoc='<p>frame'></iframe>");
    var w = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Preload = preload } });
    Fires(h => w.WebContents.DidFinishLoad += h, h => w.WebContents.DidFinishLoad -= h, () => w.WebContents.LoadFile(page), step);

    static async Task<MessagePortEvent?> Next(MessagePortMain port, Action send)
    {
        var got = new TaskCompletionSource<MessagePortEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<MessagePortEvent> on = (_, e) => got.TrySetResult(e);
        port.Message += on;
        send();
        var result = await got.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
        port.Message -= on;
        return result;
    }

    // Main to page: one end moves into the page; the page answers on it.
    var channel = new MessageChannelMain();
    channel.Port1.Start();
    w.WebContents.PostMessage("port", new System.Text.Json.Nodes.JsonObject { ["hello"] = "page" }, [channel.Port2]);
    var echo = await Next(channel.Port1, () => channel.Port1.PostMessage("ping"));
    Check("WebContents.PostMessage moves a port into the page; ipcRenderer listeners get it in event.ports",
        echo?.Data?["echo"]?.GetValue<string>() == "ping" && echo.Data["msg"]?["hello"]?.GetValue<string>() == "page", echo?.Data?.ToJsonString());
    Check("a port that moved can't move again, and a port can't send itself",
        Throws<InvalidOperationException>(() => w.WebContents.PostMessage("port", null, [channel.Port2]))
        && Throws<InvalidOperationException>(() => channel.Port1.PostMessage("x", [channel.Port1])));

    // A port sent through a port arrives with the message.
    var second = new MessageChannelMain();
    second.Port1.Start();
    var viaSecond = await Next(second.Port1, () => channel.Port1.PostMessage("hi", [second.Port2]));
    Check("a port sent through a port arrives in the page's event.ports", viaSecond?.Data?.GetValue<string>() == "via-second:hi", viaSecond?.Data?.ToJsonString());

    // Page to main: ipcRenderer.postMessage's ports arrive in ipcMain's event.ports.
    MessagePortMain? given = null;
    string? givenArg = null;
    using var gave = new ManualResetEventSlim();
    Action<IpcMainEvent> onGiven = e => { given = e.Ports.FirstOrDefault(); givenArg = e.Args.FirstOrDefault().GetString(); gave.Set(); };
    IpcMain.On("given", onGiven);
    var postedAsIpcMessage = 0;
    EventHandler<IpcMessageEventArgs> onPosted = (_, _) => Interlocked.Increment(ref postedAsIpcMessage);
    w.WebContents.IpcMessage += onPosted;
    w.WebContents.Send("make");
    var arrived = await Task.Run(() => gave.Wait(step));
    IpcMain.RemoveListener("given", onGiven);
    w.WebContents.IpcMessage -= onPosted;
    Check("ipcRenderer.postMessage's ports reach IpcMainEvent.Ports, with the message, and raise no ipc-message, as Electron's", arrived && given is not null && givenArg == "from-page" && postedAsIpcMessage == 0, $"{givenArg} ipc-message={postedAsIpcMessage}");
    // Messages wait until Start.
    var queued = new List<string?>();
    given!.Message += (_, e) => queued.Add(e.Data?.GetValue<string>());
    given.PostMessage("hi");
    await Task.Delay(500);
    var beforeStart = queued.Count;
    given.Start();
    Check("a page's port answers, and its messages wait until Start", beforeStart == 0 && Eventually(() => queued.Contains("pong:hi"), step), $"{beforeStart} {string.Join(",", queued)}");

    // In the main process: a channel's two ends, and closing one.
    var local = new MessageChannelMain();
    local.Port2.Start();
    var localGot = await Next(local.Port2, () => local.Port1.PostMessage(new System.Text.Json.Nodes.JsonObject { ["n"] = 1 }));
    var inner = new MessageChannelMain();
    var carried = await Next(local.Port2, () => local.Port1.PostMessage("carry", [inner.Port2]));
    var through = carried?.Ports.FirstOrDefault();
    inner.Port1.Start();
    var innerGot = through is null ? null : await Next(inner.Port1, () => through.PostMessage("back"));
    Check("a port sent through a channel here arrives as a new port that still reaches its partner",
        carried?.Ports.Count == 1 && !ReferenceEquals(through, inner.Port2) && innerGot?.Data?.GetValue<string>() == "back", innerGot?.Data?.ToJsonString());
    var closed = false;
    local.Port2.Closed += (_, _) => closed = true;
    local.Port1.Close();
    Check("a channel in the main process delivers, and Close raises Closed on the other end",
        localGot?.Data?["n"]?.GetValue<int>() == 1 && Eventually(() => closed, step), localGot?.Data?.ToJsonString());

    // A frame's PostMessage reaches that frame's preload.
    var frame = Eventually(() => w.WebContents.MainFrame?.Frames.Count == 1, step) ? w.WebContents.MainFrame!.Frames[0] : null;
    var frameChannel = new MessageChannelMain();
    frameChannel.Port1.Start();
    var frameEcho = frame is null ? null : await Next(frameChannel.Port1, () =>
    {
        frame.PostMessage("port", "frame", [frameChannel.Port2]);
        frameChannel.Port1.PostMessage("to-frame");
    });
    Check("WebFrameMain.PostMessage moves a port into that frame", frameEcho?.Data?["msg"]?.GetValue<string>() == "frame" && frameEcho.Data["top"]?.GetValue<bool>() == false, frameEcho?.Data?.ToJsonString());
    var frameClosed = false;
    frameChannel.Port1.Closed += (_, _) => frameClosed = true;
    await w.WebContents.ExecuteJavaScriptAsync("document.querySelector('iframe').srcdoc = '<p>next'; 1");
    Check("an iframe that loads a new document closes the ports it held", Eventually(() => frameClosed, step));

    // Navigating away closes the ports the page held (Chromium 154 reports no close from the page itself: drift I7).
    var navigated = false;
    channel.Port1.Closed += (_, _) => navigated = true;
    Fires(h => w.WebContents.DidFinishLoad += h, h => w.WebContents.DidFinishLoad -= h, () => w.WebContents.LoadFile(page), step);
    Check("a page that navigates away closes the ports it held", Eventually(() => navigated, step));
    w.Destroy();
    Directory.Delete(dir, recursive: true);
}

async Task MacWindowApisAsync()
{
    var w = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: mac apis", Width = 300, Height = 200, HiddenInMissionControl = true });
    if (!OperatingSystem.IsMacOS())
    {
        // Elsewhere these do nothing, as in Electron, and report the defaults.
        w.SetRepresentedFilename("/tmp/x");
        w.SetDocumentEdited(true);
        Check("represented filename and document edited are macOS only", w.GetRepresentedFilename() == "" && !w.IsDocumentEdited());
        if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/xdotool"))
        {
            // The window manager's own view of the window's desktop, read with xdotool.
            string Desktop()
            {
                var xdotool = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/xdotool", $"get_desktop_for_window {w.GetNativeWindowHandle()}") { RedirectStandardOutput = true })!;
                var output = xdotool.StandardOutput.ReadToEnd().Trim();
                xdotool.WaitForExit();
                return output;
            }
            var before = Desktop();
            w.SetVisibleOnAllWorkspaces(true);
            var all = Eventually(() => Desktop() is "-1" or "4294967295", step) && w.IsVisibleOnAllWorkspaces();
            var allDesktop = Desktop();
            w.SetVisibleOnAllWorkspaces(false);
            Check("SetVisibleOnAllWorkspaces puts the window on every desktop and back on the current one (Linux)",
                all && Eventually(() => Desktop() == before, step) && !w.IsVisibleOnAllWorkspaces(), $"{before} -> {allDesktop} -> {Desktop()}");
        }
        w.Destroy();
        return;
    }
    // Read back from AppKit directly, not through Oriel's helpers.
    nint NSWindow() => MacInterop.Send(w.GetNativeWindowHandle(), MacInterop.sel_registerName("window"));
    string Represented() => System.Runtime.InteropServices.Marshal.PtrToStringUTF8(MacInterop.Send(MacInterop.Send(NSWindow(), MacInterop.sel_registerName("representedFilename")), MacInterop.sel_registerName("UTF8String"))) ?? "";
    bool Edited() => MacInterop.SendBool(NSWindow(), MacInterop.sel_registerName("isDocumentEdited")) != 0;
    nuint Behavior() => (nuint)MacInterop.Send(NSWindow(), MacInterop.sel_registerName("collectionBehavior"));
    bool ButtonHidden(nuint kind) => MacInterop.SendBool(MacInterop.SendNUInt(NSWindow(), MacInterop.sel_registerName("standardWindowButton:"), kind), MacInterop.sel_registerName("isHidden")) != 0;

    var file = Path.Combine(Path.GetTempPath(), "oriel-represented.txt");
    File.WriteAllText(file, "x");
    w.SetRepresentedFilename(file);
    Check("SetRepresentedFilename sets the NSWindow's, and GetRepresentedFilename reads it", Eventually(() => Represented() == file, step) && w.GetRepresentedFilename() == file, Represented());
    w.SetDocumentEdited(true);
    var edited = Eventually(() => Edited(), step) && w.IsDocumentEdited();
    w.SetDocumentEdited(false);
    Check("SetDocumentEdited sets the NSWindow's edited state, both ways", edited && Eventually(() => !Edited(), step) && !w.IsDocumentEdited());
    w.SetWindowButtonVisibility(false);
    var hidden = Eventually(() => ButtonHidden(0) && ButtonHidden(1) && ButtonHidden(2), step);
    w.SetWindowButtonVisibility(true);
    Check("SetWindowButtonVisibility hides and shows the close, minimise and zoom buttons", hidden && Eventually(() => !ButtonHidden(0) && !ButtonHidden(1) && !ButtonHidden(2), step));
    var transientAtStart = (Behavior() & 8) != 0 && w.IsHiddenInMissionControl();
    w.SetHiddenInMissionControl(false);
    Check("HiddenInMissionControl sets the Transient behaviour; SetHiddenInMissionControl(false) clears it",
        transientAtStart && Eventually(() => (Behavior() & 8) == 0, step) && !w.IsHiddenInMissionControl(), Behavior());
    w.SetVisibleOnAllWorkspaces(true, new VisibleOnAllWorkspacesOptions { SkipTransformProcessType = true });
    var allSpaces = Eventually(() => (Behavior() & 1) != 0 && (Behavior() & 256) == 0, step) && w.IsVisibleOnAllWorkspaces();
    w.SetVisibleOnAllWorkspaces(true, new VisibleOnAllWorkspacesOptions { VisibleOnFullScreen = true, SkipTransformProcessType = true });
    var overFullScreen = Eventually(() => (Behavior() & 257) == 257, step);
    w.SetVisibleOnAllWorkspaces(false, new VisibleOnAllWorkspacesOptions { SkipTransformProcessType = true });
    Check("SetVisibleOnAllWorkspaces joins all Spaces, over full screen apps when asked, and leaves them",
        allSpaces && overFullScreen && Eventually(() => (Behavior() & 257) == 0, step) && !w.IsVisibleOnAllWorkspaces(), Behavior());
    // Without SkipTransformProcessType the Dock icon goes for full-screen visibility and comes back otherwise.
    nint Policy() => MacInterop.Send(MacInterop.Send(MacInterop.objc_getClass("NSApplication"), MacInterop.sel_registerName("sharedApplication")), MacInterop.sel_registerName("activationPolicy"));
    w.SetVisibleOnAllWorkspaces(true, new VisibleOnAllWorkspacesOptions { VisibleOnFullScreen = true });
    var accessory = Eventually(() => Policy() == 1, step);
    w.SetVisibleOnAllWorkspaces(false);
    Check("SetVisibleOnAllWorkspaces hides the Dock icon for full-screen visibility and shows it again, as Electron", accessory && Eventually(() => Policy() == 0, step), Policy());
    w.Destroy();
    File.Delete(file);
    await Task.CompletedTask;
}

async Task WebContentsMethodsAsync()
{
    // A local server whose responses wait on named gates: a navigation can be held before or after its response.
    var gates = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource>();
    TaskCompletionSource Gate(string name) => gates.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var origin = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}";
    _ = Task.Run(async () =>
    {
        while (true)
        {
            System.Net.Sockets.TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                using var _ = client;
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream);
                    var path = (await reader.ReadLineAsync() ?? "").Split(' ').ElementAtOrDefault(1) ?? "/";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    async Task Write(string text) { await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text)); await stream.FlushAsync(); }
                    const string head = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                    if (path.StartsWith("/slow", StringComparison.Ordinal)) { await Gate(path).Task; await Write(head + "<!doctype html><title>slow</title><p>slow"); }
                    else if (path.StartsWith("/redirect", StringComparison.Ordinal)) await Write("HTTP/1.1 302 Found\r\nLocation: /slow?redirected\r\nConnection: close\r\n\r\n");
                    else if (path.StartsWith("/nocontent", StringComparison.Ordinal)) await Write("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
                    else if (path.StartsWith("/body", StringComparison.Ordinal)) { await Write(head + "<!doctype html><title>body</title><p>start" + new string(' ', 4096)); await Gate(path).Task; await Write("<p>end"); }
                    else await Write(head + "<!doctype html><title>root</title><p>root");
                }
                catch (Exception) { }
            });
        }
    });

    var w = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: webContents methods", Width = 500, Height = 400 });
    var wc = w.WebContents;
    await wc.LoadUrl(origin + "/");
    (bool Loading, bool Main, bool Waiting) State() => (wc.IsLoading(), wc.IsLoadingMainFrame(), wc.IsWaitingForResponse());
    // did-finish-load, which LoadUrl waits for, comes before did-stop-loading, as in Chromium.
    Check("a loaded page is not loading, nor waiting", Eventually(() => State() == (false, false, false), step), State());

    // isLoading / isLoadingMainFrame / isWaitingForResponse through a navigation's phases, and stop().
    _ = wc.LoadUrl(origin + "/slow?stop").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    var atOnce = State();
    Check("while the main frame waits for its response: loading, loading the main frame, waiting, as soon as LoadUrl returns",
        atOnce == (true, true, true) && Eventually(() => State() == (true, true, true), step), $"{atOnce} {State()}");
    wc.Stop();
    Check("Stop ends the navigation: nothing loads or waits, and the page stays", Eventually(() => State() == (false, false, false), step)
        && wc.GetUrl() == origin + "/", $"{State()} {wc.GetUrl()}");
    Gate("/slow?stop").TrySetResult();
    _ = wc.LoadUrl(origin + "/redirect").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    Eventually(() => wc.IsWaitingForResponse(), step);
    await Task.Delay(500);
    wc.Stop();
    Check("Stop after a redirect ends the navigation too", Eventually(() => State() == (false, false, false), step), State());
    Gate("/slow?redirected").TrySetResult();

    _ = wc.ExecuteJavaScriptAsync($"location.href = '{origin}/slow?page'; 1");
    Check("a navigation the page starts also waits for its response", Eventually(() => State() == (true, true, true), step), State());
    Gate("/slow?page").TrySetResult();
    Eventually(() => State() == (false, false, false) && wc.GetUrl().EndsWith("?page", StringComparison.Ordinal), step);

    _ = wc.LoadUrl(origin + "/body?1").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    Check("once the response is in and the document loads: loading the main frame, no longer waiting",
        Eventually(() => State() == (true, true, false), step), State());
    Gate("/body?1").TrySetResult();
    Check("the loaded document ends all three", Eventually(() => State() == (false, false, false), step), State());

    await wc.LoadUrl(wc.GetUrl() + "#in-page");
    await wc.ExecuteJavaScriptAsync($"const f = document.createElement('iframe'); f.src = '{origin}/slow?frame'; document.body.append(f); 1");
    Check("after an in-page navigation, an iframe loading alone: loading, but not the main frame and not waiting", Eventually(() => State() == (true, false, false), step), State());
    _ = wc.LoadUrl(origin + "/nocontent").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    await Task.Delay(1000);
    Check("a main-frame navigation that ends without a document (204) no longer waits, while the iframe still loads", State() == (true, false, false), State());
    Gate("/slow?frame").TrySetResult();
    Eventually(() => !wc.IsLoading(), step);

    // executeJavaScriptInIsolatedWorld waits for the main frame's document, as Electron's.
    _ = wc.LoadUrl(origin + "/slow?js").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    Eventually(() => wc.IsWaitingForResponse(), step);
    var waited = wc.ExecuteJavaScriptInIsolatedWorldAsync(0, [new WebSource("location.search")]);
    await Task.Delay(500);
    Gate("/slow?js").TrySetResult();
    Check("ExecuteJavaScriptInIsolatedWorld waits for the loading main frame and runs in the new document",
        await Task.WhenAny(waited, Task.Delay(step)) == waited && waited.IsCompletedSuccessfully && waited.Result?.ToString() == "?js", waited.IsCompletedSuccessfully ? waited.Result : waited.Exception?.GetBaseException().Message);

    var fresh = new BrowserWindow(new BrowserWindowOptions { Show = false });
    var early = fresh.WebContents.ExecuteJavaScriptInIsolatedWorldAsync(0, [new WebSource("location.href")]);
    await Task.Delay(500);
    _ = fresh.WebContents.LoadUrl(origin + "/");
    Check("ExecuteJavaScriptInIsolatedWorld before the first page waits for it",
        await Task.WhenAny(early, Task.Delay(step)) == early && early.IsCompletedSuccessfully && early.Result?.ToString() == origin + "/",
        early.IsCompletedSuccessfully ? early.Result : early.Exception?.GetBaseException().Message);
    fresh.Destroy();

    // Worlds: 0 is the page's, 999 the preload's, any other id a world of its own that keeps its globals.
    await wc.ExecuteJavaScriptAsync("window.pageVar = 42; 1");
    async Task<string?> World(int id, params string[] code)
    {
        try { return (await wc.ExecuteJavaScriptInIsolatedWorldAsync(id, [.. code.Select(c => new WebSource(c))]))?.ToJsonString(); }
        catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
    }
    Check("world 0 is the page's main world", await World(0, "pageVar") == "42", await World(0, "pageVar"));
    Check("world 999 is the preload's world", await World(999, "typeof __orielDeliver + ' ' + typeof pageVar") == "\"function undefined\"",
        await World(999, "typeof __orielDeliver + ' ' + typeof pageVar"));
    Check("another world sees neither the page's globals nor the preload's", await World(5, "typeof pageVar + ' ' + typeof __orielDeliver") == "\"undefined undefined\"",
        await World(5, "typeof pageVar + ' ' + typeof __orielDeliver"));
    await World(5, "var kept = 'five'");
    Check("a world keeps its globals between calls, apart from other worlds", await World(5, "kept") == "\"five\"" && await World(6, "typeof kept") == "\"undefined\"",
        $"{await World(5, "kept")} {await World(6, "typeof kept")}");
    Check("several scripts run in order and the first one's value is returned", await World(7, "var b = 1; 'first'", "b = 2") == "\"first\"" && await World(7, "b") == "2",
        $"{await World(7, "var b = 1; 'first'", "b = 2")} {await World(7, "b")}");
    Check("a promise is awaited, an object returned by value", await World(5, "Promise.resolve(9)") == "9" && await World(5, "({ x: [1] })") == "{\"x\":[1]}",
        $"{await World(5, "Promise.resolve(9)")} {await World(5, "({ x: [1] })")}");
    Check("a throwing script fails with Electron's message", await World(5, "throw new Error('boom')") == "JavaScriptException: Script failed to execute, this normally means an error was thrown. Check the renderer console for the error.",
        await World(5, "throw new Error('boom')"));
    Check("a rejected promise fails with its reason", (await World(5, "Promise.reject(new Error('nope'))"))?.StartsWith("JavaScriptException: Error: nope", StringComparison.Ordinal) == true,
        await World(5, "Promise.reject(new Error('nope'))"));
    var traced = (await wc.ExecuteJavaScriptInIsolatedWorldAsync(5, [new WebSource("new Error().stack", "oriel://named-script.js")]))?.ToString();
    Check("a script's url names it in stack traces", traced?.Contains("oriel://named-script.js", StringComparison.Ordinal) == true, traced);
    var noGesture = await wc.ExecuteJavaScriptInIsolatedWorldAsync(9, [new WebSource("navigator.userActivation.isActive")]);
    var gesture = await wc.ExecuteJavaScriptInIsolatedWorldAsync(8, [new WebSource("navigator.userActivation.isActive")], userGesture: true);
    Check("userGesture activates the page", gesture?.GetValue<bool>() == true && noGesture?.GetValue<bool>() == false, $"{gesture} {noGesture}");
    Check("no scripts fail; a negative world id throws", await World(5) == "InvalidOperationException: WebFrame was removed before script could run. This normally means the underlying frame was destroyed"
        && Throws<ArgumentOutOfRangeException>(() => _ = wc.ExecuteJavaScriptInIsolatedWorldAsync(-1, [new WebSource("1")])), await World(5));

    // Editing commands, read back from the DOM.
    var dir = Directory.CreateTempSubdirectory("oriel-methods-").FullName;
    var page = Path.Combine(dir, "edit.html");
    File.WriteAllText(page, "<!doctype html><style>body{margin:0}</style><input id=i value='hello world'><textarea id=t>one two three</textarea>" +
        "<p id=p>alpha beta gamma</p><div id=box style='height:100px;overflow:auto'><div style='height:1000px'><button id=inner>in</button></div></div>" +
        "<div style='height:5000px'></div><p id=far>far away text</p><div style='height:2000px'></div><script>window.inputs = []; i.addEventListener('input', e => inputs.push(e.inputType));</script>");
    await wc.LoadFile(page);
    w.Focus();
    await wc.ExecuteJavaScriptAsync("i.value = ''; i.focus(); 1");
    await wc.InsertTextAsync("héllo");
    Check("InsertText types into the focused field, as an input method", await Js(w, "i.value === 'h\\u00e9llo' && inputs.join()") is "\"insertText\"", await Js(w, "[i.value, inputs.join()]"));

    await wc.ExecuteJavaScriptAsync("t.focus(); t.setSelectionRange(5, 5); 1");
    wc.Replace("2");
    Check("Replace with a caret replaces the word around it", Eventually(() => Js(w, "t.value").Result is "\"one 2 three\"", step), Js(w, "t.value").Result);
    await wc.ExecuteJavaScriptAsync("t.setSelectionRange(6, 11); 1");
    wc.Replace("3");
    Check("Replace replaces the selection", Eventually(() => Js(w, "t.value").Result is "\"one 2 3\"", step), Js(w, "t.value").Result);

    await wc.ExecuteJavaScriptAsync("i.value = 'hello world'; i.focus(); i.setSelectionRange(1, 4); 1");
    wc.Unselect();
    Check("Unselect collapses a field's selection to its end", Eventually(() => Js(w, "[i.selectionStart, i.selectionEnd]").Result is "[4,4]", step), Js(w, "[i.selectionStart, i.selectionEnd]").Result);
    await wc.ExecuteJavaScriptAsync("i.blur(); getSelection().setBaseAndExtent(p.firstChild, 6, p.firstChild, 10); 1");
    wc.Unselect();
    Check("Unselect collapses the document's selection to its end", Eventually(() => Js(w, "[getSelection().isCollapsed, getSelection().focusOffset]").Result is "[true,10]", step),
        Js(w, "[getSelection().isCollapsed, getSelection().focusOffset]").Result);

    await wc.ExecuteJavaScriptAsync("i.focus(); i.setSelectionRange(6, 11); 1");
    wc.AdjustSelection(new AdjustSelectionOptions { Start = -6, End = -6 });
    Check("AdjustSelection moves both ends of a field's selection", Eventually(() => Js(w, "[i.selectionStart, i.selectionEnd]").Result is "[0,5]", step), Js(w, "[i.selectionStart, i.selectionEnd]").Result);
    wc.AdjustSelection(new AdjustSelectionOptions { Start = 7 });
    wc.AdjustSelection(new AdjustSelectionOptions { Start = -1 });
    Check("AdjustSelection refuses an empty or negative result", Eventually(() => Js(w, "[i.selectionStart, i.selectionEnd]").Result is "[0,5]", step), Js(w, "[i.selectionStart, i.selectionEnd]").Result);
    await wc.ExecuteJavaScriptAsync("i.blur(); getSelection().setBaseAndExtent(p.firstChild, 6, p.firstChild, 10); 1");
    wc.AdjustSelection(new AdjustSelectionOptions { Start = -1, End = 1 });
    Check("AdjustSelection moves the document's selection by characters", Eventually(() => Js(w, "getSelection().toString()").Result is "\" beta \"", step), Js(w, "getSelection().toString()").Result);

    var bottom = (await wc.ExecuteJavaScriptAsync("document.scrollingElement.scrollHeight - innerHeight"))!.GetValue<int>();
    // On a 2x display the last scroll position can be half a pixel past the rounded scrollHeight.
    const string atBottom = "Math.abs(scrollY - (document.scrollingElement.scrollHeight - innerHeight)) < 1";
    await wc.ExecuteJavaScriptAsync("document.activeElement.blur(); 1");
    wc.ScrollToBottom();
    Check("ScrollToBottom scrolls the document to its end", Eventually(() => Js(w, atBottom).Result is "true", step), $"{Js(w, "scrollY").Result} of {bottom}");
    wc.ScrollToTop();
    Check("ScrollToTop scrolls the document back", Eventually(() => Js(w, "scrollY").Result is "0", step), Js(w, "scrollY").Result);
    await wc.ExecuteJavaScriptAsync("inner.focus(); 1");
    wc.ScrollToBottom();
    Check("ScrollToBottom scrolls the focused element's scroller, not the document", Eventually(() => Js(w, "[box.scrollTop, scrollY]").Result is "[900,0]", step), Js(w, "[box.scrollTop, scrollY]").Result);
    wc.ScrollToTop();
    Check("ScrollToTop scrolls the focused element's scroller back", Eventually(() => Js(w, "box.scrollTop").Result is "0", step), Js(w, "box.scrollTop").Result);

    await wc.ExecuteJavaScriptAsync("inner.blur(); getSelection().selectAllChildren(far); 1");
    wc.CenterSelection();
    await Task.Delay(500);
    var centered = (await wc.ExecuteJavaScriptAsync("const r = getSelection().getRangeAt(0).getBoundingClientRect(); [scrollY, Math.abs(r.top + r.height / 2 - innerHeight / 2) < 30]"))?.ToJsonString();
    Check(OperatingSystem.IsMacOS() ? "CenterSelection puts the selection in the middle of the view" : "CenterSelection does nothing off macOS, as Electron's",
        OperatingSystem.IsMacOS() ? centered?.EndsWith("true]", StringComparison.Ordinal) == true : centered == "[0,false]", centered);

    // insertCSS: applied, invisible to the page's sheet list, removable, and gone with the document.
    File.WriteAllText(page, "<!doctype html><p id=t>x</p>");
    await wc.LoadFile(page);
    var key = await wc.InsertCssAsync("#t { color: rgb(1, 2, 3) }");
    const string cssState = "[getComputedStyle(t).color, document.styleSheets.length, document.querySelectorAll('style, link').length]";
    Check("InsertCss applies the CSS where the page cannot see a sheet", Eventually(() => Js(w, cssState).Result is "[\"rgb(1, 2, 3)\",0,0]", step), Js(w, cssState).Result);
    var userKey = await wc.InsertCssAsync("#t { font-size: 31px }", new InsertCssOptions { CssOrigin = CssOrigin.User });
    Check("a user-origin sheet applies too", Eventually(() => Js(w, "getComputedStyle(t).fontSize").Result is "\"31px\"", step), Js(w, "getComputedStyle(t).fontSize").Result);
    await wc.RemoveInsertedCssAsync(key);
    Check("RemoveInsertedCss removes that sheet only", Eventually(() => Js(w, "[getComputedStyle(t).color, getComputedStyle(t).fontSize]").Result is "[\"rgb(0, 0, 0)\",\"31px\"]", step),
        Js(w, "[getComputedStyle(t).color, getComputedStyle(t).fontSize]").Result);
    await wc.RemoveInsertedCssAsync("no-such-key");
    await wc.InsertCssAsync("#t { color: rgb(4, 5, 6) }");
    await wc.LoadFile(page);
    Check("inserted CSS goes with its document", await Js(w, "[getComputedStyle(t).color, getComputedStyle(t).fontSize]") is "[\"rgb(0, 0, 0)\",\"16px\"]",
        await Js(w, "[getComputedStyle(t).color, getComputedStyle(t).fontSize]"));
    _ = userKey;

    // capturePage: the view's device pixels, a rect of it in DIPs, at the page's zoom.
    File.WriteAllText(page, "<!doctype html><style>html,body{margin:0;background:#f00;overflow:hidden}</style><div style='position:absolute;left:10px;top:10px;width:40px;height:30px;background:#00f'></div>");
    await wc.LoadFile(page);
    await Task.Delay(300);
    var ratio = (await wc.ExecuteJavaScriptAsync("devicePixelRatio"))!.GetValue<double>();
    var view = (await wc.ExecuteJavaScriptAsync("[innerWidth, innerHeight]"))!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
    (byte B, byte G, byte R) Pixel(NativeImage image, int x, int y)
    {
        var bytes = image.ToBitmap();
        var at = (y * image.GetSize().Width + x) * 4;
        return (bytes[at], bytes[at + 1], bytes[at + 2]);
    }
    // The capture is in the display's colour space, as Chromium's is, so pure sRGB red or blue shifts on a wide-gamut Mac.
    static bool IsRed((byte B, byte G, byte R) p) => p.R > 200 && p.G < 90 && p.B < 90;
    static bool IsBlue((byte B, byte G, byte R) p) => p.B > 200 && p.R < 90 && p.G < 90;
    var whole = await wc.CapturePageAsync();
    Check("CapturePage without a rect captures the visible page in device pixels, at scale 1",
        whole.GetSize() == ((int)Math.Ceiling(view[0] * ratio), (int)Math.Ceiling(view[1] * ratio)) && whole.GetScaleFactors().SequenceEqual([1.0])
        && IsRed(Pixel(whole, 2, 2)) && IsBlue(Pixel(whole, (int)(30 * ratio), (int)(25 * ratio))), $"{whole.GetSize()} view {view[0]}x{view[1]}@{ratio} scales={string.Join(",", whole.GetScaleFactors())} bitmap={whole.ToBitmap().Length} p22={Pixel(whole, 2, 2)} pIn={Pixel(whole, (int)(30 * ratio), (int)(25 * ratio))}");
    var part = await wc.CapturePageAsync(new Rectangle(10, 10, 40, 30));
    Check("CapturePage with a rect captures that part", part.GetSize() == ((int)Math.Ceiling(40 * ratio), (int)Math.Ceiling(30 * ratio))
        && IsBlue(Pixel(part, 0, 0)) && IsBlue(Pixel(part, part.GetSize().Width - 1, part.GetSize().Height - 1)), $"{part.GetSize()} {Pixel(part, 0, 0)} {Pixel(part, part.GetSize().Width - 1, part.GetSize().Height - 1)}");
    var empty = await wc.CapturePageAsync(new Rectangle(10, 10, 0, 30));
    Check("an empty rect captures the whole page, as Electron's", empty.GetSize() == whole.GetSize(), empty.GetSize());
    wc.SetZoomFactor(2);
    await Task.Delay(500);
    var zoomed = await wc.CapturePageAsync(new Rectangle(20, 20, 80, 60));
    Check("CapturePage's rect is in DIPs of the view, whatever the zoom", zoomed.GetSize() == ((int)Math.Ceiling(80 * ratio), (int)Math.Ceiling(60 * ratio))
        && IsBlue(Pixel(zoomed, 1, 1)) && IsBlue(Pixel(zoomed, zoomed.GetSize().Width - 2, zoomed.GetSize().Height - 2)), zoomed.GetSize());
    wc.SetZoomFactor(1);

    // console-message, update-target-url, cursor-changed, frame-created, as Electron's.
    var consoleLog = new List<string>();
    EventHandler<ConsoleMessageEventArgs> onConsole = (_, e) => { lock (consoleLog) consoleLog.Add($"{e.Level}:{e.Message}:{e.LineNumber}:{Path.GetFileName(new Uri(e.SourceId).LocalPath)}:{e.Frame is null}"); };
    wc.ConsoleMessage += onConsole;
    File.WriteAllText(page, "<!doctype html><style>html,body{margin:0}</style>\n<a id=l href='https://target.test/x' style='position:absolute;left:0;top:0;width:100px;height:40px;display:block'>link</a>\n"
        + "<div id=h style='position:absolute;left:0;top:100px;width:100px;height:40px;cursor:pointer'></div>\n"
        + "<div id=c style='position:absolute;left:0;top:200px;width:100px;height:40px;cursor:url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAgAAAAIAQMAAAD+wSzIAAAABlBMVEX///+/v7+jQ3Y5AAAADklEQVQI12P4AQQKDDgAgfAKERn8L8kAAAAASUVORK5CYII=) 3 4, auto'></div>\n"
        + "<script>console.warn('w1');\nconsole.log('l1');\nconsole.debug('d1');\nconsole.error('e1');</script>");
    await wc.LoadFile(page);
    var logged = Eventually(() => { lock (consoleLog) return consoleLog.Count >= 4; }, step);
    wc.ConsoleMessage -= onConsole;
    string LogText() { lock (consoleLog) return string.Join(" | ", consoleLog); }
    Check("console-message carries the level, message, line and source, as Electron's", logged && LogText().StartsWith(
        $"Warning:w1:5:{Path.GetFileName(page)}:True | Info:l1:6:{Path.GetFileName(page)}:True | Debug:d1:7:{Path.GetFileName(page)}:True | Error:e1:8:{Path.GetFileName(page)}:True", StringComparison.Ordinal), LogText());

    var targets = new List<string>();
    EventHandler<string> onTarget = (_, url) => { lock (targets) targets.Add(url); };
    wc.UpdateTargetUrl += onTarget;
    var cursors = new List<CursorChangedEventArgs>();
    EventHandler<CursorChangedEventArgs> onCursor = (_, e) => { lock (cursors) cursors.Add(e); };
    wc.CursorChanged += onCursor;
    bool SawCursor(string type) { lock (cursors) return cursors.Any(c => c.Type == type); }
    wc.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseMove, 20, 20));
    var hovered = Eventually(() => { lock (targets) return targets.Contains("https://target.test/x"); }, step);
    wc.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseMove, 20, 120));
    var left = Eventually(() => { lock (targets) return targets.Count > 0 && targets[^1] == ""; }, step);
    var hand = Eventually(() => SawCursor("hand"), step);
    wc.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseMove, 20, 320));
    var plain = Eventually(() => SawCursor("pointer"), step);
    wc.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseMove, 20, 220));
    var custom = Eventually(() => SawCursor("custom"), step);
    wc.UpdateTargetUrl -= onTarget;
    wc.CursorChanged -= onCursor;
    Check("update-target-url reports the link under the pointer, then empty", hovered && left, string.Join(",", targets));
    string CursorText() { lock (cursors) return string.Join(",", cursors.Select(c => c.Type)); }
    Check("cursor-changed names the cursor as Electron does: hand for cursor:pointer, pointer for the arrow", hand && plain, CursorText());
    CursorChangedEventArgs? customCursor;
    lock (cursors) customCursor = cursors.LastOrDefault(c => c.Type == "custom");
    Check("a custom cursor comes with its image, size and hotspot", custom && customCursor is { Image: not null, Hotspot: { X: 3, Y: 4 } } cc
        && cc.Size.Width == (int)Math.Round(8 * cc.Scale) && cc.Image.GetSize(cc.Scale) is { Width: > 0 },
        customCursor is null ? CursorText() : $"{customCursor.Size} {customCursor.Hotspot} {customCursor.Scale}");

    var createdFrames = new List<WebFrameMain>();
    EventHandler<WebFrameMain> onFrameCreated = (_, f) => { lock (createdFrames) createdFrames.Add(f); };
    wc.FrameCreated += onFrameCreated;
    await wc.ExecuteJavaScriptAsync("const fr = document.createElement('iframe'); fr.name = 'made'; document.body.append(fr); 1");
    var frameMade = Eventually(() => { lock (createdFrames) return createdFrames.Count == 1; }, step);
    wc.FrameCreated -= onFrameCreated;
    Check("frame-created reports a new iframe with its frame", frameMade && createdFrames[0].Parent == wc.MainFrame, createdFrames.Count);

    // focus and blur follow the page's view; focusing one of its own iframes is not a blur.
    File.WriteAllText(page, "<!doctype html><input id=i><iframe name=inner srcdoc='<input id=j>'></iframe>");
    var fa = new BrowserWindow(new BrowserWindowOptions { Width = 300, Height = 200, X = 40, Y = 40 });
    var fb = new BrowserWindow(new BrowserWindowOptions { Width = 300, Height = 200, X = 380, Y = 40 });
    await fa.WebContents.LoadFile(page);
    await fb.WebContents.LoadFile(page);
    var focusEvents = new List<string>();
    fa.WebContents.Focused += (_, _) => { lock (focusEvents) focusEvents.Add("focus"); };
    fa.WebContents.Blurred += (_, _) => { lock (focusEvents) focusEvents.Add("blur"); };
    string FocusText() { lock (focusEvents) return string.Join(",", focusEvents); }
    fa.WebContents.Focus();
    var gained = Eventually(() => FocusText().EndsWith("focus", StringComparison.Ordinal) && fa.WebContents.IsFocused(), step);
    lock (focusEvents) focusEvents.Clear();
    await fa.WebContents.ExecuteJavaScriptAsync("frames.inner.document.getElementById('j').focus(); 1");
    await Task.Delay(400);
    var stayed = !FocusText().Contains("blur", StringComparison.Ordinal) && fa.WebContents.IsFocused();
    fb.WebContents.Focus();
    var lost = Eventually(() => FocusText().EndsWith("blur", StringComparison.Ordinal) && !fa.WebContents.IsFocused() && fb.WebContents.IsFocused(), step);
    Check("Focus focuses the page and raises focus; IsFocused follows", gained, FocusText());
    Check("focus moving into the page's own iframe is no blur", stayed, FocusText());
    Check("another page taking focus raises blur, and IsFocused moves with it", lost, $"{FocusText()} a={fa.WebContents.IsFocused()} b={fb.WebContents.IsFocused()}");
    fb.Destroy();

    // close: the page goes, destroyed is raised, and its window closes with it.
    var faContents = fa.WebContents;
    var destroyedRaised = false;
    var faClosed = false;
    faContents.Destroyed += (_, _) => destroyedRaised = true;
    fa.Closed += (_, _) => faClosed = true;
    Check("IsDestroyed is false while the page lives", !faContents.IsDestroyed());
    faContents.Close();
    var closedDown = Eventually(() => destroyedRaised && faContents.IsDestroyed() && faClosed, step);
    Check("WebContents.Close destroys the page, raises destroyed and closes its window", closedDown, $"destroyed={destroyedRaised} isDestroyed={faContents.IsDestroyed()} windowClosed={faClosed}");
    var waiting = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await waiting.WebContents.LoadFile(page);
    var waitingContents = waiting.WebContents;
    waitingContents.Close(new WebContentsCloseOptions { WaitForBeforeUnload = true });
    Check("Close waiting for beforeunload closes a page that does not object", Eventually(() => waitingContents.IsDestroyed(), step));
    File.WriteAllText(page, "<!doctype html><script>addEventListener('beforeunload', e => { e.preventDefault(); e.returnValue = ''; });</script>");
    var objecting = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await objecting.WebContents.LoadFile(page);
    var objectingContents = objecting.WebContents;
    var preventions = 0;
    objectingContents.WillPreventUnload += (_, _) => preventions++;
    objectingContents.Close(new WebContentsCloseOptions { WaitForBeforeUnload = true });
    await Task.Delay(1500);
    var kept = !objectingContents.IsDestroyed() && preventions == 1;
    Check("a page whose beforeunload objects stays open when Close waits, with will-prevent-unload and no dialog", kept, $"destroyed={objectingContents.IsDestroyed()} preventions={preventions}");
    objectingContents.WillPreventUnload += (_, e) => e.Cancel = true;
    objectingContents.Close(new WebContentsCloseOptions { WaitForBeforeUnload = true });
    Check("preventDefault in will-prevent-unload lets the page close", Eventually(() => objectingContents.IsDestroyed(), step) && preventions == 2, $"preventions={preventions}");
    var forced = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await forced.WebContents.LoadFile(page);
    var forcedContents = forced.WebContents;
    var forcedPreventions = 0;
    forcedContents.WillPreventUnload += (_, _) => forcedPreventions++;
    // With user activation, so only the forced close keeps beforeunload from running.
    await forcedContents.ExecuteJavaScriptAsync("0", userGesture: true);
    forcedContents.Close();
    Check("Close without waiting skips beforeunload, even after user activation", Eventually(() => forcedContents.IsDestroyed(), step) && forcedPreventions == 0, $"preventions={forcedPreventions}");

    // zoom-changed: Ctrl+wheel the page leaves alone is reported, one per tick, and does not zoom; never on macOS.
    File.WriteAllText(page, "<!doctype html><div id=box style='height:3000px'>zoom</div>");
    await wc.LoadFile(page);
    var zooms = new List<ZoomDirection>();
    EventHandler<ZoomDirection> onZoom = (_, d) => { lock (zooms) zooms.Add(d); };
    wc.ZoomChanged += onZoom;
    string Zooms() { lock (zooms) return string.Join(",", zooms); }
    void Wheel(int deltaY) => wc.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseWheel, 50, 50) { DeltaY = deltaY, Modifiers = InputModifiers.Control });
    Wheel(120);
    Wheel(-120);
    await Task.Delay(800);
    var zoomsSeen = Zooms();
    await wc.ExecuteJavaScriptAsync("box.addEventListener('wheel', e => e.preventDefault(), { passive: false }); 1");
    Wheel(120);
    await Task.Delay(800);
    wc.ZoomChanged -= onZoom;
    var zoomFactor = wc.GetZoomFactor();
    if (OperatingSystem.IsMacOS())
        Check("Ctrl+wheel raises no zoom-changed on macOS, as Chromium's, and does not zoom", Zooms() == "" && zoomFactor == 1, $"{Zooms()} zoom={zoomFactor}");
    else
    {
        Check("Ctrl+wheel raises zoom-changed in then out, one per tick, without zooming the page", zoomsSeen == "In,Out" && zoomFactor == 1, $"{zoomsSeen} zoom={zoomFactor}");
        Check("a Ctrl+wheel the page handles raises no zoom-changed", Zooms() == "In,Out", Zooms());
    }

    // Visual zoom: a touchpad pinch, which Electron's page scale limits of 1 keep from zooming until the app widens them.
    await wc.LoadFile(page);
    async Task<double> PinchedScale()
    {
        await wc.DevToolsCommandAsync("Input.synthesizePinchGesture", w =>
        {
            w.WriteNumber("x", 100);
            w.WriteNumber("y", 100);
            w.WriteNumber("scaleFactor", 2);
            w.WriteString("gestureSourceType", "mouse");
        });
        await Task.Delay(300);
        var scale = Convert.ToDouble((await wc.ExecuteJavaScriptAsync("visualViewport.scale"))?.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        await wc.DevToolsCommandAsync("Emulation.setPageScaleFactor", w => w.WriteNumber("pageScaleFactor", 1));
        return scale;
    }
    var defaultPinch = await PinchedScale();
    Check("a touchpad pinch does not zoom by default, as Electron's page scale limits of 1", defaultPinch == 1, $"scale={defaultPinch}");
    await wc.SetVisualZoomLevelLimits(1, 3);
    await Task.Delay(200);
    var widenedPinch = await PinchedScale();
    // Electron keeps the limits in the page's web preferences, so they outlive a load.
    await wc.LoadFile(page);
    var widenedAfterLoad = await PinchedScale();
    await wc.SetVisualZoomLevelLimits(1, 1);
    await Task.Delay(200);
    var narrowedPinch = await PinchedScale();
    var pinches = $"widened={widenedPinch} afterLoad={widenedAfterLoad} narrowed={narrowedPinch}";
    if (OperatingSystem.IsMacOS())
        Check("SetVisualZoomLevelLimits(1, 3) lets a pinch zoom, also after a load, and (1, 1) stops it again",
            widenedPinch > 1 && widenedAfterLoad > 1 && narrowedPinch == 1, pinches);
    else
        Check("on Windows and Linux a pinch stays off even with wider limits: every Ctrl+wheel is cancelled (drift C37)",
            widenedPinch == 1 && widenedAfterLoad == 1 && narrowedPinch == 1, pinches);

    // did-change-theme-color: the first matching, valid meta theme-color, as uppercase #RRGGBB; raised on change.
    var themes = new List<string>();
    EventHandler<string?> onTheme = (_, c) => { lock (themes) themes.Add(c ?? "null"); };
    wc.DidChangeThemeColor += onTheme;
    string Themes() { lock (themes) return string.Join(",", themes); }
    File.WriteAllText(page, "<!doctype html><meta name=theme-color media='(min-width: 99999px)' content='blue'><meta name=Theme-Color content='not a color'>"
        + "<meta name=theme-color content='rgb(255, 128, 0)'><meta name=theme-color content=red><p>themed</p>");
    await wc.LoadFile(page);
    var firstTheme = Eventually(() => Themes() == "#FF8000", step);
    await wc.ExecuteJavaScriptAsync("document.querySelector('meta[content=\"rgb(255, 128, 0)\"]').content = 'lime'; 1");
    var changedTheme = Eventually(() => Themes() == "#FF8000,#00FF00", step);
    await wc.ExecuteJavaScriptAsync("document.querySelector('p').textContent = 'same'; 1");
    await Task.Delay(400);
    var unchanged = Themes() == "#FF8000,#00FF00";
    File.WriteAllText(page, "<!doctype html><p>plain</p>");
    await wc.LoadFile(page);
    var clearedTheme = Eventually(() => Themes() == "#FF8000,#00FF00,null", step);
    wc.DidChangeThemeColor -= onTheme;
    Check("did-change-theme-color picks the first matching, valid meta, as #RRGGBB", firstTheme, Themes());
    Check("did-change-theme-color follows a change, and only a change", changedTheme && unchanged, Themes());
    Check("a page without a theme color raises null", clearedTheme, Themes());

    // fromFrame: a frame's page, from the main frame or an iframe.
    File.WriteAllText(page, "<!doctype html><iframe srcdoc='x'></iframe>");
    await wc.LoadFile(page);
    Eventually(() => wc.MainFrame?.Frames.Count == 1, step);
    var framePage = wc.MainFrame is { } topFrame && WebContents.FromFrame(topFrame) == wc && WebContents.FromFrame(topFrame.Frames[0]) == wc;
    var closing = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await closing.WebContents.LoadFile(page);
    var goneFrame = closing.WebContents.MainFrame!;
    closing.Destroy();
    Check("WebContents.FromFrame finds a frame's page, and null once the page is gone", framePage && Eventually(() => WebContents.FromFrame(goneFrame) is null, step));

    // devtools-opened and devtools-closed; inspectElement opens DevTools at a point; nothing when devTools is off.
    var inspected = new BrowserWindow(new BrowserWindowOptions { Width = 300, Height = 200 });
    await inspected.WebContents.LoadFile(page);
    var devtoolsLog = new List<string>();
    inspected.WebContents.DevToolsOpened += (_, _) => { lock (devtoolsLog) devtoolsLog.Add("opened"); };
    inspected.WebContents.DevToolsClosed += (_, _) => { lock (devtoolsLog) devtoolsLog.Add("closed"); };
    string DevToolsLog() { lock (devtoolsLog) return string.Join(",", devtoolsLog); }
    inspected.WebContents.OpenDevTools();
    var openedSeen = Eventually(() => DevToolsLog() == "opened" && inspected.WebContents.IsDevToolsOpened(), step);
    inspected.WebContents.CloseDevTools();
    var closedSeen = Eventually(() => DevToolsLog() == "opened,closed" && !inspected.WebContents.IsDevToolsOpened(), step);
    inspected.WebContents.InspectElement(10, 10);
    var inspectOpened = Eventually(() => DevToolsLog() == "opened,closed,opened", step);
    inspected.WebContents.CloseDevTools();
    Eventually(() => DevToolsLog() == "opened,closed,opened,closed", step);
    Check("devtools-opened and devtools-closed follow OpenDevTools and CloseDevTools", openedSeen && closedSeen, DevToolsLog());
    Check("InspectElement opens DevTools", inspectOpened, DevToolsLog());
    inspected.Destroy();
    var locked = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { DevTools = false } });
    await locked.WebContents.LoadFile(page);
    var lockedOpened = 0;
    locked.WebContents.DevToolsOpened += (_, _) => Interlocked.Increment(ref lockedOpened);
    locked.WebContents.InspectElement(10, 10);
    await Task.Delay(1000);
    Check("InspectElement does nothing when devTools is off", lockedOpened == 0 && !locked.WebContents.IsDevToolsOpened(), lockedOpened);
    locked.Destroy();

    // content-bounds-updated: the page's moveTo and resizeTo move its window unless prevented.
    var mover = new BrowserWindow(new BrowserWindowOptions { Width = 320, Height = 240, X = 60, Y = 80 });
    File.WriteAllText(page, "<!doctype html><p>mover</p>");
    await mover.WebContents.LoadFile(page);
    var asked = new List<Rectangle>();
    var preventNext = false;
    mover.WebContents.ContentBoundsUpdated += (_, e) => { lock (asked) asked.Add(e.Bounds); e.Cancel = preventNext; };
    await mover.WebContents.ExecuteJavaScriptAsync("window.moveTo(140, 160); 1");
    var movedTo = Eventually(() => { lock (asked) return asked.Count == 1; }, step) && Eventually(() => mover.GetBounds() is { X: 140, Y: 160 }, step);
    preventNext = true;
    await mover.WebContents.ExecuteJavaScriptAsync("window.resizeTo(500, 400); 1");
    var askedResize = Eventually(() => { lock (asked) return asked.Count == 2; }, step);
    await Task.Delay(300);
    Rectangle[] askedNow;
    lock (asked) askedNow = [.. asked];
    Check("window.moveTo raises content-bounds-updated and moves the window", movedTo && askedNow[0] is { X: 140, Y: 160 }, $"{string.Join(";", askedNow)} now {mover.GetBounds()}");
    Check("preventDefault in content-bounds-updated keeps the window as it was", askedResize && askedNow[1] is { Width: 500, Height: 400 } && mover.GetBounds() is { Width: 320, Height: 240 },
        $"{string.Join(";", askedNow)} now {mover.GetBounds()}");
    mover.Destroy();

    // media-started-playing and media-paused, once per start and stop; an ended player pauses once.
    var wav = new MemoryStream();
    using (var wr = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
    {
        const int rate = 8000, samples = rate * 3 / 10;
        wr.Write("RIFF"u8); wr.Write(36 + samples * 2); wr.Write("WAVEfmt "u8); wr.Write(16); wr.Write((short)1); wr.Write((short)1);
        wr.Write(rate); wr.Write(rate * 2); wr.Write((short)2); wr.Write((short)16); wr.Write("data"u8); wr.Write(samples * 2); wr.Write(new byte[samples * 2]);
    }
    File.WriteAllText(page, $"<!doctype html><audio id=au src='data:audio/wav;base64,{Convert.ToBase64String(wav.ToArray())}'></audio>");
    await wc.LoadFile(page);
    var media = new List<string>();
    EventHandler onStarted = (_, _) => { lock (media) media.Add("start"); };
    EventHandler onPaused = (_, _) => { lock (media) media.Add("pause"); };
    wc.MediaStartedPlaying += onStarted;
    wc.MediaPaused += onPaused;
    string Media() { lock (media) return string.Join(",", media); }
    await wc.ExecuteJavaScriptAsync("au.loop = true; au.play(); 1", userGesture: true);
    var started = Eventually(() => Media() == "start", step);
    await wc.ExecuteJavaScriptAsync("au.pause(); 1");
    var paused = Eventually(() => Media() == "start,pause", step);
    await wc.ExecuteJavaScriptAsync("au.loop = false; au.currentTime = 0; au.play(); 1", userGesture: true);
    var ended = Eventually(() => Media() == "start,pause,start,pause", step) && await Js(w, "au.ended") is "true";
    wc.MediaStartedPlaying -= onStarted;
    wc.MediaPaused -= onPaused;
    Check("media-started-playing and media-paused follow play and pause", started && paused, Media());
    Check("a player that reaches its end raises media-paused once", ended, Media());

    // Device emulation: Electron's parameters reach the page.
    var before = await Js(w, "[innerWidth, devicePixelRatio, screen.width]");
    wc.EnableDeviceEmulation(new DeviceEmulationParameters
    {
        ScreenSize = (400, 800),
        ViewPosition = new Point(10, 20),
        ViewSize = (300, 200),
        DeviceScaleFactor = 2,
    });
    const string emulated = "[innerWidth, innerHeight, devicePixelRatio, screen.width, screen.height, screenX, screenY]";
    Check("EnableDeviceEmulation emulates the view size, scale factor, screen and view position",
        Eventually(() => Js(w, emulated).Result is "[300,200,2,400,800,10,20]", step), Js(w, emulated).Result);
    wc.EnableDeviceEmulation(new DeviceEmulationParameters { ScreenPosition = EmulatedScreenPosition.Mobile, ViewSize = (300, 200) });
    Check("a mobile screen lays a page without a viewport out at the mobile default width", Eventually(() => Js(w, "innerWidth").Result is "980", step), Js(w, "innerWidth").Result);
    wc.DisableDeviceEmulation();
    Check("DisableDeviceEmulation restores the real metrics", Eventually(() => Js(w, "[innerWidth, devicePixelRatio, screen.width]").Result as string == before as string, step),
        $"{Js(w, "[innerWidth, devicePixelRatio, screen.width]").Result} vs {before}");

    // Audio: mute state.
    Check("a page starts unmuted", !wc.IsAudioMuted());
    wc.SetAudioMuted(true);
    Check("SetAudioMuted(true) mutes", Eventually(() => wc.IsAudioMuted(), step));
    wc.SetAudioMuted(false);
    Check("SetAudioMuted(false) unmutes", Eventually(() => !wc.IsAudioMuted(), step));

    // forcefullyCrashRenderer: the renderer goes, the page is crashed until a reload brings a new one.
    var crash = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await crash.WebContents.LoadUrl(origin + "/");
    RenderProcessGoneEventArgs? gone = null;
    crash.WebContents.RenderProcessGone += (_, e) => gone = e;
    Check("a live page is not crashed", !crash.WebContents.IsCrashed());
    crash.WebContents.ForcefullyCrashRenderer();
    Check("ForcefullyCrashRenderer ends the renderer: RenderProcessGone (crashed or killed), IsCrashed",
        Eventually(() => gone is { Reason: "crashed" or "killed" } && crash.WebContents.IsCrashed(), TimeSpan.FromSeconds(15)), gone);
    crash.WebContents.Reload();
    Check("a reload after the crash runs a new renderer and the page is no longer crashed",
        Eventually(() => !crash.WebContents.IsCrashed(), step) && Eventually(() => crash.WebContents.ExecuteJavaScriptAsync("document.title").Wait(1000), step),
        crash.WebContents.IsCrashed());
    crash.Destroy();

    w.Destroy();
    listener.Stop();
    foreach (var gate in gates.Values) gate.TrySetResult();
    Directory.Delete(dir, recursive: true);
}

async Task WebFrameAsync()
{
    var dir = Directory.CreateTempSubdirectory("oriel-webframe-").FullName;
    var preload = Path.Combine(dir, "preload.js");
    File.WriteAllText(preload, string.Join("\n",
        "const { webFrame, contextBridge } = oriel;",
        "contextBridge.exposeInMainWorld('wf', {",
        "  zoom: (f) => { webFrame.setZoomFactor(f); return webFrame.getZoomFactor(); },",
        "  getZoom: () => webFrame.getZoomFactor(),",
        "  css: (c) => webFrame.insertCSS(c),",
        "  uncss: (k) => webFrame.removeInsertedCSS(k),",
        "  exec: (code) => webFrame.executeJavaScript(code),",
        "  iso: (code) => webFrame.executeJavaScriptInIsolatedWorld(5, [{ code }]),",
        "  token: () => webFrame.frameToken,",
        "  text: (t) => webFrame.insertText(t),",
        "});"));
    var page = Path.Combine(dir, "webframe.html");
    File.WriteAllText(page, "<!doctype html><script>window.pageVar = 'from-page';</script><input id=i><iframe srcdoc='<p>child'></iframe>");
    var w = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: webFrame", Width = 400, Height = 300, WebPreferences = new WebPreferences { Preload = preload } });
    Fires(h => w.WebContents.DidFinishLoad += h, h => w.WebContents.DidFinishLoad -= h, () => w.WebContents.LoadFile(page), step);
    async Task<string?> Page(string code) => (await w.WebContents.ExecuteJavaScriptAsync(code))?.ToString();

    var zoomed = await Page("wf.zoom(1.5)");
    Check("webFrame.setZoomFactor zooms the page; getZoomFactor answers at once", zoomed == "1.5" && Eventually(() => Math.Abs(w.WebContents.GetZoomFactor() - 1.5) < 0.01, step), $"{zoomed} {w.WebContents.GetZoomFactor()}");
    w.WebContents.SetZoomFactor(2);
    Check("a zoom set from the main process reaches webFrame.getZoomFactor", Eventually(() => Page("wf.getZoom()").Result == "2", step), Page("wf.getZoom()").Result);
    w.WebContents.SetZoomFactor(1);

    var sheets = await Page("document.styleSheets.length");
    var key = await Page("wf.css('body { background: rgb(1, 2, 3) }')");
    var styled = Eventually(() => Page("getComputedStyle(document.body).backgroundColor").Result == "rgb(1, 2, 3)", step);
    Check("webFrame.insertCSS styles the frame, out of document.styleSheets, and returns a key", styled && key is { Length: > 0 } && await Page("document.styleSheets.length") == sheets, $"{key} {sheets}");
    var child = Eventually(() => w.WebContents.MainFrame?.Frames.Count == 1, step) ? w.WebContents.MainFrame!.Frames[0] : null;
    Check("insertCSS styles only its own frame", child is not null && (await child.ExecuteJavaScriptAsync("getComputedStyle(document.body).backgroundColor"))?.ToString() != "rgb(1, 2, 3)");
    await child!.ExecuteJavaScriptAsync("wf.css('body { color: rgb(4, 5, 6) }')");
    Check("insertCSS from an iframe styles that iframe, not the page", Eventually(() => child.ExecuteJavaScriptAsync("getComputedStyle(document.body).color").Result?.ToString() == "rgb(4, 5, 6)", step)
        && await Page("getComputedStyle(document.body).color") != "rgb(4, 5, 6)");
    await Page($"wf.uncss('{key}')");
    Check("webFrame.removeInsertedCSS removes it", Eventually(() => Page("getComputedStyle(document.body).backgroundColor").Result != "rgb(1, 2, 3)", step), Page("getComputedStyle(document.body).backgroundColor").Result);

    Check("webFrame.executeJavaScript runs in the page's world", await Page("wf.exec('window.pageVar')") == "from-page");
    var isolated = await Page("wf.iso('typeof window.pageVar')");
    await Page("wf.iso('window.kept = 7')");
    Check("webFrame.executeJavaScriptInIsolatedWorld runs apart from the page, in a world that persists", isolated == "undefined" && await Page("wf.iso('window.kept')") == "7", isolated);
    Check("webFrame's scripts run in the frame that asks", (await child.ExecuteJavaScriptAsync("wf.iso('document.body.textContent')"))?.ToString() == "child"
        && (await child.ExecuteJavaScriptAsync("wf.exec('document.body.textContent')"))?.ToString() == "child");
    Check("webFrame.frameToken is the frame's", await Page("wf.token()") == w.WebContents.MainFrame?.FrameToken
        && child is not null && (await child.ExecuteJavaScriptAsync("wf.token()"))?.ToString() == child.FrameToken, await Page("wf.token()"));
    w.Focus();
    await w.WebContents.ExecuteJavaScriptAsync("i.focus(); 1", userGesture: true);
    await Page("wf.text('typed')");
    Check("webFrame.insertText types into the focused field", Eventually(() => Page("i.value").Result == "typed", step), Page("i.value").Result);
    w.Destroy();
    Directory.Delete(dir, recursive: true);
}

async Task MenuRolesAsync(BrowserWindow win)
{
    // Electron v44's role table, read on this OS.
    string? Label(MenuItemRole role) => new MenuItem { Role = role }.Label;
    if (OperatingSystem.IsLinux())
        Check("Linux role labels and Redo are Electron's", Label(MenuItemRole.Close) == "Close" && Label(MenuItemRole.Quit) == "Quit" && Label(MenuItemRole.About) == "About"
            && new MenuItem { Role = MenuItemRole.Redo }.Accelerator == "Shift+CmdOrCtrl+Z", $"{Label(MenuItemRole.Close)} {Label(MenuItemRole.Quit)} {Label(MenuItemRole.About)}");
    // toggleSpellChecker flips the focused page's session.
    var session = win.WebContents.Session;
    var before = session.IsSpellCheckerEnabled();
    new MenuItem { Role = MenuItemRole.ToggleSpellChecker }.Activate(win);
    var flipped = Eventually(() => session.IsSpellCheckerEnabled() != before, step);
    new MenuItem { Role = MenuItemRole.ToggleSpellChecker }.Activate(win);
    Check("the toggleSpellChecker role turns the page's spell checker off and on", flipped && Eventually(() => session.IsSpellCheckerEnabled() == before, step));
    await Task.CompletedTask;
}

async Task MouseEventsAsync()
{
    var page = Path.Combine(Path.GetTempPath(), "oriel-mouse-events.html");
    File.WriteAllText(page, "<!doctype html><body style='height:4000px;margin:0'><script>window.log=[];" +
        "document.addEventListener('mouseover',()=>log.push('over'));" +
        "document.addEventListener('mouseout',e=>{if(!e.relatedTarget)log.push('out')});" +
        "document.addEventListener('contextmenu',e=>{log.push('ctx');e.preventDefault()});</script>");
    var w = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300 });
    await w.WebContents.LoadFile(page);
    async Task<string> Log() => (await w.WebContents.ExecuteJavaScriptAsync("log.join(',')"))?.ToString() ?? "";

    // Chromium's convention: a negative deltaY scrolls the page down.
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseWheel, 50, 50) { DeltaY = -300 });
    var scrolled = 0;
    for (var i = 0; i < 30 && scrolled == 0; i++)
    {
        await Task.Delay(100);
        scrolled = (await w.WebContents.ExecuteJavaScriptAsync("scrollY"))?.GetValue<int>() ?? 0;
    }
    Check("SendInputEvent MouseWheel scrolls the page, deltaY negative downwards as Electron", scrolled > 0, scrolled);

    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseEnter, 20, 20));
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseLeave, 20, 20));
    string log = "";
    for (var i = 0; i < 30 && !log.Contains("out"); i++)
    {
        await Task.Delay(100);
        log = await Log();
    }
    Check("SendInputEvent MouseEnter and MouseLeave reach the page", log.Contains("over") && log.Contains("out"), log);

    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.ContextMenu, 30, 30));
    for (var i = 0; i < 30 && !log.Contains("ctx"); i++)
    {
        await Task.Delay(100);
        log = await Log();
    }
    Check("SendInputEvent ContextMenu raises the page's contextmenu", log.Contains("ctx"), log);
    w.Destroy();
    File.Delete(page);
}

async Task InputEventAsync()
{
    var page = Path.Combine(Path.GetTempPath(), "oriel-input-event.html");
    File.WriteAllText(page, "<!doctype html><title>oriel-input-event</title><body style='margin:0;height:3000px'><input id=k><script>window.moves=[];" +
        "addEventListener('mousemove',e=>moves.push([e.clientX,e.clientY,e.screenX,e.screenY]))</script>");
    var w = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300 });
    await w.WebContents.LoadFile(page);
    var seen = new System.Collections.Concurrent.ConcurrentQueue<WebInputEvent>();
    EventHandler<WebInputEvent> onInput = (_, e) => seen.Enqueue(e);
    w.WebContents.InputEvent += onInput;
    w.Focus();
    await Js(w, "document.getElementById('k').focus(); 1");
    List<WebInputEvent> Keys() => [.. seen.Where(e => e is WebKeyboardEvent)];
    List<WebInputEvent> Mice() => [.. seen.Where(e => e is WebMouseEvent)];
    string Types(IEnumerable<WebInputEvent> events) => string.Join(",", events.Select(e => e.Type.ElectronName()));
    async Task<double[]> LastMove() => (await w.WebContents.ExecuteJavaScriptAsync("moves.at(-1) ?? []"))!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
    // The pointer reporting is turned on in the page asynchronously.
    await Task.Delay(500);

    // Keys from sendInputEvent: Electron forwards keyDown as rawKeyDown, and a char on its own.
    seen.Clear();
    w.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "A") { Modifiers = InputModifiers.Shift });
    w.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.Char, "A") { Modifiers = InputModifiers.Shift });
    w.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "A") { Modifiers = InputModifiers.Shift });
    Check("InputEvent: sent keys arrive as rawKeyDown, char, keyUp", Eventually(() => Keys().Count >= 3, step) && Types(Keys()) == "rawKeyDown,char,keyUp", Types(Keys()));
    var down = Keys().FirstOrDefault() as WebKeyboardEvent;
    Check("InputEvent: a key event's key, code, location and modifiers", down is { Key: "A", Code: "KeyA", Location: 0, Shift: true, Control: false, IsAutoRepeat: false }
        && down.Modifiers.ElectronNames().Contains("shift"), down);
    var typed = Keys().ElementAtOrDefault(1) as WebKeyboardEvent;
    Check("InputEvent: a char event carries the character", typed is { Type: InputEventType.Char, Key: "A" }, typed);

    // A key before-input-event cancels never reaches the renderer, so input-event never sees it.
    EventHandler<BeforeInputEventArgs> cancelQ = (_, e) => { if (e.Input.Key == "q") e.Cancel = true; };
    w.WebContents.BeforeInputEvent += cancelQ;
    seen.Clear();
    w.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "Q"));
    w.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "B"));
    Check("InputEvent: a key BeforeInputEvent cancelled is not reported, the next one is",
        Eventually(() => Keys().Any(e => e is WebKeyboardEvent { Key: "b" }), step) && !Keys().Any(e => e is WebKeyboardEvent { Key: "q" }),
        string.Join(",", Keys().Select(e => ((WebKeyboardEvent)e).Key)));
    w.WebContents.BeforeInputEvent -= cancelQ;

    // Mouse events from sendInputEvent.
    seen.Clear();
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseMove, 40, 50));
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, 40, 50));
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseUp, 40, 50));
    Check("InputEvent: sent mouse events arrive as mouseMove, mouseDown, mouseUp",
        Eventually(() => Mice().Any(e => e.Type == InputEventType.MouseUp), step) && Types(Mice()) == "mouseMove,mouseDown,mouseUp", Types(Mice()));
    var move = Mice().FirstOrDefault() as WebMouseEvent;
    var moved = await LastMove();
    Check("InputEvent: a move's position, global position, no button, no clicks and no modifiers",
        move is { X: 40, Y: 50, Button: MouseButton.None, ClickCount: 0, Modifiers: InputModifiers.None } && moved.Length == 4 && move.GlobalX == moved[2] && move.GlobalY == moved[3],
        $"{move} page {string.Join(",", moved)}");
    var press = Mice().ElementAtOrDefault(1) as WebMouseEvent;
    var release = Mice().ElementAtOrDefault(2) as WebMouseEvent;
    Check("InputEvent: a press and release carry the button and click count",
        press is { Button: MouseButton.Left, ClickCount: 1, X: 40, Y: 50 } && release is { Button: MouseButton.Left, ClickCount: 1 }, $"{press} {release}");

    // A wheel is not one of Blink's mouse event types: Electron reports its type and modifiers only.
    seen.Clear();
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseWheel, 50, 50) { DeltaY = -120, Modifiers = InputModifiers.Control | InputModifiers.Alt });
    Check("InputEvent: a wheel is a plain InputEvent with its modifiers",
        Eventually(() => seen.Any(e => e.Type == InputEventType.MouseWheel), step) && seen.First(e => e.Type == InputEventType.MouseWheel) is var wheel
            && wheel.GetType() == typeof(WebInputEvent) && wheel.Modifiers == (InputModifiers.Control | InputModifiers.Alt),
        seen.FirstOrDefault(e => e.Type == InputEventType.MouseWheel));

    seen.Clear();
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseLeave, 20, 20));
    Check("InputEvent: the pointer leaving the page is mouseLeave", Eventually(() => seen.Any(e => e.Type == InputEventType.MouseLeave), step), Types(seen));

    // Chromium's positions are DIPs, so they scale with the zoom; DevTools' sent positions are CSS pixels.
    w.WebContents.SetZoomFactor(2.0);
    Eventually(() => Math.Abs(w.WebContents.GetZoomFactor() - 2.0) < 0.01, step);
    seen.Clear();
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseMove, 60, 70));
    Eventually(() => Mice().Any(), step);
    var zoomed = Mice().FirstOrDefault() as WebMouseEvent;
    moved = await LastMove();
    Check("InputEvent: positions are DIPs, the page's CSS pixels times the zoom",
        zoomed is not null && moved.Length == 4 && zoomed.X == moved[0] * 2 && zoomed.Y == moved[1] * 2 && zoomed.X == 120, $"{zoomed} page {string.Join(",", moved)}");
    w.WebContents.SetZoomFactor(1.0);
    Eventually(() => Math.Abs(w.WebContents.GetZoomFactor() - 1.0) < 0.01, step);

    // A new document's preload world starts with reporting off; the main process turns it on again.
    await w.WebContents.LoadFile(page);
    await Task.Delay(500);
    seen.Clear();
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, 30, 30));
    w.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseUp, 30, 30));
    Check("InputEvent: pointer events are still reported after a navigation", Eventually(() => Mice().Any(e => e.Type == InputEventType.MouseUp), step), Types(seen));
    await Js(w, "document.getElementById('k').focus(); 1");

    // Real input, through the X server as a user's.
    if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/xdotool"))
    {
        string X(string args)
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/timeout", "10 /usr/bin/xdotool " + args) { RedirectStandardOutput = true })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output.Trim() : "";
        }
        var id = X("search --sync --name oriel-input-event").Split('\n')[0];
        X($"windowactivate --sync {id}");
        await Js(w, "document.getElementById('k').focus(); 1");
        await Task.Delay(300);
        seen.Clear();
        X("key a");
        Check("InputEvent: a real key arrives as rawKeyDown, char, keyUp", Eventually(() => Keys().Count >= 3, step) && Types(Keys()) == "rawKeyDown,char,keyUp", Types(Keys()));
        var real = Keys().Cast<WebKeyboardEvent>().ToList();
        Check("InputEvent: a real key's key and code", real.Count == 3 && real.All(k => k is { Key: "a", Code: "KeyA" }), string.Join(" ", real));

        X($"mousemove --window {id} 100 120");
        await Task.Delay(300);
        seen.Clear();
        X($"mousemove --window {id} 110 125 click 1");
        Check("InputEvent: a real click arrives as mouseMove, mouseDown, mouseUp",
            Eventually(() => Mice().Any(e => e.Type == InputEventType.MouseUp), step) && Types(Mice()).EndsWith("mouseMove,mouseDown,mouseUp", StringComparison.Ordinal), Types(Mice()));
        var pointer = X("getmouselocation --shell").Split('\n').Select(l => l.Split('=')).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1]);
        var click = Mice().LastOrDefault(e => e.Type == InputEventType.MouseDown) as WebMouseEvent;
        Check("InputEvent: a real click's button, count and screen position",
            click is { Button: MouseButton.Left, ClickCount: 1 } && pointer.TryGetValue("X", out var px) && pointer.TryGetValue("Y", out var py)
                && click.GlobalX == double.Parse(px) && click.GlobalY == double.Parse(py),
            $"{click} pointer {string.Join(",", pointer)}");
    }

    w.WebContents.InputEvent -= onInput;
    w.Destroy();
    File.Delete(page);
}

async Task PreferredSizeAsync()
{
    var page = Path.Combine(Path.GetTempPath(), "oriel-preferred-size.html");
    File.WriteAllText(page, "<!doctype html><style>@keyframes grow { to { height: 333px } }</style><body style='margin:0'><div id=d style='width:300px;height:150px'></div>");
    async Task Run(BrowserWindow w, string code) => await w.WebContents.ExecuteJavaScriptAsync(code + "; 1");

    var off = new BrowserWindow(new BrowserWindowOptions { Width = 500, Height = 400 });
    var offSizes = new System.Collections.Concurrent.ConcurrentQueue<System.Drawing.Size>();
    off.WebContents.PreferredSizeChanged += (_, s) => offSizes.Enqueue(s);
    await off.WebContents.LoadFile(page);
    await Run(off, "d.style.width = '320px'");
    await Task.Delay(1000);
    Check("PreferredSizeChanged is not raised without EnablePreferredSizeMode", offSizes.IsEmpty, string.Join(" ", offSizes));
    off.Destroy();

    var w = new BrowserWindow(new BrowserWindowOptions { Width = 500, Height = 400, WebPreferences = new WebPreferences { EnablePreferredSizeMode = true } });
    var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var sizes = new System.Collections.Concurrent.ConcurrentQueue<System.Drawing.Size>();
    w.WebContents.DidStopLoading += (_, _) => order.Enqueue("stop");
    w.WebContents.PreferredSizeChanged += (_, s) => { sizes.Enqueue(s); order.Enqueue("size"); };
    // An image answered late holds the load open well after the page has laid out, so a size sent before the stop shows.
    var slow = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    slow.Start();
    var port = ((System.Net.IPEndPoint)slow.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        using var client = await slow.AcceptTcpClientAsync();
        var stream = client.GetStream();
        _ = await stream.ReadAsync(new byte[4096]);
        await Task.Delay(1500);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(png);
    });
    var slowPage = Path.Combine(Path.GetTempPath(), "oriel-preferred-size-slow.html");
    File.WriteAllText(slowPage, $"<!doctype html><style>@keyframes grow {{ to {{ height: 333px }} }}</style><body style='margin:0'><div id=d style='width:300px;height:150px'></div><img src='http://127.0.0.1:{port}/slow.png' style='position:absolute;left:0;top:0;width:1px;height:1px'>");
    await w.WebContents.LoadFile(slowPage);
    slow.Stop();
    File.Delete(slowPage);
    Check("PreferredSizeChanged: the content's size, once the load stops",
        Eventually(() => !sizes.IsEmpty, step) && sizes.First() == new System.Drawing.Size(300, 150) && order.First() == "stop", $"{string.Join(" ", sizes)} order {string.Join(",", order)}");

    sizes.Clear();
    await Run(w, "d.style.width = '400px'; d.style.height = '250px'");
    Check("PreferredSizeChanged follows the content", Eventually(() => sizes.LastOrDefault() == new System.Drawing.Size(400, 250), step), string.Join(" ", sizes));

    sizes.Clear();
    await Run(w, "d.title = 'unchanged'");
    await Task.Delay(800);
    Check("PreferredSizeChanged is not raised when the size stays", sizes.IsEmpty, string.Join(" ", sizes));

    // The page's own box stays put; only its narrowest width changes.
    await Run(w, "d.style.width = 'auto'; d.innerHTML = '<span id=s style=\"display:inline-block;width:200px;height:20px\"></span>'");
    Eventually(() => sizes.LastOrDefault() == new System.Drawing.Size(200, 250), step);
    await Run(w, "s.style.width = '260px'");
    Check("PreferredSizeChanged follows a change to the narrowest width alone", Eventually(() => sizes.LastOrDefault() == new System.Drawing.Size(260, 250), step), string.Join(" ", sizes));

    // An animation changes the size with no DOM change after it starts.
    await Run(w, "d.innerHTML = ''; d.style.width = '300px'; d.style.animation = 'grow 0.3s forwards'");
    Check("PreferredSizeChanged follows a size change no DOM change makes", Eventually(() => sizes.LastOrDefault() == new System.Drawing.Size(300, 333), step), string.Join(" ", sizes));
    await Run(w, "d.style.animation = ''");

    // Chromium's width is the minimum: content that wraps is measured at its narrowest.
    await Run(w, "d.style.width = 'auto'; d.style.height = 'auto'; d.innerHTML = '<span style=\"display:inline-block;width:120px;height:20px\"></span> <span style=\"display:inline-block;width:90px;height:20px\"></span>'");
    Check("PreferredSizeChanged: the width is the min-content width, the height the laid-out one",
        Eventually(() => sizes.LastOrDefault().Width == 120, step) && sizes.Last().Height is > 0 and < 40, string.Join(" ", sizes));

    // The root's box is the window's height here; its scroll height is the content's.
    await Run(w, "document.documentElement.style.height = '100%'; document.body.style.height = '100%'; d.innerHTML = ''; d.style.width = '200px'; d.style.height = '1500px'");
    Check("PreferredSizeChanged: taller than the window, the height is the scroll height", Eventually(() => sizes.LastOrDefault() == new System.Drawing.Size(200, 1500), step), string.Join(" ", sizes));

    w.WebContents.SetZoomFactor(1.5);
    Check("PreferredSizeChanged: DIPs scale with the zoom", Eventually(() => sizes.LastOrDefault() == new System.Drawing.Size(300, 2250), step), string.Join(" ", sizes));
    // Chromium keeps a page's zoom in the profile, so the next run would start this page zoomed.
    w.WebContents.SetZoomFactor(1);
    w.Destroy();
    File.Delete(page);
}

// Windows: the shell's IFileDialog, read and answered through its own controls, as a user would.
async Task WindowsDialogsAsync(BrowserWindow win, string dir)
{
    var real = Path.Combine(dir, "real.txt");
    File.WriteAllText(real, "");
    File.WriteAllText(Path.Combine(dir, "a.txt"), "");
    File.WriteAllText(Path.Combine(dir, "b.dat"), "");
    var secret = Path.Combine(dir, "secret.txt");
    File.WriteAllText(secret, "");
    File.SetAttributes(secret, FileAttributes.Hidden);
    Directory.CreateDirectory(Path.Combine(dir, "sub1"));
    Directory.CreateDirectory(Path.Combine(dir, "sub2"));
    var winHwnd = win.GetNativeWindowHandle();
    static bool Same(string? a, string? b) => a is not null && b is not null
        && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    static bool Listed(List<string> names, string file) => names.Contains(file) || names.Contains(Path.GetFileNameWithoutExtension(file));

    // Waits for the dialog and for its shell view to fill in: commands before that can be dropped.
    nint Find(string title)
    {
        var hwnd = (nint)0;
        if (!Eventually(() => (hwnd = WindowsShell.FileDialog(title)) != 0, step)) return 0;
        Thread.Sleep(1000);
        return hwnd;
    }
    static void Press(nint dialog, bool accept) => WinDialog.PostMessageW(dialog, 0x0111 /* WM_COMMAND */, accept ? 1 /* IDOK */ : 2 /* IDCANCEL */, 0);
    void Type(nint dialog, string text) => WindowsShell.SetText(WindowsShell.NameBox(dialog), text);
    async Task<T?> Result<T>(Task<T> dialog) where T : class => await Task.WhenAny(dialog, Task.Delay(step)) == dialog ? dialog.Result : null;
    static string Paths(OpenDialogReturnValue? r) => r is null ? "none" : $"{r.Canceled}: {string.Join(",", r.FilePaths)}";

    // Window-less: no owner, as Electron passes none; defaultPath names a file, so the name box and folder follow it.
    var loose = Dialog.ShowOpenDialogAsync(new OpenDialogOptions { Title = "Oriel windowless open", DefaultPath = real });
    var d = Find("Oriel windowless open");
    Check("window-less open dialog shows", d != 0);
    Check("window-less open dialog has no owner", d != 0 && WindowsShell.Owner(d) == 0, WindowsShell.Owner(d));
    Check("defaultPath file: the name box shows its name", WindowsShell.Text(WindowsShell.NameBox(d)) == "real.txt", WindowsShell.Text(WindowsShell.NameBox(d)));
    Check("defaultPath file: the dialog opens in its directory", Same(WindowsShell.Address(d), dir), WindowsShell.Address(d));
    Press(d, accept: true);
    var looseResult = await Result(loose);
    Check("window-less open accepts and returns the file", looseResult is { Canceled: false, FilePaths: [var p] } && Same(p, real), Paths(looseResult));

    // With a window: owned by it and modal to it; buttonLabel on the accept button; defaultPath a directory.
    var owned = Dialog.ShowOpenDialogAsync(win, new OpenDialogOptions { Title = "Oriel owned open", DefaultPath = dir, ButtonLabel = "Pick it" });
    d = Find("Oriel owned open");
    Check("an open dialog with a window is owned by it", d != 0 && WindowsShell.Owner(d) == winHwnd, $"{WindowsShell.Owner(d)} vs {winHwnd}");
    Check("the owner is disabled while the dialog is up", d != 0 && !WindowsShell.IsEnabled(winHwnd));
    Check("buttonLabel is the accept button's text", WindowsShell.DialogItemText(d, 1) == "Pick it", WindowsShell.DialogItemText(d, 1));
    Check("defaultPath directory: the dialog opens in it with an empty name box",
        Same(WindowsShell.Address(d), dir) && WindowsShell.Text(WindowsShell.NameBox(d)) == "", $"{WindowsShell.Address(d)} | {WindowsShell.Text(WindowsShell.NameBox(d))}");
    Press(d, accept: false);
    var cancelled = await Result(owned);
    Check("a cancelled open dialog reports Canceled and no paths", cancelled is { Canceled: true, FilePaths.Count: 0 }, Paths(cancelled));
    Check("the owner is enabled again after the dialog", Eventually(() => WindowsShell.IsEnabled(winHwnd), step));

    // Each dialog has a dedicated STA thread of its own, as Electron's dialog_thread: two open at once run on two threads.
    var first = Dialog.ShowOpenDialogAsync(new OpenDialogOptions { Title = "Oriel pair one", DefaultPath = dir });
    var second = Dialog.ShowOpenDialogAsync(new OpenDialogOptions { Title = "Oriel pair two", DefaultPath = dir });
    var one = Find("Oriel pair one");
    var two = Find("Oriel pair two");
    Check("two dialogs open at once run on two threads", one != 0 && two != 0 && WindowsShell.ThreadOf(one) != WindowsShell.ThreadOf(two),
        $"{WindowsShell.ThreadOf(one)} {WindowsShell.ThreadOf(two)}");
    Press(one, accept: false);
    Press(two, accept: false);
    Check("both cancel", Task.WaitAll([first, second], step));
    // multiSelections: several quoted names in the name box.
    var multi = Dialog.ShowOpenDialogAsync(win, new OpenDialogOptions { Title = "Oriel multi open", DefaultPath = dir, Properties = OpenDialogProperties.OpenFile | OpenDialogProperties.MultiSelections });
    d = Find("Oriel multi open");
    Type(d, "\"a.txt\" \"b.dat\"");
    Press(d, accept: true);
    var multiResult = await Result(multi);
    Check("MultiSelections returns several files", multiResult is { Canceled: false, FilePaths.Count: 2 }
        && multiResult.FilePaths.Any(f => Same(f, Path.Combine(dir, "a.txt"))) && multiResult.FilePaths.Any(f => Same(f, Path.Combine(dir, "b.dat"))), Paths(multiResult));

    // openDirectory + multiSelections: FOS_PICKFOLDERS | FOS_ALLOWMULTISELECT.
    // A folder picker ignores quoted names typed in its box, so the folders are selected in the view, as Ctrl+clicks would.
    var folders = Dialog.ShowOpenDialogAsync(win, new OpenDialogOptions { Title = "Oriel folders open", DefaultPath = dir, Properties = OpenDialogProperties.OpenDirectory | OpenDialogProperties.MultiSelections });
    d = Find("Oriel folders open");
    var picked = await Task.Run(() => WindowsShell.SelectItems(d, ["sub1", "sub2"]));
    Thread.Sleep(500);
    Press(d, accept: true);
    var foldersResult = await Result(folders);
    Check("OpenDirectory with MultiSelections returns several directories", foldersResult is { Canceled: false, FilePaths.Count: 2 }
        && foldersResult.FilePaths.Any(f => Same(f, Path.Combine(dir, "sub1"))) && foldersResult.FilePaths.Any(f => Same(f, Path.Combine(dir, "sub2"))), $"selected {picked}; {Paths(foldersResult)}");

    // One folder: accepting with an empty name box chooses the folder shown.
    var folder = Dialog.ShowOpenDialogAsync(win, new OpenDialogOptions { Title = "Oriel folder open", DefaultPath = dir, Properties = OpenDialogProperties.OpenDirectory });
    d = Find("Oriel folder open");
    Press(d, accept: true);
    var folderResult = await Result(folder);
    Check("OpenDirectory returns the directory it started in", folderResult is { Canceled: false, FilePaths: [var only] } && Same(only, dir), Paths(folderResult));

    // showHiddenFiles: a hidden file is listed only with it, which shows only while Explorer hides hidden files
    // (FOS_FORCESHOWHIDDEN does not reveal protected system files either).
    List<string> NamesIn(OpenDialogOptions options)
    {
        var t = Dialog.ShowOpenDialogAsync(win, options);
        var h = Find(options.Title!);
        var names = WindowsShell.ElementNames(h);
        Press(h, accept: false);
        t.Wait(step);
        return names;
    }
    var plain = NamesIn(new OpenDialogOptions { Title = "Oriel plain list", DefaultPath = dir });
    var hidden = NamesIn(new OpenDialogOptions { Title = "Oriel hidden list", DefaultPath = dir, Properties = OpenDialogProperties.ShowHiddenFiles });
    Check("the item view lists the folder's files", Listed(plain, "a.txt") && Listed(hidden, "a.txt"));
    var explorerHides = OperatingSystem.IsWindows() && Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2) is 2;
    if (explorerHides)
    {
        Check("without ShowHiddenFiles a hidden file is not listed", !Listed(plain, "secret.txt"));
        Check("ShowHiddenFiles lists a hidden file", Listed(hidden, "secret.txt"));
    }
    else Console.WriteLine($"SKIP  ShowHiddenFiles lists a hidden file  [Explorer shows hidden files on this machine, so the flag has no visible effect; listed without it: {Listed(plain, "secret.txt")}]");

    // Filters: '*' is "*.*", so every file is listed; an extension filter narrows the list; the combo shows the name.
    var anyTask = Dialog.ShowOpenDialogAsync(win, new OpenDialogOptions { Title = "Oriel any filter", DefaultPath = dir, Filters = [new("Everything", "*")] });
    d = Find("Oriel any filter");
    var anyType = WindowsShell.FileType(d);
    var any = WindowsShell.ElementNames(d);
    Press(d, accept: false);
    anyTask.Wait(step);
    var textOnly = NamesIn(new OpenDialogOptions { Title = "Oriel text filter", DefaultPath = dir, Filters = [new("Text", "txt")] });
    Check("a '*' filter is offered as *.*", anyType == "Everything (*.*)", anyType);
    Check("a '*' filter lists every file", Listed(any, "a.txt") && Listed(any, "b.dat"));
    Check("an extension filter lists only its files", Listed(textOnly, "a.txt") && !Listed(textOnly, "b.dat"));

    // Save: a typed name gets the first filter's extension (SetDefaultExtension).
    var save = Dialog.ShowSaveDialogAsync(win, new SaveDialogOptions { Title = "Oriel save", DefaultPath = dir, Filters = [new("Text", "txt")] });
    d = Find("Oriel save");
    Type(d, "named");
    Press(d, accept: true);
    var saved = await Result(save);
    Check("save dialog accepts a typed name and adds the filter's extension", saved is { Canceled: false } && Same(saved.FilePath, Path.Combine(dir, "named.txt")), saved);

    // Save with defaultPath naming a new file: the name box shows it and accepting returns it.
    var target = Path.Combine(dir, "saved.txt");
    save = Dialog.ShowSaveDialogAsync(win, new SaveDialogOptions { Title = "Oriel save path", DefaultPath = target });
    d = Find("Oriel save path");
    Check("save defaultPath: the name box shows the file name", WindowsShell.Text(WindowsShell.NameBox(d)) == "saved.txt", WindowsShell.Text(WindowsShell.NameBox(d)));
    Press(d, accept: true);
    saved = await Result(save);
    Check("ShowSaveDialogAsync returns the chosen path", saved is { Canceled: false } && Same(saved.FilePath, target), saved);

    // Sync on the UI thread: the dialog runs on its own thread while the UI thread dispatches the owner's messages.
    string? syncPath = null;
    using (var syncDone = new ManualResetEventSlim())
    {
        Oriel.Cef.CefThread.PostUi(() =>
        {
            try { syncPath = Dialog.ShowSaveDialogSync(win, new SaveDialogOptions { Title = "Oriel sync save", DefaultPath = Path.Combine(dir, "sync.txt") }); }
            catch (Exception e) { syncPath = e.GetType().Name; }
            finally { syncDone.Set(); }
        });
        d = Find("Oriel sync save");
        Check("ShowSaveDialogSync on the UI thread shows a dialog owned by the window", d != 0 && WindowsShell.Owner(d) == winHwnd, WindowsShell.Owner(d));
        Check("the owner is disabled while the Sync dialog is up", d != 0 && !WindowsShell.IsEnabled(winHwnd));
        Press(d, accept: true);
        Check("ShowSaveDialogSync on the UI thread returns the path", syncDone.Wait(step) && Same(syncPath, Path.Combine(dir, "sync.txt")), syncPath);
    }

    // Sync off the UI thread, window-less, cancelled: null.
    var worker = Task.Run(() => Dialog.ShowOpenDialogSync(null, new OpenDialogOptions { Title = "Oriel sync worker", DefaultPath = dir }));
    d = Find("Oriel sync worker");
    Press(d, accept: false);
    Check("ShowOpenDialogSync off the UI thread returns null when cancelled", await Task.WhenAny(worker, Task.Delay(step)) == worker && worker.Result is null);

    // Nothing left open.
    foreach (var title in new[] { "Oriel windowless open", "Oriel pair one", "Oriel pair two", "Oriel owned open", "Oriel multi open", "Oriel folders open", "Oriel folder open", "Oriel plain list", "Oriel hidden list", "Oriel any filter", "Oriel text filter", "Oriel save", "Oriel save path", "Oriel sync save", "Oriel sync worker" })
        if (WindowsShell.FileDialog(title) is var left and not 0) Press(left, accept: false);
    File.SetAttributes(secret, FileAttributes.Normal);
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
    var save = new MenuItem { Id = "save", Label = "&Save", Accelerator = "CmdOrCtrl+Shift+K", Click = (_, w, keys) => { saveKeys = keys; clicks.Add($"save:{w == win}"); } };
    var check = new MenuItem { Type = MenuItemType.Checkbox, Label = "Check" };
    var radioA = new MenuItem { Type = MenuItemType.Radio, Label = "A", Checked = true };
    var radioB = new MenuItem { Type = MenuItemType.Radio, Label = "B" };
    var go = new MenuItem { Label = "&Go", Click = (_, w, _) => clicks.Add($"go:{w == win}") };
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
    Check("Click's event says the accelerator chose it, with its keys, as Electron's KeyboardEvent",
        saveKeys is { TriggeredByAccelerator: true, ShiftKey: true, CtrlKey: true, AltKey: false }, saveKeys);

    save.Enabled = false;
    clicks.Clear();
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    await Task.Delay(500);
    Check("a disabled item's accelerator does nothing", clicks.Count == 0, string.Join(",", clicks));
    save.Enabled = true;
    win.WebContents.SetIgnoreMenuShortcuts(true);
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    await Task.Delay(500);
    var ignoredShortcut = clicks.Count == 0;
    win.WebContents.SetIgnoreMenuShortcuts(false);
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "K") { Modifiers = InputModifiers.Control | InputModifiers.Shift });
    Check("SetIgnoreMenuShortcuts keeps the page's keys from the window's accelerators until cleared",
        ignoredShortcut && Eventually(() => clicks.Contains("save:True"), step), string.Join(",", clicks));
    clicks.Clear();

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
    win.WebContents.SetIgnoreMenuShortcuts(true);
    PressAlt();
    await Task.Delay(500);
    var altIgnored = !win.IsMenuBarVisible;
    win.WebContents.SetIgnoreMenuShortcuts(false);
    Check("SetIgnoreMenuShortcuts keeps Alt from the auto-hidden bar, as Electron skips the window's key handling", altIgnored);
    PressAlt();
    Eventually(() => win.IsMenuBarVisible, step);
    Key("A", KeyboardInputEventType.KeyDown);
    Key("A", KeyboardInputEventType.KeyUp);
    Check("typing into the page hides an Alt-shown bar", Eventually(() => !win.IsMenuBarVisible, step));
    PressAlt();
    Eventually(() => win.IsMenuBarVisible, step);
    win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, 40, 40));
    win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseUp, 40, 40));
    Check("a click into the page hides an Alt-shown bar, as Electron", Eventually(() => !win.IsMenuBarVisible, step));
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
    // The active index is Chromium's current entry, the pending one included: an offset taken before the move back
    // commits counts from the page still showing, as in Electron.
    Eventually(() => !noScript.WebContents.IsLoading(), step);
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
    // progress has no check here. The overlay's description comes back as the button's UI Automation help text.
    string? OverlayHelp() => WindowsShell.TaskbarButton(app + " - ")?.Help;
    tb.SetOverlayIcon(NativeImage.CreateFromPath(OverlayPng()), "Oriel overlay test");
    var overlaid = Eventually(() => OverlayHelp() == "Oriel overlay test", step);
    tb.SetOverlayIcon(null, "");
    Check("SetOverlayIcon shows its description on the taskbar button; null removes it (UI Automation help text)", overlaid && Eventually(() => OverlayHelp() == "", step), $"{overlaid} [{OverlayHelp()}]");

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

// app.setBadgeCount on Windows: the shell exposes the overlay's description as the taskbar button's UI Automation
// help text, and the badge itself shows up as screen pixels over the button.
async Task BadgeAsync(BrowserWindow win)
{
    if (!OperatingSystem.IsWindows()) return;
    var prefix = System.Diagnostics.Process.GetCurrentProcess().ProcessName + " - ";
    string? Help() => WindowsShell.TaskbarButton(prefix)?.Help;
    win.Show();
    await Task.Delay(1000);
    if (WindowsShell.TaskbarButton(prefix) is not { } button)
    {
        Check("the taskbar button is found for the badge checks", false, prefix);
        return;
    }
    var before = WindowsShell.ScreenPixels(button.Bounds);
    (int Changed, int Dark, int Bright) Diff((byte R, byte G, byte B)[] now)
    {
        int changed = 0, dark = 0, bright = 0;
        for (var i = 0; i < Math.Min(now.Length, before.Length); i++)
        {
            var (a, b) = (before[i], now[i]);
            if (Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))) <= 60) continue;
            changed++;
            if (Math.Max(b.R, Math.Max(b.G, b.B)) < 90) dark++;
            if (Math.Min(b.R, Math.Min(b.G, b.B)) > 200) bright++;
        }
        return (changed, dark, bright);
    }

    var set = app.SetBadgeCount(5);
    Check("SetBadgeCount(5) returns true and GetBadgeCount reads 5", set && app.GetBadgeCount() == 5, $"{set} {app.GetBadgeCount()}");
    Check("SetBadgeCount gives the taskbar button Chromium's description (UI Automation help text)", Eventually(() => Help() == "5 Unread Notifications", step), Help());
    await Task.Delay(500);
    var drawn = Diff(WindowsShell.ScreenPixels(button.Bounds));
    Check("the badge is drawn over the taskbar button: dark circle, white text (screen pixels)", drawn is { Dark: >= 20, Bright: >= 2 }, $"{drawn} {button.Bounds}");
    set = app.SetBadgeCount(150);
    Check("past 99 the description says more than 99 and GetBadgeCount keeps the count", set && Eventually(() => Help() == "More than 99 unread notifications", step) && app.GetBadgeCount() == 150, $"{Help()} {app.GetBadgeCount()}");
    set = app.SetBadgeCount();
    Check("SetBadgeCount() shows the unspecified badge and GetBadgeCount reads 0", set && Eventually(() => Help() == "Unread Notifications", step) && app.GetBadgeCount() == 0, $"{Help()} {app.GetBadgeCount()}");
    app.SetBadgeCount(7);
    set = app.SetBadgeCount(0);
    // Electron passes the description with the null icon, and the shell keeps it as the help text.
    var cleared = Eventually(() => Help() == "0 Unread Notifications", step);
    await Task.Delay(500);
    var after = Diff(WindowsShell.ScreenPixels(button.Bounds));
    Check("SetBadgeCount(0) returns true and removes the overlay's pixels, leaving Electron's '0 Unread Notifications'", set && cleared && app.GetBadgeCount() == 0 && after.Changed <= 4, $"{set} [{Help()}] {app.GetBadgeCount()} {after}");

    // Electron badges the windows open at the call and no later ones; the button shows the overlay of any window that has one.
    var later = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: badge, opened later", Width = 300, Height = 200 });
    await Task.Delay(1500);
    bool KeptWithout(BrowserWindow dropped)
    {
        app.SetBadgeCount(4);
        if (!Eventually(() => Help() == "4 Unread Notifications", step)) return false;
        dropped.SetOverlayIcon(null, "");
        return Stays(() => Help() == "4 Unread Notifications");
    }
    var keptWithoutFirst = KeptWithout(win);
    var keptWithoutSecond = KeptWithout(later);
    Check("SetBadgeCount badges every open window: the button keeps it when either window drops its overlay", keptWithoutFirst && keptWithoutSecond, $"{keptWithoutFirst} {keptWithoutSecond} [{Help()}]");
    app.SetBadgeCount(3);
    var newest = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: badge, opened after the call", Width = 300, Height = 200 });
    await Task.Delay(1500);
    var shown = Eventually(() => Help() == "3 Unread Notifications", step);
    win.SetOverlayIcon(null, "");
    later.SetOverlayIcon(null, "");
    Check("a window opened after SetBadgeCount gets no badge", shown && Eventually(() => Help() == "", step), Help());
    app.SetBadgeCount(0);
    newest.Destroy();
    later.Destroy();
    await Task.Delay(300);

    bool Stays(Func<bool> condition)
    {
        for (var i = 0; i < 10; i++)
        {
            if (!condition()) return false;
            Thread.Sleep(150);
        }
        return true;
    }
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

async Task TransparencyAsync()
{
    if (!OperatingSystem.IsWindows()) return;

    // A red window underneath; what the test windows show of it is read off the composited screen. The display is
    // colour-managed, so colours are told apart by their dominant channel.
    var back = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: red backdrop", X = 60, Y = 60, Width = 800, Height = 600, AutoHideMenuBar = true, BackgroundColor = "#FF0000" });
    await back.LoadUrl("data:text/html,<body style='background:red'></body>");
    const string clear = "data:text/html,<html style='background:transparent'><body style='background:transparent'></body></html>";
    static bool Red((byte R, byte G, byte B) c) => c.R > 150 && c.G < 90 && c.B < 90;
    static bool White((byte R, byte G, byte B) c) => c.R > 200 && c.G > 200 && c.B > 200;
    async Task<(BrowserWindow W, nint Hwnd)> Open(BrowserWindowOptions options)
    {
        var w = new BrowserWindow(options with { X = 200, Y = 150, Width = 400, Height = 300, AutoHideMenuBar = true });
        await w.LoadUrl(clear);
        await Task.Delay(1000);
        return (w, w.GetNativeWindowHandle());
    }
    async Task Close(BrowserWindow w)
    {
        w.Destroy();
        await Task.Delay(300);
    }

    var (plain, plainHwnd) = await Open(new BrowserWindowOptions { Title = "Oriel window test: opaque" });
    var opaque = WindowsShell.ClientCentrePixel(plainHwnd);
    Check("an ordinary window covers the one behind it (control)", White(opaque), opaque);
    await Close(plain);

    var (framed, framedHwnd) = await Open(new BrowserWindowOptions { Title = "Oriel window test: transparent", Transparent = true });
    Check("Transparent shows the window behind through a transparent page (screen pixel)", Eventually(() => Red(WindowsShell.ClientCentrePixel(framedHwnd)), step), WindowsShell.ClientCentrePixel(framedHwnd));
    Check("a transparent window has no caption or sizing border (GWL_STYLE)", (WindowsShell.Style(framedHwnd) & 0x00C40000) == 0, $"{WindowsShell.Style(framedHwnd):X8}");
    Check("a transparent window has no backdrop to cover the see-through area (DWM read-back)", WindowsShell.Backdrop(framedHwnd) == WindowsBackdrop.None, WindowsShell.Backdrop(framedHwnd));
    Check("a framed transparent window is neither resizable nor maximizable, as in Electron", !framed.IsResizable && !framed.IsMaximizable, $"{framed.IsResizable} {framed.IsMaximizable}");
    WindowsShell.SetStyle(framedHwnd, WindowsShell.Style(framedHwnd) | 0x00C40000);
    Check("a later style change cannot put the caption back, as Chromium tries to", (WindowsShell.Style(framedHwnd) & 0x00C40000) == 0, $"{WindowsShell.Style(framedHwnd):X8}");
    var page = Path.Combine(Path.GetTempPath(), "oriel-transparent.html");
    File.WriteAllText(page, "<html style='background:transparent'><body style='background:transparent'>file</body></html>");
    await framed.LoadUrl(new Uri(page).AbsoluteUri);
    Check("the page stays transparent after navigating to another origin", Eventually(() => Red(WindowsShell.ClientCentrePixel(framedHwnd)), step), WindowsShell.ClientCentrePixel(framedHwnd));
    framed.SetBackgroundColor("#FFFFFF");
    Check("SetBackgroundColor makes a transparent window's page opaque again", Eventually(() => White(WindowsShell.ClientCentrePixel(framedHwnd)), step), WindowsShell.ClientCentrePixel(framedHwnd));
    await Close(framed);
    File.Delete(page);

    var (whitened, whitenedHwnd) = await Open(new BrowserWindowOptions { Title = "Oriel window test: transparent white", Transparent = true, BackgroundColor = "#FFFFFF" });
    var opaqueColour = WindowsShell.ClientCentrePixel(whitenedHwnd);
    whitened.SetBackgroundColor("#00000000");
    Check("an opaque BackgroundColor covers a transparent window until SetBackgroundColor clears the page", White(opaqueColour) && Eventually(() => Red(WindowsShell.ClientCentrePixel(whitenedHwnd)), step),
        $"{opaqueColour} -> {WindowsShell.ClientCentrePixel(whitenedHwnd)}");
    await Close(whitened);

    var (half, halfHwnd) = await Open(new BrowserWindowOptions { Title = "Oriel window test: half black", Transparent = true, Frame = false, BackgroundColor = "#80000000" });
    // Half-black once over the red is a quarter of it after the window and the page each paint it.
    bool Quarter() => WindowsShell.ClientCentrePixel(halfHwnd) is var c && c.R is > 40 and < 95 && c.G < 30 && c.B < 30;
    Check("a translucent BackgroundColor is painted by the window and the page, the browser view adding none", Eventually(Quarter, step), WindowsShell.ClientCentrePixel(halfHwnd));
    await Close(half);

    var (ordinary, ordinaryHwnd) = await Open(new BrowserWindowOptions { Title = "Oriel window test: frameless", Frame = false });
    Check("an ordinary frameless window has a resize border at its edge (control, WM_NCHITTEST)", WindowsShell.LeftEdgeHitTest(ordinaryHwnd) == 10, WindowsShell.LeftEdgeHitTest(ordinaryHwnd));
    await Close(ordinary);

    var (frameless, framelessHwnd) = await Open(new BrowserWindowOptions { Title = "Oriel window test: transparent frameless", Transparent = true, Frame = false });
    Check("a frameless transparent window shows the window behind (screen pixel)", Eventually(() => Red(WindowsShell.ClientCentrePixel(framelessHwnd)), step), WindowsShell.ClientCentrePixel(framelessHwnd));
    Check("a frameless transparent window reports Resizable as set, and not maximizable, as in Electron", frameless.IsResizable && !frameless.IsMaximizable, $"{frameless.IsResizable} {frameless.IsMaximizable}");
    Check("the user cannot resize a transparent window: no sizing code at its edge (WM_NCHITTEST)", WindowsShell.LeftEdgeHitTest(framelessHwnd) is not (>= 10 and <= 17), WindowsShell.LeftEdgeHitTest(framelessHwnd));
    var before = frameless.GetBounds();
    var workArea = Screen.GetDisplayMatching(before).WorkArea;
    frameless.Maximize();
    Check("Maximize on a transparent window fills the work area and reports maximized", Eventually(() => frameless.GetBounds() == workArea && frameless.IsMaximized, step), $"{frameless.GetBounds()} vs {workArea}");
    frameless.Unmaximize();
    Check("Unmaximize puts a transparent window back", Eventually(() => frameless.GetBounds() == before && !frameless.IsMaximized, step), frameless.GetBounds());
    await Close(frameless);

    var (acrylic, acrylicHwnd) = await Open(new BrowserWindowOptions { Title = "Oriel window test: acrylic", BackgroundMaterial = BackgroundMaterial.Acrylic });
    Check("BackgroundMaterial Acrylic shows the window behind, blurred, through the page (screen pixel)", Eventually(() => Red(WindowsShell.ClientCentrePixel(acrylicHwnd)), step), WindowsShell.ClientCentrePixel(acrylicHwnd));
    Check("a material makes the background colour transparent, as in Electron", acrylic.GetBackgroundColor() == "#00000000", acrylic.GetBackgroundColor());
    acrylic.SetBackgroundMaterial(BackgroundMaterial.None);
    Check("SetBackgroundMaterial(None) makes the window opaque white again", Eventually(() => White(WindowsShell.ClientCentrePixel(acrylicHwnd)), step) && acrylic.GetBackgroundColor() == "#FFFFFFFF", $"{WindowsShell.ClientCentrePixel(acrylicHwnd)} {acrylic.GetBackgroundColor()}");
    await Close(acrylic);

    var (mica, micaHwnd) = await Open(new BrowserWindowOptions { Title = "Oriel window test: mica", Frame = false });
    var micaWhite = WindowsShell.ClientCentrePixel(micaHwnd);
    mica.SetBackgroundMaterial(BackgroundMaterial.Mica);
    bool ShowsMica() => WindowsShell.ClientCentrePixel(micaHwnd) is var c && !White(c) && !Red(c);
    Check("SetBackgroundMaterial(Mica) on a frameless window shows Mica, neither the page's white nor the window behind", Eventually(ShowsMica, step) && White(micaWhite),
        $"{micaWhite} -> {WindowsShell.ClientCentrePixel(micaHwnd)}, backdrop {WindowsShell.Backdrop(micaHwnd)}");
    mica.SetBackgroundMaterial(BackgroundMaterial.Acrylic);
    Check("SetBackgroundMaterial(Acrylic) on a frameless window shows the window behind (screen pixel)", Eventually(() => Red(WindowsShell.ClientCentrePixel(micaHwnd)), step), WindowsShell.ClientCentrePixel(micaHwnd));
    await Close(mica);
    await Close(back);
}

async Task ShellAsync()
{
    if (!OperatingSystem.IsWindows())
    {
        Check("Shell throws PlatformNotSupportedException off Windows", Throws<PlatformNotSupportedException>(Shell.Beep));
        return;
    }

    var dir = Path.Combine(Path.GetTempPath(), "oriel-shell-" + Path.GetRandomFileName());
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, "shown.txt");
    File.WriteAllText(file, "oriel");

    // Shortcuts: every field round-trips through the .lnk; update changes only what it is given.
    // Wrapped so a mutant that throws fails its check instead of stopping the run.
    ShortcutDetails? ReadLink(string path) { try { return Shell.ReadShortcutLink(path); } catch (Exception) { return null; } }
    bool WriteLink(string path, ShortcutLinkOperation operation, ShortcutDetails options) { try { return Shell.WriteShortcutLink(path, operation, options); } catch (Exception) { return false; } }
    var link = Path.Combine(dir, "test.lnk");
    var toast = Guid.NewGuid();
    var details = new ShortcutDetails
    {
        Target = Environment.ProcessPath, Cwd = dir, Args = "--from-shortcut \"a b\"", Description = "Oriel shortcut test",
        Icon = Environment.ProcessPath, IconIndex = 0, AppUserModelId = "Riddlemd.Oriel.WindowTest.Shortcut", ToastActivatorClsid = toast,
    };
    Check("WriteShortcutLink creates a shortcut", WriteLink(link, ShortcutLinkOperation.Create, details) && File.Exists(link));
    var read = ReadLink(link);
    Check("ReadShortcutLink reads every field back", read == details, read);
    var wsh = WindowsShell.ShortcutFields(link);
    Check("WriteShortcutLink's fields read back through WScript.Shell",
        wsh.Target.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && wsh.Arguments == details.Args && wsh.WorkingDirectory.Equals(dir, StringComparison.OrdinalIgnoreCase)
        && wsh.Description == details.Description && wsh.IconLocation.Equals(Environment.ProcessPath + ",0", StringComparison.OrdinalIgnoreCase), wsh);
    Check("WriteShortcutLink(Update) changes only the given fields",
        WriteLink(link, ShortcutLinkOperation.Update, new ShortcutDetails { Description = "Updated" }) && ReadLink(link) == details with { Description = "Updated" },
        ReadLink(link));
    Check("WriteShortcutLink(Replace) keeps the old arguments when none are given (WScript.Shell read-back)",
        WriteLink(link, ShortcutLinkOperation.Replace, new ShortcutDetails { Target = Environment.ProcessPath, Description = "Replaced" })
        && WindowsShell.ShortcutFields(link) is { Arguments: "--from-shortcut \"a b\"", Description: "Replaced", WorkingDirectory: "" }, WindowsShell.ShortcutFields(link));
    Check("WriteShortcutLink(Replace) needs an existing shortcut", !WriteLink(Path.Combine(dir, "missing.lnk"), ShortcutLinkOperation.Replace, details) && !File.Exists(Path.Combine(dir, "missing.lnk")));
    Check("WriteShortcutLink(Update) of a file that is not a shortcut fails and leaves it alone",
        !WriteLink(file, ShortcutLinkOperation.Update, new ShortcutDetails { Description = "x" }) && File.ReadAllText(file) == "oriel");
    var nested = Path.Combine(dir, "new", "deeper", "nested.lnk");
    Check("WriteShortcutLink creates the shortcut's folder", WriteLink(nested, ShortcutLinkOperation.Create, new ShortcutDetails { Target = Environment.ProcessPath }) && File.Exists(nested));
    var iconOnly = Path.Combine(dir, "icon-only.lnk");
    Check("WriteShortcutLink sets no icon without an icon index (WScript.Shell read-back)",
        WriteLink(iconOnly, ShortcutLinkOperation.Create, new ShortcutDetails { Target = Environment.ProcessPath, Icon = @"C:\Windows\System32\shell32.dll" })
        && !WindowsShell.ShortcutFields(iconOnly).IconLocation.Contains("shell32", StringComparison.OrdinalIgnoreCase), WindowsShell.ShortcutFields(iconOnly));
    var bare = ReadLink(nested);
    Check("ReadShortcutLink reads unset fields as empty, as Electron does",
        bare is { Args: "", Cwd: "", Description: "", Icon: "", IconIndex: 0, AppUserModelId: "" } && bare?.ToastActivatorClsid == Guid.Empty && WindowsShell.ShortcutFields(nested) is { Arguments: "", Description: "" }, bare);
    Exception? readError = null;
    try { Shell.ReadShortcutLink(file); } catch (Exception e) { readError = e; }
    Check("ReadShortcutLink of a file that is not a shortcut throws Electron's error", readError?.Message == "Failed to read shortcut link", readError?.Message);

    // Explorer windows the checks open are found and closed through Shell.Application.
    Shell.ShowItemInFolder(file);
    Check("ShowItemInFolder opens the folder with the item selected", Eventually(() => WindowsShell.ExplorerWindows().Any(w => w.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase) && w.Selected.Contains("shown.txt")), TimeSpan.FromSeconds(10)),
        string.Join("; ", WindowsShell.ExplorerWindows().Select(w => $"{w.Folder} [{string.Join(",", w.Selected)}]")));
    WindowsShell.CloseExplorerAt(dir);
    Shell.ShowItemInFolder(file.Replace('\\', '/'));
    Check("ShowItemInFolder accepts forward slashes", Eventually(() => WindowsShell.ExplorerWindows().Any(w => w.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase) && w.Selected.Contains("shown.txt")), TimeSpan.FromSeconds(10)),
        string.Join("; ", WindowsShell.ExplorerWindows().Select(w => $"{w.Folder} [{string.Join(",", w.Selected)}]")));
    WindowsShell.CloseExplorerAt(dir);
    async Task<string> OpenPath(string path) { try { return await Shell.OpenPathAsync(path); } catch (Exception e) { return e.Message; } }
    Check("OpenPathAsync opens a folder in Explorer", await OpenPath(dir) == "" && Eventually(() => WindowsShell.ExplorerWindows().Any(w => w.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(10)));
    WindowsShell.CloseExplorerAt(dir);
    var reason = await OpenPath(Path.Combine(dir, "missing"));
    Check("OpenPathAsync of a missing path reports why", reason == "Failed to open path", reason);
    Exception? opened = null;
    try { await Shell.OpenExternalAsync(new Uri(dir).AbsoluteUri, new OpenExternalOptions { LogUsage = true }); } catch (Exception e) { opened = e; }
    Check("OpenExternalAsync opens a file: URL with its handler", opened is null && Eventually(() => WindowsShell.ExplorerWindows().Any(w => w.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(10)),
        opened?.Message ?? ShellRules.ExternalFile(new Uri(dir).AbsoluteUri));
    WindowsShell.CloseExplorerAt(dir);
    // Windows 11 answers a scheme with no handler with its own "Pick an app" dialog (OpenWith.exe) even under
    // SEE_MASK_FLAG_NO_UI, and SEE_MASK_NOASYNC holds the call until the dialog closes; Electron makes the same call.
    int[] OpenWith() => [.. System.Diagnostics.Process.GetProcessesByName("OpenWith").Select(p => p.Id)];
    var pickersBefore = OpenWith();
    var unknown = Shell.OpenExternalAsync("oriel-no-such-scheme:hello");
    var picker = Eventually(() => OpenWith().Except(pickersBefore).Any(), TimeSpan.FromSeconds(10));
    foreach (var id in OpenWith().Except(pickersBefore)) try { System.Diagnostics.Process.GetProcessById(id).Kill(); } catch (Exception) { }
    Exception? refused = null;
    try { if (await Task.WhenAny(unknown, Task.Delay(10000)) == unknown) await unknown; else refused = new TimeoutException("still waiting"); } catch (Exception e) { refused = e; }
    Check("OpenExternalAsync of a scheme with no handler hands it to the shell, which offers to pick an app", picker && refused is null, $"picker={picker} {refused?.Message}");
    refused = null;
    pickersBefore = OpenWith();
    try { await Shell.OpenExternalAsync("not a url"); } catch (Exception e) { refused = e; }
    var stray = OpenWith().Except(pickersBefore).ToArray();
    foreach (var id in stray) try { System.Diagnostics.Process.GetProcessById(id).Kill(); } catch (Exception) { }
    Check("OpenExternalAsync of a string that is not a URL passes the shell an empty one, which does nothing", refused is null && stray.Length == 0, refused?.Message ?? $"{stray.Length} pickers");

    // TrashItem: the file leaves its folder and is found in the Recycle Bin, then purged from there.
    var doomed = Path.Combine(dir, "trash-me.txt");
    File.WriteAllText(doomed, "bin");
    Exception? trashError = null;
    try { await Shell.TrashItemAsync(doomed); } catch (Exception e) { trashError = e; }
    var recycled = WindowsShell.RecycledCopyOf(doomed);
    Check("TrashItemAsync moves the file to the Recycle Bin", trashError is null && !File.Exists(doomed) && recycled is not null, trashError?.Message ?? recycled);
    if (recycled is not null) WindowsShell.Purge(recycled);
    trashError = null;
    try { await Shell.TrashItemAsync(Path.Combine(dir, "missing.txt")); } catch (Exception e) { trashError = e; }
    Check("TrashItemAsync of a missing path rejects with Electron's error", trashError?.Message == "Failed to parse path", trashError?.Message);

    // No read-back for the sound.
    Shell.Beep();
    Directory.Delete(dir, recursive: true);
}

async Task ClipboardAsync()
{
    if (OperatingSystem.IsMacOS())
    {
        await MacClipboardTest.RunAsync(Check);
        return;
    }
    if (!OperatingSystem.IsWindows())
    {
        Check("Clipboard throws PlatformNotSupportedException off Windows and macOS", Throws<PlatformNotSupportedException>(Clipboard.Clear));
        return;
    }

    // Windows PowerShell in another process reads and writes the clipboard independently of Oriel's code.
    var outFile = Path.Combine(Path.GetTempPath(), "oriel-clipboard-" + Path.GetRandomFileName());
    string Ps(string script)
    {
        var wrapped = "Add-Type -AssemblyName System.Windows.Forms, System.Drawing; $o = & { " + script + " }; [IO.File]::WriteAllText('" + outFile + "', [string]$o, [Text.Encoding]::UTF8)";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-STA", "-Command", wrapped }) psi.ArgumentList.Add(arg);
        System.Diagnostics.Process.Start(psi)!.WaitForExit();
        return File.Exists(outFile) ? File.ReadAllText(outFile).TrimStart('\uFEFF') : "";
    }
    string Formats() => Ps("[Windows.Forms.Clipboard]::GetDataObject().GetFormats() -join ','");
    // Wrapped so a mutant that throws fails its check instead of stopping the run.
    async Task<IReadOnlyList<string>> Types() { try { return (await Clipboard.ReadAsync())[0].Types; } catch (Exception e) { return ["<" + e.Message + ">"]; } }
    async Task<object> Get(string type) { try { return await (await Clipboard.ReadAsync())[0].GetTypeAsync(type); } catch (Exception e) { return "<" + e.Message + ">"; } }
    async Task<byte[]> GetBytes(string type) => await Get(type) as byte[] ?? [];
    async Task<string> GetText(string type) => await Get(type) is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : $"{await Get(type)}";
    async Task Put(Dictionary<string, object?> item) { try { await Clipboard.WriteAsync([new ClipboardItem(item)]); } catch (Exception e) { Console.WriteLine("clipboard write threw: " + e.Message); } }
    async Task<string> ReadText() { try { return await Clipboard.ReadTextAsync(); } catch (Exception e) { return "<" + e.Message + ">"; } }
    async Task WriteText(string text) { try { await Clipboard.WriteTextAsync(text); } catch (Exception e) { Console.WriteLine("clipboard write threw: " + e.Message); } }
    async Task<bool> Has(string type) { try { return await Clipboard.HasAsync(type); } catch (Exception) { return false; } }

    // The user's text is put back at the end; other formats on the clipboard are lost.
    var saved = await ReadText();

    await WriteText("Oriel héllo ✓");
    var owner = WindowsShell.ClipboardOwner();
    Check("Writes own the clipboard through a window of this process, as Chromium's do", owner == (Environment.ProcessId, "OrielClipboardOwner"), owner);
    Check("WriteTextAsync reaches other apps", Ps("Get-Clipboard -Raw") == "Oriel héllo ✓", Ps("Get-Clipboard -Raw"));
    Ps("Set-Clipboard -Value 'from PowerShell é'");
    Check("ReadTextAsync reads another app's text", await ReadText() == "from PowerShell é", await ReadText());

    await Put(new() { ["text/html"] = "<b>bold é</b>" });
    // WinForms decodes CF_HTML in the ANSI code page; undoing that gives the bytes, sliced at Oriel's own byte offsets.
    const string Undo = "$s = [Windows.Forms.Clipboard]::GetText('Html'); $b = [Text.Encoding]::Default.GetBytes($s); $t = [Text.Encoding]::ASCII.GetString($b); ";
    var fragment = Ps(Undo + "$f = [int]([regex]::Match($t, 'StartFragment:(\\d+)').Groups[1].Value); $e = [int]([regex]::Match($t, 'EndFragment:(\\d+)').Groups[1].Value); [Text.Encoding]::UTF8.GetString($b, $f, $e - $f)");
    Check("Write(text/html) puts UTF-8 CF_HTML whose offsets frame the fragment", fragment == "<b>bold é</b>", fragment);
    var document = Ps(Undo + "$h = [int]([regex]::Match($t, 'StartHTML:(\\d+)').Groups[1].Value); [Text.Encoding]::UTF8.GetString($b, $h, $b.Length - $h)").Replace("\r", "\\r").Replace("\n", "\\n");
    Check("Write(text/html) wraps the fragment as Chromium does", document == "<html>\\r\\n<body>\\r\\n<!--StartFragment--><b>bold é</b><!--EndFragment-->\\r\\n</body>\\r\\n</html>", document);
    // A MemoryStream makes WinForms write the bytes as given: real UTF-8, as CF_HTML requires.
    Ps("$h = \"Version:0.9`r`nStartHTML:-1`r`nEndHTML:-1`r`nStartFragment:-1`r`nEndFragment:-1`r`n<html><body><!--StartFragment--><i>italic ü</i><!--EndFragment--></body></html>\"; $d = New-Object Windows.Forms.DataObject; $d.SetData('HTML Format', (New-Object IO.MemoryStream (,[Text.Encoding]::UTF8.GetBytes($h)))); [Windows.Forms.Clipboard]::SetDataObject($d, $true)");
    Check("getType(text/html) reads another app's fragment", await GetText("text/html") == "<i>italic ü</i>", await GetText("text/html"));

    await Put(new() { ["text/rtf"] = @"{\rtf1\ansi Oriel {\b RTF}}" });
    Check("Write(text/rtf) reaches other apps", Ps("[Windows.Forms.Clipboard]::GetText('Rtf')").Contains(@"{\b RTF}"), Ps("[Windows.Forms.Clipboard]::GetText('Rtf')"));
    Ps(@"[Windows.Forms.Clipboard]::SetText('{\rtf1\ansi from {\i PS}}', 'Rtf')");
    Check("getType(text/rtf) reads another app's RTF", (await GetText("text/rtf")).Contains(@"from {\i PS}"), await GetText("text/rtf"));
    // "{\rtf1 é}" with é as the windows-1252 byte 0xE9, written as raw bytes.
    Ps("$d = New-Object Windows.Forms.DataObject; $d.SetData('Rich Text Format', (New-Object IO.MemoryStream (,[byte[]](0x7B,0x5C,0x72,0x74,0x66,0x31,0x20,0xE9,0x7D)))); [Windows.Forms.Clipboard]::SetDataObject($d, $true)");
    Check("getType(text/rtf) converts legacy-encoded RTF to UTF-8, as Chromium does", await GetText("text/rtf") == @"{\rtf1 é}", await GetText("text/rtf"));

    // Images: a 4x4 green square out as PNG bytes, a 3x2 red bitmap in, through System.Drawing.
    var green = new byte[4 * 4 * 4];
    for (var i = 0; i < green.Length; i += 4) (green[i], green[i + 1], green[i + 2], green[i + 3]) = (0, 200, 0, 255);
    await Put(new() { ["image/png"] = NativeImage.CreateFromBitmap(green, new NativeImageBitmapOptions { Width = 4, Height = 4 }).ToPng() });
    var seen = Ps("$i = [Windows.Forms.Clipboard]::GetImage(); $p = $i.GetPixel(1, 1); \"$($i.Width)x$($i.Height) $($p.R),$($p.G),$($p.B)\"");
    Check("Write(image/png) reaches other apps", seen == "4x4 0,200,0", seen);
    Check("read() lists an image as image/png only, with PNG on the clipboard for other apps",
        (await Types()).SequenceEqual(["image/png"]) && Formats().Split(',').Contains("PNG"), $"{string.Join(",", await Types())} | {Formats()}");
    Ps("$b = New-Object Drawing.Bitmap 3, 2; for ($x = 0; $x -lt 3; $x++) { for ($y = 0; $y -lt 2; $y++) { $b.SetPixel($x, $y, [Drawing.Color]::FromArgb(255, 210, 10, 20)) } }; [Windows.Forms.Clipboard]::SetImage($b)");
    var image = NativeImage.CreateFromBuffer(await GetBytes("image/png"));
    var px = image.ToBitmap();
    Check("getType(image/png) reads another app's bitmap", image.GetSize() == (3, 2) && px.Length == 24 && (px[0], px[1], px[2], px[3]) == (20, 10, 210, 255), $"{image.GetSize()} {string.Join(",", px.Take(4))}");

    // Bookmark: Chromium's Windows format holds the URL only.
    await Put(new() { ["electron application/bookmark"] = new ClipboardBookmark("Oriel", "https://example.com/oriel") });
    var url = Ps("$m = [Windows.Forms.Clipboard]::GetData('UniformResourceLocatorW'); [Text.Encoding]::Unicode.GetString($m.ToArray()).TrimEnd([char]0)");
    Check("Write(bookmark) writes the URL alone to UniformResourceLocatorW", url == "https://example.com/oriel", url);
    Check("read() lists the bookmark and its raw format", (await Types()).SequenceEqual(["electron application/osclipboard;format=\"UniformResourceLocatorW\"", "electron application/bookmark"]), string.Join(",", await Types()));
    // GURL resolves dot segments in a non-special URL and percent-encodes its opaque host; the stand-in keeps "a/../".
    await Put(new() { ["electron application/bookmark"] = new ClipboardBookmark("", "MyApp://Bücher.example/a/../b") });
    var canonical = Ps("$m = [Windows.Forms.Clipboard]::GetData('UniformResourceLocatorW'); [Text.Encoding]::Unicode.GetString($m.ToArray()).TrimEnd([char]0)");
    Check("Write(bookmark) stores GURL's canonical URL (CEF)", canonical == "myapp://B%C3%BCcher.example/b", canonical);
    await Put(new() { ["electron application/bookmark"] = new ClipboardBookmark("Oriel", "https://example.com/oriel") });
    Check("getType(bookmark) reads the URL with an empty title", await Get("electron application/bookmark") is ClipboardBookmark { Title: "", Url: "https://example.com/oriel" }, await Get("electron application/bookmark"));

    // One item with several types; has and the type list agree with another app.
    await Put(new() { ["text/plain"] = "plain part", ["text/html"] = "<u>html part</u>", ["text/rtf"] = @"{\rtf1 rtf part}" });
    Check("Write puts text, HTML and RTF together", await ReadText() == "plain part" && await GetText("text/html") == "<u>html part</u>" && (await GetText("text/rtf")).Contains("rtf part")
        && Ps("Get-Clipboard -Raw") == "plain part", $"{await ReadText()} | {await GetText("text/html")}");
    Check("read() lists them in Chromium's order", (await Types()).SequenceEqual(["text/plain", "text/html", "text/rtf"]), string.Join(",", await Types()));
    Check("has answers per type", await Has("text/html") && !await Has("image/png"));

    // Raw platform formats and W3C custom formats, as other apps see them.
    await Put(new() { ["electron application/osclipboard;format=\"Oriel/Test\""] = new byte[] { 1, 2, 3, 250 } });
    var raw = Ps("$m = [Windows.Forms.Clipboard]::GetData('Oriel/Test'); ($m.ToArray() | ForEach-Object { $_ }) -join ','");
    Check("Write(osclipboard) puts the bytes under that registered format", raw == "1,2,3,250", raw);
    Check("read() lists it back and getType reads the bytes", (await Types()).Contains("electron application/osclipboard;format=\"Oriel/Test\"")
        && (await GetBytes("electron application/osclipboard;format=\"Oriel/Test\"")).SequenceEqual(new byte[] { 1, 2, 3, 250 }), string.Join(",", await Types()));
    await Put(new() { ["web application/x-oriel"] = "payload" });
    var map = Ps("$m = [Windows.Forms.Clipboard]::GetData('Web Custom Format Map'); [Text.Encoding]::UTF8.GetString($m.ToArray())");
    var custom = Ps("$m = [Windows.Forms.Clipboard]::GetData('Web Custom Format0'); [Text.Encoding]::UTF8.GetString($m.ToArray())");
    Check("Write(web type) stores Web Custom Format0 and Chromium's map", map == "{\"application/x-oriel\":\"Web Custom Format0\"}" && custom == "payload", $"{map} | {custom}");
    var webTypes = await Types();
    Check("read() lists the web type first and getType reads it", webTypes.Count > 0 && webTypes[0] == "web application/x-oriel" && await GetText("web application/x-oriel") == "payload", string.Join(",", webTypes));

    // Files: text/uri-list is the copied-files format both ways.
    var shown = Path.Combine(Path.GetTempPath(), "oriel clipboard é.txt");
    File.WriteAllText(shown, "x");
    await Put(new() { ["text/uri-list"] = new Uri(shown).AbsoluteUri });
    var drop = Ps("[Windows.Forms.Clipboard]::GetFileDropList() -join '|'");
    Check("Write(text/uri-list) copies the file for other apps", drop.Equals(shown, StringComparison.OrdinalIgnoreCase), drop);
    Ps($"$c = New-Object Collections.Specialized.StringCollection; [void]$c.Add('{outFile}'); [Windows.Forms.Clipboard]::SetFileDropList($c)");
    var uris = (await Types()).Contains("text/uri-list") ? await GetText("text/uri-list") : "";
    Check("getType(text/uri-list) reads files another app copied", uris == "file:///" + outFile.Replace('\\', '/'), uris);
    File.Delete(shown);

    await WriteText("");
    Check("WriteTextAsync(\"\") empties the clipboard, as Chromium does", Formats() == "" && (await Types()).Count == 0, Formats());
    await WriteText("x");
    try { Clipboard.Clear(); } catch (Exception e) { Console.WriteLine("clipboard clear threw: " + e.Message); }
    Check("Clear empties it for every app", (await Types()).Count == 0 && Ps("[Windows.Forms.Clipboard]::GetDataObject().GetFormats().Count") == "0", Formats());

    if (saved.Length > 0) await WriteText(saved);
    File.Delete(outFile);
}

async Task ShellApisAsync()
{
    if (!OperatingSystem.IsWindows()) return;

    // hookWindowMessage: the callback runs inside the window procedure, so SendMessage returns after it.
    var w = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: hooks", Width = 320, Height = 220 });
    await Task.Delay(1000);
    var hwnd = w.GetNativeWindowHandle();
    const uint custom = 0x0400 + 0x77; // WM_USER + 0x77
    var seen = new List<(nint W, nint L, bool OnWindowThread)>();
    var hooked = w.HookWindowMessage(custom, (wp, lp) => seen.Add((wp, lp, WindowsShell.OnWindowThread(hwnd))));
    WindowsShell.Send(hwnd, custom, 0x1234, -5);
    Check("HookWindowMessage: the callback gets wParam and lParam, synchronously on the window's thread",
        hooked && seen is [(0x1234, -5, true)], string.Join(";", seen));
    Check("IsWindowMessageHooked after HookWindowMessage", w.IsWindowMessageHooked(custom) && !w.IsWindowMessageHooked(custom + 1));
    var replaced = new List<string>();
    w.HookWindowMessage(custom, (_, _) => replaced.Add("second"));
    seen.Clear();
    WindowsShell.Send(hwnd, custom, 1, 2);
    Check("a second HookWindowMessage replaces the callback", seen.Count == 0 && replaced is ["second"], $"{seen.Count} {string.Join(",", replaced)}");
    w.UnhookWindowMessage(custom);
    replaced.Clear();
    WindowsShell.Send(hwnd, custom, 1, 2);
    Check("UnhookWindowMessage stops the callback", replaced.Count == 0 && !w.IsWindowMessageHooked(custom), $"{replaced.Count} {w.IsWindowMessageHooked(custom)}");
    var all = new List<uint>();
    w.HookWindowMessage(custom, (_, _) => all.Add(custom));
    w.HookWindowMessage(custom + 1, (_, _) => all.Add(custom + 1));
    w.UnhookAllWindowMessages();
    WindowsShell.Send(hwnd, custom, 0, 0);
    WindowsShell.Send(hwnd, custom + 1, 0, 0);
    Check("UnhookAllWindowMessages stops every callback", all.Count == 0 && !w.IsWindowMessageHooked(custom) && !w.IsWindowMessageHooked(custom + 1), all.Count);

    // The message goes on to Chromium and DefWindowProc: WM_SETTEXT still retitles, WM_GETTEXT still answers.
    string? setTextSeen = null;
    w.HookWindowMessage(0x000C /* WM_SETTEXT */, (_, lp) => setTextSeen = WindowsShell.ReadString(lp));
    WindowsShell.SetText(hwnd, "hooked title");
    Check("a hooked WM_SETTEXT reaches the callback and still sets the title", setTextSeen == "hooked title" && WindowsShell.GetText(hwnd) == "hooked title",
        $"{setTextSeen} / {WindowsShell.GetText(hwnd)}");
    var getTexts = 0;
    w.HookWindowMessage(0x000D /* WM_GETTEXT */, (_, _) => getTexts++);
    var text = WindowsShell.GetText(hwnd);
    Check("a hooked WM_GETTEXT reaches the callback and still answers", getTexts >= 1 && text == "hooked title", $"{getTexts} {text}");
    var stderr = Console.Error;
    var logged = new StringWriter();
    Console.SetError(logged);
    w.HookWindowMessage(0x000C, (_, _) => throw new InvalidOperationException("hook threw"));
    WindowsShell.SetText(hwnd, "after a throw");
    Console.SetError(stderr);
    Check("a throwing hook does not stop the message or cross into the window procedure",
        WindowsShell.GetText(hwnd) == "after a throw" && logged.ToString().Contains("hook threw"), WindowsShell.GetText(hwnd));
    w.UnhookAllWindowMessages();
    w.Destroy();
    await Task.Delay(300);

    // thickFrame and roundedCorners: GWL_STYLE and DWMWA_WINDOW_CORNER_PREFERENCE read back.
    const uint caption = 0x00C00000, thick = 0x00040000, sysMenu = 0x00080000, minBox = 0x00020000, maxBox = 0x00010000;
    BrowserWindow Open(BrowserWindowOptions o) => new(o with { Width = 300, Height = 200, Title = "Oriel window test: frame" });
    var frameless = Open(new BrowserWindowOptions { Frame = false });
    var noThick = Open(new BrowserWindowOptions { Frame = false, ThickFrame = false });
    var square = Open(new BrowserWindowOptions { Frame = false, RoundedCorners = false });
    var fixedSize = Open(new BrowserWindowOptions { Frame = false, Resizable = false });
    var framedNoThick = Open(new BrowserWindowOptions { ThickFrame = false });
    await Task.Delay(1500);
    uint Style(BrowserWindow b) => WindowsShell.Style(b.GetNativeWindowHandle());
    uint? Corners(BrowserWindow b) => WindowsShell.Dwm(b.GetNativeWindowHandle(), 33);
    string Hex(uint? v) => v is { } x ? $"0x{x:X8}" : "null";
    var fs = Style(frameless);
    Check("frameless: Electron's style (caption, thick frame, min and max boxes, no system menu)",
        (fs & (caption | thick | minBox | maxBox)) == (caption | thick | minBox | maxBox) && (fs & sysMenu) == 0, Hex(fs));
    Check("thickFrame = false: a frameless window loses WS_THICKFRAME and WS_CAPTION", (Style(noThick) & (caption | thick)) == 0 && (Style(noThick) & minBox) != 0, Hex(Style(noThick)));
    Check("resizable = false: a frameless window has no thick frame and no maximize box", (Style(fixedSize) & (thick | maxBox)) == 0 && (Style(fixedSize) & caption) == caption, Hex(Style(fixedSize)));
    Check("thickFrame = false: a framed window loses WS_THICKFRAME only, and is not resizable",
        (Style(framedNoThick) & thick) == 0 && (Style(framedNoThick) & caption) == caption && !framedNoThick.IsResizable, $"{Hex(Style(framedNoThick))} {framedNoThick.IsResizable}");
    Check("roundedCorners: a frameless window asks DWM to round (DWMWCP_ROUND)", Corners(frameless) == 2, Hex(Corners(frameless)));
    Check("roundedCorners = false: DWMWCP_DONOTROUND", Corners(square) == 1, Hex(Corners(square)));
    Check("thickFrame = false squares the corners too", Corners(noThick) == 1, Hex(Corners(noThick)));
    noThick.SetResizable(false);
    noThick.SetResizable(true);
    noThick.Maximize();
    await Task.Delay(500);
    noThick.Unmaximize();
    await Task.Delay(500);
    Check("thickFrame = false stays off through SetResizable and maximize", (Style(noThick) & (caption | thick)) == 0, Hex(Style(noThick)));
    framedNoThick.SetResizable(true);
    Check("a framed window without a thick frame stays unresizable after SetResizable(true)", !framedNoThick.IsResizable && (Style(framedNoThick) & thick) == 0, Hex(Style(framedNoThick)));
    foreach (var b in new[] { frameless, noThick, square, fixedSize, framedNoThick }) b.Destroy();
    await Task.Delay(300);

    // Full screen squares a framed window's corners and rounds them again after, as Electron's SetFullScreen.
    var framed = Open(new BrowserWindowOptions());
    await Task.Delay(1000);
    framed.SetFullScreen(true);
    var fullCorners = Eventually(() => Corners(framed) == 1, step);
    framed.SetFullScreen(false);
    Check("full screen sets DWMWCP_DONOTROUND, leaving it DWMWCP_ROUND", fullCorners && Eventually(() => Corners(framed) == 2, step), Hex(Corners(framed)));
    framed.Destroy();
    await Task.Delay(300);

    // accentColor: DWM takes DWMWA_BORDER_COLOR and DWMWA_CAPTION_COLOR but will not read them back, so the frame's
    // pixels are the read-back: the caption's middle and the 1px border along the bottom. The display is colour-managed.
    static bool Near((byte R, byte G, byte B) c, uint argb) => Math.Abs(c.R - (int)((argb >> 16) & 0xFF)) < 40 && Math.Abs(c.G - (int)((argb >> 8) & 0xFF)) < 40 && Math.Abs(c.B - (int)(argb & 0xFF)) < 40;
    static string Px((byte R, byte G, byte B) c) => $"{c.R},{c.G},{c.B}";
    (byte, byte, byte) CaptionPixel(BrowserWindow b) => WindowsShell.FramePixel(b.GetNativeWindowHandle(), caption: true);
    (byte, byte, byte) BorderPixel(BrowserWindow b) => WindowsShell.FramePixel(b.GetNativeWindowHandle(), caption: false);
    var system = WindowsWindow.SystemAccentColor() ?? 0;
    var dwmLog = new StringWriter();
    Console.SetError(dwmLog);
    BrowserWindow Accent(AccentColor? accent, bool frame = true, int x = 60) => new(new BrowserWindowOptions
    {
        Title = "accent", X = x, Y = 60, Width = 640, Height = 240, AlwaysOnTop = true, Frame = frame, AccentColor = accent,
    });
    var red = Accent("#FF0000");
    await Task.Delay(1200);
    red.Focus();
    Check("accentColor string: the caption and border take the colour (pixels)",
        Eventually(() => Near(CaptionPixel(red), 0xFF0000) && Near(BorderPixel(red), 0xFF0000), step), $"{Px(CaptionPixel(red))} / {Px(BorderPixel(red))}");
    Check("GetAccentColor returns the colour as #RRGGBB", red.GetAccentColor().Color == "#FF0000", red.GetAccentColor().Color);
    red.SetAccentColor("rgb(0, 255, 0)");
    Check("SetAccentColor(string) recolours caption and border", Eventually(() => Near(CaptionPixel(red), 0x00FF00) && Near(BorderPixel(red), 0x00FF00), step), $"{Px(CaptionPixel(red))} / {Px(BorderPixel(red))}");
    red.SetAccentColor("not a colour");
    await Task.Delay(400);
    Check("SetAccentColor with an unparsable string changes nothing", Near(CaptionPixel(red), 0x00FF00) && red.GetAccentColor().Color == "#00FF00", $"{Px(CaptionPixel(red))} {red.GetAccentColor().Color}");
    red.SetAccentColor(true);
    Check("SetAccentColor(true): the system accent colour", Eventually(() => Near(CaptionPixel(red), system) && Near(BorderPixel(red), system), step), $"{Px(CaptionPixel(red))} / {Px(BorderPixel(red))} vs {system:X6}");
    Check("GetAccentColor after true is the system colour", red.GetAccentColor().Color == $"#{system & 0xFFFFFF:X6}", red.GetAccentColor().Color);
    red.SetAccentColor(false);
    Check("SetAccentColor(false): DWM's default frame, and GetAccentColor is false",
        Eventually(() => !Near(CaptionPixel(red), system) && !Near(CaptionPixel(red), 0x00FF00), step) && red.GetAccentColor() == AccentColor.None, $"{Px(CaptionPixel(red))} {red.GetAccentColor()}");
    red.Destroy();
    var on = Accent(true, x: 80);
    var off = Accent(false, frame: false, x: 760);
    await Task.Delay(1200);
    on.Focus();
    await Task.Delay(500);
    off.Focus();
    await Task.Delay(500);
    Check("accentColor = true: the system accent colour, even inactive", Eventually(() => Near(CaptionPixel(on), system), step), $"{Px(CaptionPixel(on))} vs {system:X6}");
    Check("accentColor = false: GetAccentColor is false", off.GetAccentColor() == AccentColor.None, off.GetAccentColor());
    on.Destroy();
    off.Destroy();
    // Created hidden, the window gets its colour from the activation that showing it brings, as in Electron.
    var late = new BrowserWindow(new BrowserWindowOptions { Title = "accent", X = 60, Y = 60, Width = 640, Height = 240, AlwaysOnTop = true, Show = false, AccentColor = "#0000FF" });
    await Task.Delay(800);
    late.Show();
    late.Focus();
    Check("accentColor on a window created hidden applies when it is shown and activated", Eventually(() => Near(CaptionPixel(late), 0x0000FF), step), Px(CaptionPixel(late)));
    late.Destroy();
    await Task.Delay(300);
    Console.SetError(stderr);
    Check("DWM takes every accent colour (frameless: border only)", !dwmLog.ToString().Contains("accent colour"), dwmLog.ToString().Trim());
}

async Task TitleBarAsync(BrowserWindow win)
{
    if (!OperatingSystem.IsWindows()) return;

    // The display is colour-managed: test the dominant channel, not exact values.
    static string Rgb((byte R, byte G, byte B) c) => $"{c.R},{c.G},{c.B}";
    static bool IsBlue((byte R, byte G, byte B) c) => c.B > 120 && c.B > c.R + 50 && c.B > c.G + 30;
    static bool IsGreen((byte R, byte G, byte B) c) => c.G > 120 && c.G > c.R + 50 && c.G > c.B + 30;
    static bool IsRed((byte R, byte G, byte B) c) => c.R > 150 && c.R > c.G + 80 && c.R > c.B + 80;
    const int HtClient = 1, HtCaption = 2;
    const string Page = "<body style='margin:0;background:%23FFFF00'>"
        + "<div style='-webkit-app-region:drag;height:40px;background:%230000FF'></div>"
        + "<div style='-webkit-app-region:no-drag;position:absolute;top:5px;left:5px;width:30px;height:30px;background:%23FF0000'></div></body>";

    // app-region: CEF hit-tests the page's drag rectangles as caption, minus the no-drag ones, in a frameless window only.
    var frameless = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: drag regions", X = 200, Y = 200, Width = 400, Height = 250, Frame = false });
    await frameless.LoadUrl("data:text/html," + Page);
    var fh = frameless.GetNativeWindowHandle();
    int Hit((int X, int Y) p) => WindowsShell.HitTest(fh, p.X, p.Y);
    (int X, int Y) At(int dx, int dy) { var (x, y, _, _) = WindowsShell.Bounds(fh); return (x + dx, y + dy); }
    Check("a drag region hit-tests as caption", Eventually(() => Hit(At(200, 20)) == HtCaption, step), Hit(At(200, 20)));
    Check("a no-drag region inside it stays client", Hit(At(20, 20)) == HtClient, Hit(At(20, 20)));
    Check("the page outside the regions stays client", Hit(At(200, 120)) == HtClient, Hit(At(200, 120)));
    var before = WindowsShell.Bounds(fh);
    WindowsShell.Drag(before.X + 200, before.Y + 20, before.X + 300, before.Y + 80);
    var after = WindowsShell.Bounds(fh);
    Check("dragging a drag region moves the window", after.X > before.X + 50 && after.Width == before.Width, $"{before} -> {after}");
    await frameless.WebContents.ExecuteJavaScriptAsync("document.querySelector('div').style.webkitAppRegion = 'none'");
    Check("removing the region makes it client again", Eventually(() => Hit(At(200, 20)) == HtClient, step), Hit(At(200, 20)));
    frameless.Destroy();
    var framed = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: framed drag region", X = 200, Y = 200, Width = 400, Height = 250 });
    await framed.LoadUrl("data:text/html," + Page);
    await Task.Delay(500);
    var page = framed.GetContentBounds();
    Check("a framed window ignores drag regions", WindowsShell.HitTest(framed.GetNativeWindowHandle(), page.X + 200, page.Y + 20) == HtClient, WindowsShell.HitTest(framed.GetNativeWindowHandle(), page.X + 200, page.Y + 20));
    framed.Destroy();

    // TitleBarStyle.Hidden without an overlay: no title bar, the page fills the window.
    var hidden = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: hidden title bar", X = 220, Y = 220, Width = 400, Height = 250, TitleBarStyle = TitleBarStyle.Hidden });
    await Task.Delay(1200);
    Check("TitleBarStyle.Hidden fills the window with the page", hidden.GetContentBounds() == hidden.GetBounds() && hidden.GetTitleBarOverlayBounds() == default, $"{hidden.GetContentBounds()} vs {hidden.GetBounds()}");
    Check("SetTitleBarOverlay without an overlay throws", Throws<InvalidOperationException>(() => hidden.SetTitleBarOverlay(new TitleBarOverlayOptions { Color = "#000000" })));
    hidden.Destroy();

    // TitleBarOverlay: buttons at the top right in the colours asked for, over the page.
    var closed = false;
    var overlaid = new BrowserWindow(new BrowserWindowOptions
    {
        Title = "Oriel window test: title bar overlay", X = 240, Y = 240, Width = 500, Height = 300, TitleBarStyle = TitleBarStyle.Hidden,
        TitleBarOverlay = new TitleBarOverlayOptions { Color = "#2050C0", SymbolColor = "#FFFFFF", Height = 40 },
    });
    overlaid.Closed += (_, _) => closed = true;
    await overlaid.LoadUrl("data:text/html," + Page);
    await Task.Delay(800);
    var oh = overlaid.GetNativeWindowHandle();
    var box = overlaid.GetTitleBarOverlayBounds();
    // Electron's buttons: 45 DIPs, the second and third one DIP wider for the spacing on their left.
    Check("the overlay sits at the top right, 45 + 46 + 46 DIPs wide, at the height asked", box == new Rectangle(500 - 137, 0, 137, 40), box);
    int ButtonLeft(int index) { var (x, _, w, _) = WindowsShell.Bounds(oh); return x + w - 137 + (index == 0 ? 0 : 45 + (index - 1) * 46); }
    (int X, int Y) Button(int index, int dy = 0) => (ButtonLeft(index) + 6, WindowsShell.Bounds(oh).Y + 6 + dy);
    var resting = WindowsShell.ScreenPixel(Button(0).X, Button(0).Y);
    Check("the overlay has its colour", IsBlue(resting), Rgb(resting));
    var under = WindowsShell.ScreenPixel(Button(0).X, Button(0, 40).Y);
    Check("the page shows just below the overlay", under.R > 150 && under.G > 150 && under.B < 100, Rgb(under));
    // The hover fade: Electron's 150 ms ease-out, so colours between resting and red show on the way in and out.
    List<(byte R, byte G, byte B)> Samples(int ms, int x, int y)
    {
        var seen = new List<(byte R, byte G, byte B)>();
        for (var end = DateTime.UtcNow.AddMilliseconds(ms); DateTime.UtcNow < end; Thread.Sleep(4)) seen.Add(WindowsShell.ScreenPixel(x, y));
        return seen;
    }
    static bool Between((byte R, byte G, byte B) c, (byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
        => !Near(c, a, 12) && !Near(c, b, 12) && c.R >= Math.Min(a.R, b.R) - 6 && c.R <= Math.Max(a.R, b.R) + 6 && c.B >= Math.Min(a.B, b.B) - 6 && c.B <= Math.Max(a.B, b.B) + 6;
    var closeResting = WindowsShell.ScreenPixel(Button(2).X, Button(2).Y);
    WindowsShell.MovePointer(Button(2).X, Button(2).Y + 10);
    var fadeIn = Samples(400, Button(2).X, Button(2).Y);
    var red = fadeIn[^1];
    Check("the close button turns red under the pointer", Eventually(() => IsRed(WindowsShell.ScreenPixel(Button(2).X, Button(2).Y)), step), Rgb(WindowsShell.ScreenPixel(Button(2).X, Button(2).Y)));
    Check("the close button fades to red: in-between colours show on the way", IsRed(red) && fadeIn.Any(c => Between(c, closeResting, red)), string.Join(" ", fadeIn.Select(Rgb).Distinct()));
    var gap = WindowsShell.ScreenPixel(ButtonLeft(2), Button(2).Y);
    var firstFilled = WindowsShell.ScreenPixel(ButtonLeft(2) + 1, Button(2).Y);
    Check("the close button's one-DIP spacing stays the overlay colour while its background turns red", Near(gap, closeResting, 12) && IsRed(firstFilled), $"gap {Rgb(gap)} next {Rgb(firstFilled)} resting {Rgb(closeResting)}");
    WindowsShell.MovePointer(Button(0).X, Button(0).Y + 120);
    var fadeOut = Samples(400, Button(2).X, Button(2).Y);
    Check("leaving the close button fades it back", Near(fadeOut[^1], closeResting, 12) && fadeOut.Any(c => Between(c, red, closeResting)), string.Join(" ", fadeOut.Select(Rgb).Distinct()));
    WindowsShell.Hover(Button(0).X, Button(0).Y + 10);
    // Electron: the caption foreground (black on a light frame, white on a dark one) at 0x1A x 1.3 over an opaque colour.
    var foreground = TitleBar.CaptionForeground(TitleBar.DefaultFrame(WindowsWindowStyle.AppsUseDarkTheme())) & 0xFF;
    byte Toward(byte from) => (byte)Math.Round(from + (foreground - from) * 0x22 / 255.0);
    static bool Near((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, int within = 6) => Math.Abs(a.R - b.R) <= within && Math.Abs(a.G - b.G) <= within && Math.Abs(a.B - b.B) <= within;
    var tinted = (Toward(resting.R), Toward(resting.G), Toward(resting.B));
    Check("a hovered button lays the caption foreground at alpha 0x22 over its colour", Eventually(() => Near(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y), tinted), step),
        $"{Rgb(resting)} -> {Rgb(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y))}, expected {Rgb(tinted)}");
    overlaid.SetTitleBarOverlay(new TitleBarOverlayOptions { Color = "#20A050", Height = 32 });
    WindowsShell.Hover(Button(0).X, Button(0).Y + 120);
    Check("SetTitleBarOverlay changes the colour and height", Eventually(() => IsGreen(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y)) && overlaid.GetTitleBarOverlayBounds().Height == 32, step),
        $"{Rgb(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y))} {overlaid.GetTitleBarOverlayBounds()}");
    var shrunk = WindowsShell.ScreenPixel(Button(0).X, Button(0, 30).Y);
    Check("the page shows below the shorter overlay (its blue drag strip)", IsBlue(shrunk), Rgb(shrunk));
    static string? ErrorOf(Action act) { try { act(); return null; } catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; } }
    var badColor = ErrorOf(() => overlaid.SetTitleBarOverlay(new TitleBarOverlayOptions { Color = "nonsense" }));
    Check("SetTitleBarOverlay rejects a colour Chromium cannot parse, with Electron's message", badColor == "ArgumentException: Could not parse color as CSS color", badColor);
    var badSymbol = ErrorOf(() => overlaid.SetTitleBarOverlay(new TitleBarOverlayOptions { Color = "rgb(255, 0, 0)", SymbolColor = "#12345" }));
    await Task.Delay(500);
    var notRepainted = WindowsShell.ScreenPixel(Button(0).X, Button(0).Y);
    overlaid.SetTitleBarOverlay(new TitleBarOverlayOptions { Height = 32 });
    Check("a bad symbol colour throws Electron's message, the colour before it is kept unpainted until the next update",
        badSymbol == "ArgumentException: Could not parse symbol color as CSS color" && IsGreen(notRepainted) && Eventually(() => IsRed(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y)), step),
        $"{badSymbol} {Rgb(notRepainted)} -> {Rgb(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y))}");
    overlaid.SetTitleBarOverlay(new TitleBarOverlayOptions { Color = "hsl(120, 100%, 25%)" });
    Check("SetTitleBarOverlay takes hsl() colours", Eventually(() => IsGreen(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y)), step), Rgb(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y)));
    overlaid.SetTitleBarOverlay(new TitleBarOverlayOptions { Color = "#00000000" });
    Check("a transparent overlay colour shows the page through the buttons", Eventually(() => IsBlue(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y)), step), Rgb(WindowsShell.ScreenPixel(Button(0).X, Button(0).Y)));
    overlaid.SetTitleBarOverlay(new TitleBarOverlayOptions { Color = "#20A050" });
    await Task.Delay(500);
    // Snap layouts: Windows asks what is under the pointer, and the drawn maximize button answers HTMAXBUTTON.
    const int HtMaxButton = 9;
    var (mx, my) = (Button(1).X + 17, Button(1).Y + 8);
    Check("the maximize button answers HTMAXBUTTON, its neighbours do not", WindowsShell.HitTest(oh, mx, my) == HtMaxButton && WindowsShell.HitTest(oh, Button(0).X + 17, my) == HtClient,
        $"{WindowsShell.HitTest(oh, mx, my)} {WindowsShell.HitTest(oh, Button(0).X + 17, my)}");
    var maxResting = WindowsShell.ScreenPixel(Button(1).X, Button(1).Y);
    var below = (X: mx, Y: WindowsShell.Bounds(oh).Y + 150);
    static bool IsYellow((byte R, byte G, byte B) c) => c.R > 200 && c.G > 200 && c.B < 100;
    var pageBelow = WindowsShell.ScreenPixel(below.X, below.Y);
    WindowsShell.Hover(mx - 4, my);
    WindowsShell.Hover(mx, my);
    var maxHovered = WindowsShell.ScreenPixel(Button(1).X, Button(1).Y);
    Check("the maximize button shows its hover through HTMAXBUTTON", maxHovered.R > maxResting.R + 8 && maxHovered.G > maxResting.G + 8, $"{Rgb(maxResting)} -> {Rgb(maxHovered)}");
    Check("hovering it opens Windows 11's snap layouts over the page", IsYellow(pageBelow) && Eventually(() => !IsYellow(WindowsShell.ScreenPixel(below.X, below.Y)), TimeSpan.FromSeconds(4)),
        $"{Rgb(pageBelow)} -> {Rgb(WindowsShell.ScreenPixel(below.X, below.Y))}");
    WindowsShell.Hover(WindowsShell.Bounds(oh).X + 60, WindowsShell.Bounds(oh).Y + 200);
    await Task.Delay(800);

    // The buttons follow the window's flags: a disabled one takes no clicks and offers no snap layouts.
    overlaid.SetMaximizable(false);
    await Task.Delay(300);
    WindowsShell.Click(Button(1).X, Button(1).Y + 8);
    Check("SetMaximizable(false) disables the maximize button", WindowsShell.HitTest(oh, mx, my) != HtMaxButton && !overlaid.IsMaximized, $"hit={WindowsShell.HitTest(oh, mx, my)} maximized={overlaid.IsMaximized}");
    overlaid.SetMaximizable(true);
    overlaid.SetMinimizable(false);
    await Task.Delay(300);
    WindowsShell.Click(Button(0).X, Button(0).Y + 8);
    Check("SetMinimizable(false) disables the minimize button", !overlaid.IsMinimized);
    overlaid.SetMinimizable(true);
    overlaid.SetClosable(false);
    await Task.Delay(300);
    WindowsShell.Click(Button(2).X, Button(2).Y + 8);
    await Task.Delay(500);
    Check("SetClosable(false) disables the close button", !closed && !overlaid.IsDestroyed);
    overlaid.SetClosable(true);
    await Task.Delay(300);

    WindowsShell.Click(Button(1).X, Button(1).Y + 8);
    Check("the maximize button maximizes", Eventually(() => overlaid.IsMaximized, step));
    WindowsShell.Click(Button(1).X, Button(1).Y + 8);
    Check("the same button then restores", Eventually(() => !overlaid.IsMaximized, step));
    await Task.Delay(500);
    WindowsShell.Click(Button(0).X, Button(0).Y + 8);
    Check("the minimize button minimizes", Eventually(() => overlaid.IsMinimized, step));
    overlaid.Restore();
    await Task.Delay(800);
    overlaid.SetFullScreen(true);
    await Task.Delay(1500);
    var (fx, fy, fw, _) = WindowsShell.Bounds(oh);
    var fullscreenCorner = WindowsShell.ScreenPixel(fx + fw - 120, fy + 6);
    Check("the overlay hides in full screen", Eventually(() => overlaid.IsFullScreen, step) && !IsGreen(fullscreenCorner), Rgb(fullscreenCorner));
    overlaid.SetFullScreen(false);
    await Task.Delay(1500);
    WindowsShell.Click(Button(2).X, Button(2).Y + 8);
    Check("the close button closes the window", Eventually(() => closed, step));

    // Defaults: the system's caption height and button colours.
    var plain = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: default overlay", X = 260, Y = 260, Width = 400, Height = 250, TitleBarStyle = TitleBarStyle.Hidden, TitleBarOverlay = new() { Color = "not a colour" } });
    await Task.Delay(1500);
    var caption = WindowsWindowStyle.CaptionHeight(plain.GetNativeWindowHandle());
    Check("an overlay without a height takes the system caption height", plain.GetTitleBarOverlayBounds().Height == caption && caption is > 20 and < 50, $"{plain.GetTitleBarOverlayBounds()} caption={caption}");
    var (px, py, pw, _) = WindowsShell.Bounds(plain.GetNativeWindowHandle());
    var face = WindowsWindowStyle.ButtonColors().Face;
    var plainPixel = WindowsShell.ScreenPixel(px + pw - 137 + 6, py + 6);
    Check("an unparsable Color option is ignored: the button face colour shows, as Electron", Near(plainPixel, ((byte)(face >> 16), (byte)(face >> 8), (byte)face), within: 12), $"{Rgb(plainPixel)} vs face {face:X8}");
    plain.Destroy();

    // SetParentWindow: Win32's owner, read back, and Oriel's parent/child bookkeeping with it.
    var owner = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: new parent", X = 200, Y = 200, Width = 400, Height = 300 });
    var owned = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: reparented", X = 260, Y = 260, Width = 300, Height = 200 });
    await Task.Delay(1200);
    var (ph, ch) = (owner.GetNativeWindowHandle(), owned.GetNativeWindowHandle());
    owned.SetParentWindow(owner);
    Check("SetParentWindow makes the parent the Win32 owner", WindowsWindowStyle.GetOwner(ch) == ph, $"{WindowsWindowStyle.GetOwner(ch)} vs {ph}");
    Check("SetParentWindow updates GetParentWindow and GetChildWindows", owned.GetParentWindow() == owner && owner.GetChildWindows().Contains(owned));
    var (ox, oy, ow, oh2) = WindowsShell.Bounds(ch);
    Check("SetParentWindow re-shows a visible window and brings it to the top", owned.IsVisible && WindowsShell.RootAt(ox + ow / 2, oy + oh2 / 2) == ch, $"visible={owned.IsVisible} top={WindowsShell.RootAt(ox + ow / 2, oy + oh2 / 2)} vs {ch}");
    owned.Maximize();
    Check("(maximized before reparenting)", Eventually(() => owned.IsMaximized, step));
    owned.SetParentWindow(null);
    Check("SetParentWindow(null) makes it top-level again", WindowsWindowStyle.GetOwner(ch) == 0 && owned.GetParentWindow() is null && owner.GetChildWindows().Count == 0, WindowsWindowStyle.GetOwner(ch));
    await Task.Delay(500);
    Check("the re-show keeps the window's show state: still maximized and visible", owned.IsMaximized && owned.IsVisible, $"maximized={owned.IsMaximized} visible={owned.IsVisible}");
    owned.Restore();
    await Task.Delay(500);
    var hiddenChild = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: hidden reparented", Width = 200, Height = 150, Show = false });
    await Task.Delay(800);
    hiddenChild.SetParentWindow(owner);
    await Task.Delay(500);
    Check("SetParentWindow leaves a hidden window hidden", WindowsWindowStyle.GetOwner(hiddenChild.GetNativeWindowHandle()) == ph && !hiddenChild.IsVisible && !WindowsShell.Visible(hiddenChild.GetNativeWindowHandle()), $"visible={hiddenChild.IsVisible}");
    owned.SetParentWindow(owner);
    owner.Destroy();
    Check("a window given a parent later closes with it", Eventually(() => owned.IsDestroyed, step));
    var modalParent = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: modal's parent", Width = 300, Height = 200 });
    var modal = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: modal", Width = 200, Height = 150, Parent = modalParent, Modal = true });
    await Task.Delay(800);
    Check("a modal window keeps its parent", Throws<InvalidOperationException>(() => modal.SetParentWindow(null)));
    modalParent.Destroy();
    win.Focus();
    await Task.Delay(300);
}

async Task WindowPolishAsync(BrowserWindow win)
{
    // Windows only so far; Win32 reads each setting back.
    if (!OperatingSystem.IsWindows()) return;

    BrowserWindow Solid(string title, string colour, int x, int y, int width, int height, bool show = true, bool frame = true, double opacity = 1, bool kiosk = false)
    {
        var b = new BrowserWindow(new BrowserWindowOptions { Title = title, X = x, Y = y, Width = width, Height = height, Show = show, Frame = frame, Opacity = opacity, Kiosk = kiosk, BackgroundColor = colour });
        _ = b.LoadUrl($"data:text/html,<body style='margin:0;background:{Uri.EscapeDataString(colour)}'></body>");
        return b;
    }
    static string Rgb((byte R, byte G, byte B) c) => $"{c.R},{c.G},{c.B}";
    // The display is colour-managed: pure red reads about (238, 0, 0), blue (55, 0, 255), green (92, 255, 0).
    static bool IsRed((byte R, byte G, byte B) c) => c.R > 200 && c.G < 60 && c.B < 60;
    static bool IsGreen((byte R, byte G, byte B) c) => c.G > 200 && c.R < 140 && c.B < 60;
    static bool IsBlue((byte R, byte G, byte B) c) => c.B > 200 && c.R < 100 && c.G < 60;
    (int X, int Y) Centre(BrowserWindow b) { var (x, y, w, h) = WindowsShell.Bounds(b.GetNativeWindowHandle()); return (x + w / 2, y + h / 2 + 10); }

    // A white backdrop under the test windows makes blending and shadows measurable.
    var backdrop = Solid("Oriel window test: backdrop", "#FFFFFF", 80, 80, 700, 500);
    await Task.Delay(1500);

    // ShowInactive: visible, painted, and the foreground window is unchanged.
    var foreground = WindowsShell.Foreground();
    var shownCount = 0;
    var inactive = Solid("Oriel window test: show inactive", "#FF0000", 120, 120, 300, 200, show: false);
    inactive.Shown += (_, _) => shownCount++;
    await Task.Delay(800);
    inactive.ShowInactive();
    var ih = inactive.GetNativeWindowHandle();
    Check("ShowInactive shows the window and raises Shown", Eventually(() => inactive.IsVisible && shownCount == 1, step), $"visible={inactive.IsVisible} shown={shownCount}");
    await Task.Delay(1000);
    Check("ShowInactive leaves the foreground window alone", WindowsShell.Foreground() == foreground && WindowsShell.Foreground() != ih, $"{foreground} -> {WindowsShell.Foreground()} (window {ih})");
    Check("a window shown inactive paints its page", Eventually(() => IsRed(WindowsShell.ScreenPixel(Centre(inactive).X, Centre(inactive).Y)), step), Rgb(WindowsShell.ScreenPixel(Centre(inactive).X, Centre(inactive).Y)));

    // CEF activates a window created hidden, and a window that starts a load; Electron does neither.
    var front = WindowsShell.Foreground();
    var hiddenOne = Solid("Oriel window test: created hidden", "#00FF00", 500, 120, 200, 150, show: false);
    await Task.Delay(1500);
    Check("a window created hidden does not take the foreground", WindowsShell.Foreground() == front && !hiddenOne.IsVisible, $"{front} -> {WindowsShell.Foreground()} (window {hiddenOne.GetNativeWindowHandle()})");
    _ = hiddenOne.LoadUrl("data:text/html,again");
    await Task.Delay(1000);
    Check("LoadUrl in a hidden window does not take the foreground", WindowsShell.Foreground() == front, $"{front} -> {WindowsShell.Foreground()}");
    hiddenOne.Destroy();
    _ = inactive.LoadUrl("data:text/html,<body style='margin:0;background:%23FF0000'></body>");
    await Task.Delay(1000);
    Check("LoadUrl in a background window does not bring it to the front", WindowsShell.Foreground() == front, $"{front} -> {WindowsShell.Foreground()} (window {ih})");

    // Opacity: the layered alpha reads back, and the red page blends with the white backdrop.
    var faded = Solid("Oriel window test: opacity", "#FF0000", 450, 350, 300, 200, opacity: 0.5);
    await Task.Delay(1500);
    var fh = faded.GetNativeWindowHandle();
    var blended = WindowsShell.ScreenPixel(Centre(faded).X, Centre(faded).Y);
    Check("the Opacity option makes the window layered at that alpha", WindowsShell.Alpha(fh) == 127 && faded.GetOpacity() == 0.5, $"{WindowsShell.Alpha(fh)} {faded.GetOpacity()}");
    Check("a half-opaque red window over white shows pink", blended.R > 200 && blended.G is > 90 and < 170 && blended.B is > 90 and < 170, Rgb(blended));
    faded.SetOpacity(0.25);
    Check("SetOpacity changes the alpha", Eventually(() => WindowsShell.Alpha(fh) == 63, step) && faded.GetOpacity() == 0.25, $"{WindowsShell.Alpha(fh)} {faded.GetOpacity()}");
    faded.SetOpacity(3);
    Check("SetOpacity clamps to 1", Eventually(() => WindowsShell.Alpha(fh) == 255, step) && faded.GetOpacity() == 1, $"{WindowsShell.Alpha(fh)} {faded.GetOpacity()}");
    faded.SetOpacity(0.5);
    await Task.Delay(300);

    // Click-through: the window stops taking the mouse, and keeps its opacity when it takes it again.
    var (fx, fy) = Centre(faded);
    Check("before SetIgnoreMouseEvents the window takes the mouse", WindowsShell.RootAt(fx, fy) == fh, $"{WindowsShell.RootAt(fx, fy)} vs {fh}");
    faded.SetIgnoreMouseEvents(true);
    Check("SetIgnoreMouseEvents(true) passes the mouse to the window below", Eventually(() => WindowsShell.ClickThrough(fh) && WindowsShell.RootAt(fx, fy) != fh, step), $"{WindowsShell.RootAt(fx, fy)} vs {fh}");
    faded.SetIgnoreMouseEvents(false);
    Check("SetIgnoreMouseEvents(false) takes it back and keeps the opacity", Eventually(() => WindowsShell.RootAt(fx, fy) == fh, step) && WindowsShell.Alpha(fh) == 127, $"{WindowsShell.RootAt(fx, fy)} alpha={WindowsShell.Alpha(fh)}");
    var clicky = Solid("Oriel window test: click-through, opaque", "#00FF00", 450, 120, 200, 150);
    await Task.Delay(1500);
    var ch = clicky.GetNativeWindowHandle();
    var (cx, cy) = Centre(clicky);
    clicky.SetIgnoreMouseEvents(true);
    await Task.Delay(300);
    var opaqueThrough = WindowsShell.RootAt(cx, cy) != ch;
    var opaquePainted = WindowsShell.ScreenPixel(cx, cy);
    clicky.SetIgnoreMouseEvents(false);
    Check("an opaque click-through window still draws, and is no longer layered after", opaqueThrough && IsGreen(opaquePainted) && Eventually(() => WindowsShell.Alpha(ch) is null, step), $"through={opaqueThrough} {Rgb(opaquePainted)} alpha={WindowsShell.Alpha(ch)}");
    clicky.Destroy();
    faded.Destroy();

    // Forward: the page still sees mouse moves while clicks pass through; without it, it sees none.
    var hover = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: forward", X = 450, Y = 120, Width = 300, Height = 200, BackgroundColor = "#0000FF" });
    _ = hover.LoadUrl("data:text/html," + Uri.EscapeDataString("<body style='margin:0;height:100vh;background:#0000FF' onmousemove='moves++' onmousedown='downs++'><script>var moves=0,downs=0</script></body>"));
    await Task.Delay(1500);
    var hh = hover.GetNativeWindowHandle();
    var (hx, hy) = Centre(hover);
    async Task<int> PageCount(string name) => int.TryParse(await Js(hover, name) as string, out var n) ? n : -1;
    async Task<int> MovesOver()
    {
        await Js(hover, "moves=0,downs=0");
        WindowsShell.MoveMouse(hx, hy);
        WindowsShell.MoveMouse(hx + 20, hy + 10);
        await Task.Delay(400);
        return await PageCount("moves");
    }
    hover.SetIgnoreMouseEvents(true, new IgnoreMouseEventsOptions { Forward = true });
    await Task.Delay(300);
    var forwarded = await MovesOver();
    Check("SetIgnoreMouseEvents(true, Forward) still delivers mouse moves to the page", forwarded > 0 && WindowsShell.ClickThrough(hh) && WindowsShell.Forwarding(hh), $"moves={forwarded} forwarding={WindowsShell.Forwarding(hh)}");
    WindowsShell.Drag(hx, hy, hx, hy);
    var downs = await PageCount("downs");
    Check("with Forward a click still passes through to the window below", WindowsShell.RootAt(hx, hy) != hh && downs == 0, $"{WindowsShell.RootAt(hx, hy)} vs {hh} downs={downs}");
    hover.SetIgnoreMouseEvents(true);
    await Task.Delay(300);
    var unforwarded = await MovesOver();
    Check("SetIgnoreMouseEvents(true) without Forward delivers no mouse moves", unforwarded == 0 && !WindowsShell.Forwarding(hh) && WindowsShell.ClickThrough(hh), $"moves={unforwarded}");
    hover.SetIgnoreMouseEvents(true, new IgnoreMouseEventsOptions { Forward = true });
    await Task.Delay(300);
    // The pass-through clicks activated the window below; bring this one back over it before hit-testing.
    hover.Focus();
    await Task.Delay(300);
    var wasForwarding = WindowsShell.Forwarding(hh);
    hover.SetIgnoreMouseEvents(false, new IgnoreMouseEventsOptions { Forward = true });
    Check("SetIgnoreMouseEvents(false, Forward) takes the mouse back and ends forwarding", wasForwarding && Eventually(() => !WindowsShell.Forwarding(hh) && !WindowsShell.ClickThrough(hh) && WindowsShell.RootAt(hx, hy) == hh, step), $"was={wasForwarding} forwarding={WindowsShell.Forwarding(hh)} root={WindowsShell.RootAt(hx, hy)}");
    hover.Destroy();

    // Content protection: capture sees what is behind the window.
    var secret = Solid("Oriel window test: content protection", "#0000FF", 200, 200, 300, 200);
    await Task.Delay(1500);
    var (sx, sy) = Centre(secret);
    var before = Rgb(WindowsShell.ScreenPixel(sx, sy));
    secret.SetContentProtection(true);
    Check("SetContentProtection(true) hides the window from capture", IsBlue(WindowsShell.ScreenPixel(sx, sy)) && Eventually(() => secret.IsContentProtected() && !IsBlue(WindowsShell.ScreenPixel(sx, sy)), step), $"{before} -> {Rgb(WindowsShell.ScreenPixel(sx, sy))} protected={secret.IsContentProtected()}");
    secret.SetContentProtection(false);
    Check("SetContentProtection(false) shows it again", Eventually(() => !secret.IsContentProtected() && IsBlue(WindowsShell.ScreenPixel(sx, sy)), step), Rgb(WindowsShell.ScreenPixel(sx, sy)));
    secret.Destroy();

    // Shadow: Electron only stores hasShadow on Windows; a frameless window keeps darkening the backdrop just below it.
    var flat = Solid("Oriel window test: shadow", "#FF0000", 220, 160, 300, 200, frame: false);
    await Task.Delay(1500);
    var lh = flat.GetNativeWindowHandle();
    // 8 px below the window against the backdrop's white beside it, at the same height, out of the shadow's reach.
    (byte R, byte G, byte B) Below() { var (x, y, w, h) = WindowsShell.Bounds(lh); return WindowsShell.ScreenPixel(x + w / 2, y + h + 8); }
    var white = WindowsShell.ScreenPixel(WindowsShell.Bounds(lh).X - 60, WindowsShell.Bounds(lh).Y + WindowsShell.Bounds(lh).Height + 8).R;
    var shaded = Below();
    flat.SetHasShadow(false);
    await Task.Delay(800);
    Check("SetHasShadow(false) keeps a frameless window's shadow, as Electron on Windows, and reads back false", shaded.R < white - 20 && Below().R < white - 20 && !flat.GetHasShadow(), $"white={white} {Rgb(shaded)} -> {Rgb(Below())}");
    flat.SetHasShadow(true);
    Check("SetHasShadow(true) reads back true", flat.GetHasShadow() && Below().R < white - 20, $"white={white} {Rgb(Below())}");
    flat.Destroy();
    var framed = Solid("Oriel window test: framed shadow", "#FF0000", 220, 160, 300, 200);
    await Task.Delay(1200);
    framed.SetHasShadow(false);
    await Task.Delay(300);
    Check("a framed window keeps its frame (and DWM's shadow) under SetHasShadow(false)", WindowsShell.NcRendering(framed.GetNativeWindowHandle()) && !framed.GetHasShadow());
    framed.Destroy();

    // GetNormalBounds: the restored bounds survive maximize and minimize.
    var normal = Solid("Oriel window test: normal bounds", "#FFFF00", 240, 180, 320, 220);
    await Task.Delay(1500);
    var placed = normal.GetBounds();
    Check("GetNormalBounds is the bounds of a normal window", normal.GetNormalBounds() == placed, $"{normal.GetNormalBounds()} vs {placed}");
    normal.Maximize();
    Check("GetNormalBounds keeps the restored bounds while maximized", Eventually(() => normal.IsMaximized, step) && normal.GetNormalBounds() == placed && normal.GetBounds() != placed, $"{normal.GetNormalBounds()} vs {placed}");
    normal.Restore();
    await Task.Delay(500);
    normal.Minimize();
    Check("GetNormalBounds keeps the restored bounds while minimized", Eventually(() => normal.IsMinimized, step) && normal.GetNormalBounds() == placed, $"{normal.GetNormalBounds()} vs {placed}");
    normal.Restore();
    await Task.Delay(500);

    // WillMove and WillResize: a real drag of the title bar and corner, then WM_SIZING for an edge.
    var nh = normal.GetNativeWindowHandle();
    var moves = new List<Rectangle>();
    var cancelMove = true;
    normal.WillMove += (_, e) => { moves.Add(e.NewBounds); e.Cancel = cancelMove; };
    var (nx, ny, nw, nhh) = WindowsShell.Bounds(nh);
    WindowsShell.Drag(nx + 60, ny + 15, nx + 160, ny + 75);
    Check("a cancelled WillMove keeps the window where it is", moves.Count > 0 && WindowsShell.Bounds(nh) == (nx, ny, nw, nhh), $"{moves.Count} moves, {WindowsShell.Bounds(nh)} vs {(nx, ny, nw, nhh)}");
    Check("WillMove reports the bounds the drag proposed", moves.Count > 0 && moves[^1].X > placed.X && moves[^1].Width == placed.Width, moves.Count > 0 ? moves[^1] : null);
    cancelMove = false;
    WindowsShell.Drag(nx + 60, ny + 15, nx + 160, ny + 75);
    var moved = WindowsShell.Bounds(nh);
    Check("an uncancelled WillMove lets the drag move the window", moved.X > nx + 50 && moved.Width == nw, $"{(nx, ny)} -> {moved}");
    var resizes = new List<WillResizeEventArgs>();
    var cancelResize = true;
    normal.WillResize += (_, e) => { resizes.Add(e); e.Cancel = cancelResize; };
    WindowsShell.Drag(moved.X + moved.Width - 2, moved.Y + moved.Height - 2, moved.X + moved.Width + 80, moved.Y + moved.Height + 60);
    Check("a cancelled WillResize keeps the size; the edge is bottom-right", resizes.Count > 0 && WindowsShell.Bounds(nh) == moved && resizes[^1].Edge == ResizeEdge.BottomRight && resizes[^1].NewBounds.Width > placed.Width,
        $"{resizes.Count} resizes, {WindowsShell.Bounds(nh)} vs {moved}, {(resizes.Count > 0 ? $"{resizes[^1].Edge} {resizes[^1].NewBounds}" : "")}");
    cancelResize = false;
    WindowsShell.Drag(moved.X + moved.Width - 2, moved.Y + moved.Height - 2, moved.X + moved.Width + 80, moved.Y + moved.Height + 60);
    Check("an uncancelled WillResize lets the drag resize the window", WindowsShell.Bounds(nh).Width > moved.Width + 40, $"{moved} -> {WindowsShell.Bounds(nh)}");
    resizes.Clear();
    cancelResize = true;
    var now = WindowsShell.Bounds(nh);
    var left = WindowsShell.SendDrag(nh, 3 /* WMSZ_TOP */, (now.X, now.Y - 40, now.X + now.Width, now.Y + now.Height));
    Check("WM_SIZING on the top edge reports Top, and cancelling restores the rectangle", resizes.Count == 1 && resizes[0].Edge == ResizeEdge.Top && left == (now.X, now.Y, now.X + now.Width, now.Y + now.Height),
        $"{resizes.Count} {(resizes.Count > 0 ? resizes[0].Edge : null)} {left}");
    normal.Destroy();

    // Kiosk: full screen on the window's display, and back.
    var kiosk = Solid("Oriel window test: kiosk", "#000000", 260, 200, 300, 200, kiosk: true);
    Check("the Kiosk option opens full screen", Eventually(() => kiosk.IsFullScreen && kiosk.IsKiosk(), step) && kiosk.GetBounds() == Screen.GetDisplayMatching(kiosk.GetBounds()).Bounds, $"{kiosk.IsFullScreen} {kiosk.GetBounds()}");
    kiosk.SetKiosk(false);
    Check("SetKiosk(false) leaves full screen", Eventually(() => !kiosk.IsFullScreen && !kiosk.IsKiosk(), step), $"{kiosk.IsFullScreen} {kiosk.GetBounds()}");
    kiosk.SetKiosk(true);
    Check("SetKiosk(true) enters it again", Eventually(() => kiosk.IsFullScreen && kiosk.IsKiosk(), step));
    kiosk.SetFullScreen(false);
    Check("IsKiosk is IsFullScreen, as Electron answers: SetFullScreen(false) ends it", Eventually(() => !kiosk.IsFullScreen && !kiosk.IsKiosk(), step), $"{kiosk.IsFullScreen} {kiosk.IsKiosk()}");
    kiosk.SetFullScreen(true);
    Check("SetFullScreen(true) makes IsKiosk true", Eventually(() => kiosk.IsFullScreen && kiosk.IsKiosk(), step), $"{kiosk.IsFullScreen} {kiosk.IsKiosk()}");
    kiosk.Destroy();
    inactive.Destroy();
    backdrop.Destroy();
    win.Focus();
    await Task.Delay(500);
}

async Task PowerAndShortcutsAsync()
{
    if (!OperatingSystem.IsWindows())
    {
        Check("globalShortcut, powerMonitor and powerSaveBlocker throw off Windows", Throws<PlatformNotSupportedException>(() => GlobalShortcut.IsRegistered("Ctrl+A"))
            && Throws<PlatformNotSupportedException>(() => PowerMonitor.GetSystemIdleTime()) && Throws<PlatformNotSupportedException>(() => PowerSaveBlocker.Start(PowerSaveBlockerType.PreventAppSuspension)));
        return;
    }

    // Global shortcuts: Windows refuses a combination another app holds, so this test can see Oriel holding it.
    const uint Mods = 0x1 | 0x2 | 0x4; // MOD_ALT | MOD_CONTROL | MOD_SHIFT
    const uint F9 = 0x78;
    var fired = 0;
    Check("Register takes a free shortcut", GlobalShortcut.Register("Ctrl+Alt+Shift+F9", () => fired++) && GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9"));
    Check("the shortcut is held system-wide", !WindowsShell.HotKeyFree(Mods, F9));
    Check("the same combination written differently is the same shortcut", GlobalShortcut.IsRegistered("Shift+Control+Alt+F9") && !GlobalShortcut.Register("Alt+Shift+CmdOrCtrl+F9", () => { }));
    WindowsShell.Chord(0x11 /* VK_CONTROL */, 0x12 /* VK_MENU */, 0x10 /* VK_SHIFT */, (byte)F9);
    Check("pressing it runs the callback, whatever has focus", Eventually(() => fired == 1, step), fired);
    // Chromium registers without MOD_NOREPEAT: a held key's repeated key-downs fire the callback each time.
    fired = 0;
    WindowsShell.Hold(4, 0x11, 0x12, 0x10, (byte)F9);
    Check("a held shortcut repeats the callback (no MOD_NOREPEAT)", Eventually(() => fired >= 3, step), fired);
    GlobalShortcut.Unregister("Ctrl+Alt+Shift+F9");
    fired = 1;
    Check("Unregister lets it go", !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9") && WindowsShell.HotKeyFree(Mods, F9));
    WindowsShell.Chord(0x11, 0x12, 0x10, (byte)F9);
    await Task.Delay(300);
    Check("a released shortcut no longer runs the callback", fired == 1, fired);
    GlobalShortcut.RegisterAll(["Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F11"], () => { });
    Check("RegisterAll takes each", GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F10") && GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F11") && !WindowsShell.HotKeyFree(Mods, 0x79));
    GlobalShortcut.UnregisterAll();
    Check("UnregisterAll lets them all go", !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F10") && WindowsShell.HotKeyFree(Mods, 0x79) && WindowsShell.HotKeyFree(Mods, 0x7A));
    Check("an invalid accelerator throws", Throws<ArgumentException>(() => GlobalShortcut.Register("Ctrl+Nope", () => { })));

    // Idle time and power source, read independently.
    WindowsShell.Nudge();
    await Task.Delay(2500);
    var quiet = PowerMonitor.GetSystemIdleTime();
    WindowsShell.Nudge();
    Check("GetSystemIdleTime counts seconds without input, and input resets it", quiet is 2 or 3 && PowerMonitor.GetSystemIdleTime() <= 1, $"{quiet} -> {PowerMonitor.GetSystemIdleTime()}");
    Check("GetSystemIdleState: active under the threshold, idle at zero", PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Active && PowerMonitor.GetSystemIdleState(0) == SystemIdleState.Idle,
        $"{PowerMonitor.GetSystemIdleState(60)} {PowerMonitor.GetSystemIdleState(0)}");
    var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    foreach (var arg in new[] { "-NoProfile", "-Command", "Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus" }) psi.ArgumentList.Add(arg);
    var line = System.Diagnostics.Process.Start(psi)!.StandardOutput.ReadToEnd().Trim();
    Check("IsOnBatteryPower agrees with Windows' power line status", PowerMonitor.IsOnBatteryPower() == (line == "Offline") && PowerMonitor.OnBatteryPower == PowerMonitor.IsOnBatteryPower(), $"{line} {PowerMonitor.IsOnBatteryPower()}");

    // Power and session notifications reach the host window; the machine is neither suspended nor locked here.
    var power = new List<string>();
    PowerMonitor.Suspended += (_, _) => power.Add("suspend");
    PowerMonitor.Resumed += (_, _) => power.Add("resume");
    PowerMonitor.OnAc += (_, _) => power.Add("on-ac");
    PowerMonitor.OnBattery += (_, _) => power.Add("on-battery");
    PowerMonitor.ScreenLocked += (_, _) => power.Add("lock");
    PowerMonitor.ScreenUnlocked += (_, _) => power.Add("unlock");
    var speed = new List<int>();
    PowerMonitor.SpeedLimitChanged += (_, e) => speed.Add(e.Limit);
    var host = WindowsShell.SystemHost();
    foreach (var pbt in new nint[] { 0x4, 0x7, 0x12, 0xA }) WindowsShell.Send(host, 0x0218 /* WM_POWERBROADCAST */, pbt);
    foreach (var wts in new nint[] { 0x7, 0x8, 0x5 }) WindowsShell.Send(host, 0x02B1 /* WM_WTSSESSION_CHANGE */, wts);
    Check("power and session notifications raise Electron's events (a status change without a new source raises none)",
        host != 0 && power.SequenceEqual(["suspend", "resume", "lock", "unlock"]), string.Join(",", power));

    // The limit is 100 at start and only changes under real throttling, which the test cannot cause.
    await Task.Delay(2500);
    Check("SpeedLimitChanged subscribes and reports only limits under 100 (weak: no throttling to read back)", speed.All(l => l is >= 0 and < 100), string.Join(",", speed));

    // powercfg /requests lists each request by type; it needs elevation, so without it only the display request is read back
    // through the execution state (ES_DISPLAY_REQUIRED 2).
    var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductName;
    if (string.IsNullOrWhiteSpace(product)) product = "Oriel.WindowTest";
    var win11 = Environment.OSVersion.Version.Build >= 22000;
    bool Held(Dictionary<string, List<string>> requests, string section) => requests.TryGetValue(section, out var entries) && entries.Any(e => e.Trim() == product);
    string Held3(Dictionary<string, List<string>> r) => $"DISPLAY={Held(r, "DISPLAY")} SYSTEM={Held(r, "SYSTEM")} EXECUTION={Held(r, "EXECUTION")}";
    var baseline = WindowsShell.ExecutionState();
    var display = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventDisplaySleep);
    if (WindowsShell.PowerRequests() is { } r1)
    {
        Check("PreventDisplaySleep is a display request, plus a system request below Windows 11, named for the product",
            PowerSaveBlocker.IsStarted(display) && Held(r1, "DISPLAY") && Held(r1, "SYSTEM") == !win11 && !Held(r1, "EXECUTION"), Held3(r1));
        var app = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventAppSuspension);
        var r2 = WindowsShell.PowerRequests()!;
        Check("with both active one lock holds, at the display type: the app type adds no execution request", app != display && Held(r2, "DISPLAY") && !Held(r2, "EXECUTION"), Held3(r2));
        Check("stopping the display blocker drops the lock to the app type: an execution request, no display or system",
            PowerSaveBlocker.Stop(display) && WindowsShell.PowerRequests() is { } r3 && Held(r3, "EXECUTION") && !Held(r3, "DISPLAY") && !Held(r3, "SYSTEM"), Held3(WindowsShell.PowerRequests()!));
        Check("Stop ends the last blocker and releases every request; a stopped id cannot be stopped again",
            PowerSaveBlocker.Stop(app) && !PowerSaveBlocker.Stop(app) && !PowerSaveBlocker.IsStarted(app) && WindowsShell.PowerRequests() is { } r4 && !Held(r4, "DISPLAY") && !Held(r4, "SYSTEM") && !Held(r4, "EXECUTION"),
            Held3(WindowsShell.PowerRequests()!));
    }
    else
    {
        Check("PreventDisplaySleep keeps the display required (weak: powercfg /requests needs elevation)", PowerSaveBlocker.IsStarted(display) && (WindowsShell.ExecutionState() & 0x2) != 0, $"0x{baseline:X} -> 0x{WindowsShell.ExecutionState():X}");
        var app = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventAppSuspension);
        Check("ids count up from the process's first start, and starting never throws", app == display + 1, $"{display} {app}");
        Check("Stop ends each, and a stopped blocker cannot be stopped again", PowerSaveBlocker.Stop(display) && PowerSaveBlocker.Stop(app) && !PowerSaveBlocker.Stop(app) && !PowerSaveBlocker.IsStarted(display)
            && WindowsShell.ExecutionState() == baseline, $"0x{baseline:X} -> 0x{WindowsShell.ExecutionState():X}");
    }
}

async Task TrayAsync()
{
    static NativeImage Square(byte b, byte g, byte r)
    {
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        return NativeImage.CreateFromBitmap(pixels, new NativeImageBitmapOptions { Width = 16, Height = 16 });
    }
    if (!OperatingSystem.IsWindows())
    {
        Check("Tray throws PlatformNotSupportedException off Windows", Throws<PlatformNotSupportedException>(() => _ = new Tray(Square(0, 0, 255))));
        return;
    }

    // The process's first tray icon: Electron numbers icons from 2.
    const uint Id = 2;
    var tray = new Tray(Square(0, 0, 255));
    tray.SetToolTip("Oriel tray test");
    var host = WindowsShell.TrayHost();
    Check("a Tray puts an icon in the notification area under Electron's first id (the shell holds it)", host != 0 && WindowsShell.NotifyIcon(host, Id, delete: false), host);
    var bounds = tray.GetBounds();
    Check("GetBounds is the icon's place, or empty while it sits in the overflow", bounds == default || Screen.GetAllDisplays().Any(d => d.Bounds.X <= bounds.X && bounds.X < d.Bounds.X + d.Bounds.Width), bounds);

    // Version 0 callbacks, as Electron leaves the icon: the id in wParam, the message in lParam; events arrive posted.
    var events = new List<string>();
    TrayEventArgs? clickArgs = null, doubleArgs = null;
    tray.Clicked += (_, e) => { events.Add("click"); clickArgs = e; };
    tray.DoubleClicked += (_, e) => { events.Add("double"); doubleArgs = e; };
    tray.MiddleClicked += (_, _) => events.Add("middle");
    tray.RightClicked += (_, _) => events.Add("right");
    tray.MouseMoved += (_, _) => events.Add("move");
    tray.MouseEntered += (_, _) => events.Add("enter");
    tray.MouseLeft += (_, _) => events.Add("leave");
    tray.BalloonShown += (_, _) => events.Add("balloon-show");
    tray.BalloonClicked += (_, _) => events.Add("balloon-click");
    tray.BalloonClosed += (_, _) => events.Add("balloon-closed");
    var savedCursor = WindowsShell.CursorPos();
    var primary = Screen.GetPrimaryDisplay().Bounds;
    var away = Screen.DipToScreenPoint(new Point(primary.X + primary.Width / 2, primary.Y + primary.Height / 2));
    WindowsShell.MoveCursor(away.X, away.Y);
    string[] expected = ["click", "double", "middle", "middle", "right", "right", "balloon-show", "balloon-click", "balloon-closed"];
    // WM_L/M/RBUTTON(DOWN|DBLCLK), WM_CONTEXTMENU, NIN_KEYSELECT, NIN_BALLOON*, WM_LBUTTONUP.
    foreach (var message in new uint[] { 0x0201, 0x0203, 0x0207, 0x0209, 0x0204, 0x007B, 0x0206, 0x0401, 0x0402, 0x0405, 0x0404, 0x0403, 0x0202 })
        WindowsShell.TrayCallback(host, Id, message);
    Check("the shell's callbacks raise Electron's events (presses, not releases; no right double click, key select or balloon hide)",
        Eventually(() => events.Count >= expected.Length, step) && events.SequenceEqual(expected), string.Join(",", events));
    Check("a click carries the bounds and the pointer; a double click the bounds only",
        clickArgs is { } c && doubleArgs is { } d && c.Position == Screen.ScreenToDipPoint(new Point(away.X, away.Y)) && c.Bounds == tray.GetBounds() && d.Position == default(Point) && d.Bounds == c.Bounds,
        $"{clickArgs?.Position} {clickArgs?.Bounds} {doubleArgs?.Position}");

    // Hover, as Electron's MouseEnteredExitedDetector: a move counts only inside the bounds; leaving is polled every 250 ms.
    events.Clear();
    WindowsShell.TrayCallback(host, Id, 0x0200 /* WM_MOUSEMOVE */);
    await Task.Delay(600);
    Check("a move reported while the pointer is off the icon raises nothing", events.Count == 0, string.Join(",", events));
    bounds = tray.GetBounds();
    if (bounds == default) Check("entering and leaving the icon raise MouseEntered once, then MouseLeft (skipped: the icon is in the overflow)", true);
    else
    {
        var centre = Screen.DipToScreenPoint(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        WindowsShell.MoveCursor(centre.X, centre.Y);
        WindowsShell.TrayCallback(host, Id, 0x0200);
        WindowsShell.TrayCallback(host, Id, 0x0200);
        var entered = Eventually(() => events.Count >= 3, step) && events.SequenceEqual(["enter", "move", "move"]);
        WindowsShell.MoveCursor(away.X, away.Y);
        Check("entering and leaving the icon raise MouseEntered once, then MouseLeft",
            entered && Eventually(() => events.Count >= 4, step) && events.SequenceEqual(["enter", "move", "move", "leave"]), string.Join(",", events));
    }
    WindowsShell.MoveCursor(savedCursor.X, savedCursor.Y);

    // The context menu: a Chromium Views menu, as Electron's, read back through UI Automation.
    var shown = false;
    var menu = new Menu
    {
        new MenuItem { Label = "&Show", Accelerator = "CmdOrCtrl+S", Click = (_, _, _) => shown = true },
        new MenuItem { Type = MenuItemType.Separator },
        new MenuItem { Label = "Pinned", Type = MenuItemType.Checkbox, Checked = true },
        new MenuItem { Label = "Off", Enabled = false },
        new MenuItem { Label = "Hidden", Visible = false },
        new MenuItem { Label = "More", Submenu = new Menu { new MenuItem { Label = "Deep" } } },
    };
    tray.SetContextMenu(menu);
    events.Clear();
    WindowsShell.TrayCallback(host, Id, 0x0204 /* WM_RBUTTONDOWN */);
    nint menuWindow = 0;
    Check("a right press opens the context menu, a Views menu, instead of raising RightClicked",
        Eventually(() => (menuWindow = WindowsShell.ViewsMenuWindow()) != 0, step) && WindowsShell.MenuWindow() == 0 && !events.Contains("right"), string.Join(",", events));
    var names = menuWindow != 0 ? WindowsShell.ElementNames(menuWindow).Where(n => n.Length > 0).ToList() : [];
    Check("the menu has the visible items and accelerators of Oriel's Menu", names.SequenceEqual(["Show Ctrl+S", "Pinned", "Off", "More"]), string.Join(" | ", names));
    WindowsShell.PostKey(menuWindow, 0x28 /* VK_DOWN */);
    WindowsShell.PostKey(menuWindow, 0x0D /* VK_RETURN */);
    Check("choosing an item runs its click", Eventually(() => shown && WindowsShell.ViewsMenuWindow() == 0, step), $"shown={shown} open={WindowsShell.ViewsMenuWindow() != 0}");
    var popped = Task.Run(() => tray.PopUpContextMenu(position: new Point(200, 200)));
    Check("PopUpContextMenu opens it from code and returns while it is open", popped.Wait(step) && Eventually(() => WindowsShell.ViewsMenuWindow() != 0, step));
    Check("other Tray calls answer while the menu is open", Task.Run(() => tray.GetTitle()).Wait(step) && WindowsShell.ViewsMenuWindow() != 0);
    tray.CloseContextMenu();
    Check("CloseContextMenu closes it", Eventually(() => WindowsShell.ViewsMenuWindow() == 0, step));

    // Explorer restarting broadcasts TaskbarCreated; the icon comes back as Electron's ResetIcon re-adds it: image, no tooltip.
    tray.SetToolTip("Oriel tray test");
    await Task.Delay(500);
    var tipShown = WindowsShell.TaskbarNames().Contains("Oriel tray test");
    WindowsShell.NotifyIcon(host, Id, delete: true);
    var goneBehindOurBack = !WindowsShell.NotifyIcon(host, Id, delete: false);
    WindowsShell.SendRegistered(host, "TaskbarCreated");
    await Task.Delay(500);
    var tipAfter = WindowsShell.TaskbarNames().Contains("Oriel tray test");
    Check("TaskbarCreated adds the icon again", goneBehindOurBack && WindowsShell.NotifyIcon(host, Id, delete: false), goneBehindOurBack);
    // The notification area names a visible icon by its tooltip; an icon in the overflow has no name to read.
    Check(tipShown ? "TaskbarCreated drops the tooltip, as Electron" : "TaskbarCreated drops the tooltip, as Electron (skipped: the icon is in the overflow)", !tipShown || !tipAfter, $"before={tipShown} after={tipAfter}");

    // As Electron, a refused icon is logged, not thrown, and a GUID in use is only warned about: the first icon keeps it.
    var refused = new StringWriter();
    var stderrBefore = Console.Error;
    Console.SetError(refused);
    var guid = Guid.NewGuid();
    Tray? first = null, second = null;
    var threw = false;
    var firstLog = "";
    try { first = new Tray(Square(0, 0, 255), guid); firstLog = refused.ToString(); second = new Tray(Square(0, 0, 255), guid); }
    catch (Exception) { threw = true; }
    Console.SetError(stderrBefore);
    var log = refused.ToString();
    Check("a GUID already in use is warned about, the shell's refusal logged, nothing thrown, and the first icon keeps it",
        !threw && second is not null && !firstLog.Contains("Guid already in use.") && log.Contains("Guid already in use.") && log.Contains("Unable to create status tray icon.") && WindowsShell.NotifyIcon(host, Id + 1, delete: false, guid),
        $"threw={threw} log=[{log.Trim()}]");
    first?.Destroy();
    second?.Destroy();

    // A balloon is a toast on Windows 11; the shell reports it shown unless notifications are off or quiet hours hold it.
    events.Clear();
    tray.DisplayBalloon(new DisplayBalloonOptions { Title = "Oriel", Content = "Tray balloon test", IconType = BalloonIconType.Info, NoSound = true });
    Check("DisplayBalloon shows a balloon (BalloonShown)", Eventually(() => events.Contains("balloon-show"), TimeSpan.FromSeconds(8)), string.Join(",", events));
    tray.RemoveBalloon();
    tray.SetImage(Square(0, 200, 0));
    Check("SetImage keeps the icon in place", WindowsShell.NotifyIcon(host, Id, delete: false));
    Check("the macOS-only title and double-click setting read back as Electron's Windows defaults", Ok(() => { tray.SetTitle("t"); tray.SetIgnoreDoubleClickEvents(true); }) && tray.GetTitle() == "" && !tray.GetIgnoreDoubleClickEvents(),
        $"{tray.GetTitle()} {tray.GetIgnoreDoubleClickEvents()}");
    tray.Destroy();
    Check("Destroy removes the icon", tray.IsDestroyed() && !WindowsShell.NotifyIcon(host, Id, delete: false) && tray.GetBounds() == default);
}

static bool Ok(Action action)
{
    action();
    return true;
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
        WindowsShell.ClickThumbButton(hwnd, 40002);
        WindowsShell.ClickThumbButton(hwnd, 40001);
        Check("SetThumbarButtons adds buttons whose clicks reach their handlers", addedButtons && clicks.SequenceEqual(["stop", "play"]), $"{addedButtons} {string.Join(",", clicks)}");
        // A hook sees WM_COMMAND first; its exception must not keep Oriel's own handling from the click.
        clicks.Clear();
        w.HookWindowMessage(0x0111 /* WM_COMMAND */, (_, _) => throw new InvalidOperationException("command hook threw"));
        Console.SetError(TextWriter.Null);
        WindowsShell.ClickThumbButton(hwnd, 40001);
        Console.SetError(stderr);
        w.UnhookAllWindowMessages();
        Check("a throwing WM_COMMAND hook does not stop the thumbar click", clicks.SequenceEqual(["play"]), string.Join(",", clicks));
        clicks.Clear();
        var updated = w.SetThumbarButtons([new ThumbarButton { Icon = redIcon, Click = () => clicks.Add("next") }]);
        WindowsShell.ClickThumbButton(hwnd, 40001);
        WindowsShell.ClickThumbButton(hwnd, 40002);
        Check("a second SetThumbarButtons updates them; a removed button's handler is gone", updated && clicks.SequenceEqual(["next"]), $"{updated} {string.Join(",", clicks)}");
        Check("SetThumbarButtons refuses more than seven", !w.SetThumbarButtons([.. Enumerable.Range(0, 8).Select(_ => new ThumbarButton { Icon = green })]));
        // Electron's ids are 40001 + slot; THBN_CLICKED for another id is not a thumbar click.
        clicks.Clear();
        WindowsShell.ClickThumbButton(hwnd, 0);
        WindowsShell.ClickThumbButton(hwnd, 40000);
        Check("thumbar clicks route by Electron's ids (40001 + slot) only", clicks.Count == 0, string.Join(",", clicks));
        // Electron replaces the callbacks before a bad flag stops the call: earlier buttons take their new handlers.
        var badFlag = w.SetThumbarButtons([
            new ThumbarButton { Icon = green, Click = () => clicks.Add("new") },
            new ThumbarButton { Icon = green, Flags = (ThumbarButtonFlags)32, Click = () => clicks.Add("bad") },
        ]);
        WindowsShell.ClickThumbButton(hwnd, 40001);
        WindowsShell.ClickThumbButton(hwnd, 40002);
        Check("SetThumbarButtons with an unknown flag fails after replacing the earlier handlers", !badFlag && clicks.SequenceEqual(["new"]), $"{badFlag} {string.Join(",", clicks)}");
        clicks.Clear();
        var cleared = w.SetThumbarButtons([]);
        WindowsShell.ClickThumbButton(hwnd, 40001);
        Check("SetThumbarButtons([]) removes them", cleared && clicks.Count == 0, $"{cleared} {string.Join(",", clicks)}");
        w.Destroy();
        await Task.Delay(300);
    }
    Directory.Delete(dir, recursive: true);
}
// Windows PowerShell 5.1 reads Oriel's work back through .NET Framework and WinRT; stdout is base64 UTF-8.
static string PowerShell(string script)
{
    var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand "
        + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; $r = & { " + script + " }; [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$r))")))
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEndAsync();
    var error = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(30000)) { p.Kill(); return "timeout"; }
    try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(output.Result.Trim())); }
    catch (FormatException) { return "error: " + error.Result.Trim(); }
}

// Read back independently: Windows PowerShell unwraps os_crypt's key from CEF's Local State file with ProtectedData,
// and .NET's own AesGcm opens and seals the "v10" format.
async Task SafeStorageAsync()
{
    const string secret = "Oriel secret ✓ 日本";
    const string unwrap = "Add-Type -AssemblyName System.Security; $k = [Convert]::FromBase64String((Get-Content -Raw -Encoding UTF8 -LiteralPath '{0}' | ConvertFrom-Json).os_crypt.encrypted_key); if ([Text.Encoding]::ASCII.GetString($k, 0, 5) -ne 'DPAPI') {{ 'no DPAPI header' }} else {{ [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Unprotect([byte[]]$k[5..($k.Length - 1)], $null, 'CurrentUser')) }}";
    var localState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oriel.WindowTest", "Local State");
    static byte[]? FromBase64(string s) { try { return Convert.FromBase64String(s); } catch (FormatException) { return null; } }
    static byte[] Seal(byte[] key, string text)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(text);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var sealedText = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16)) aes.Encrypt(nonce, plain, sealedText, tag);
        return [.. "v10"u8, .. nonce, .. sealedText, .. tag];
    }
    static string Open(byte[] key, byte[] data)
    {
        if (data.Length < 31 || data[0] != 'v' || data[1] != '1' || data[2] != '0') return "not v10";
        var plain = new byte[data.Length - 31];
        try
        {
            using var aes = new System.Security.Cryptography.AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(3, 12), data.AsSpan(15, plain.Length), data.AsSpan(data.Length - 16), plain);
        }
        catch (System.Security.Cryptography.CryptographicException e) { return e.GetType().Name; }
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    // CEF creates the key at startup; read it before Oriel touches it, so a key Oriel replaced shows up below however
    // late CEF writes the file.
    var unwrapped = "";
    byte[]? key = null;
    Check("Local State holds os_crypt's DPAPI-wrapped 32-byte key (os_crypt.encrypted_key)",
        Eventually(() => (key = FromBase64(unwrapped = PowerShell(string.Format(unwrap, localState)))) is { Length: 32 }, step), unwrapped);
    key ??= new byte[32];
    Check("SafeStorage.IsEncryptionAvailable", SafeStorage.IsEncryptionAvailable());

    byte[] encrypted;
    try { encrypted = SafeStorage.EncryptString(secret); }
    catch (InvalidOperationException) { encrypted = []; }
    var opened = Open(key, encrypted);
    Check("EncryptString is os_crypt's v10 + nonce + AES-256-GCM under the Local State key", opened == secret && encrypted.Length == 31 + System.Text.Encoding.UTF8.GetByteCount(secret), opened);
    string decrypted;
    try { decrypted = SafeStorage.DecryptString(Seal(key, "from AesGcm ✓")); }
    catch (Exception e) { decrypted = e.GetType().Name + ": " + e.Message; }
    Check("DecryptString opens v10 data sealed with the Local State key", decrypted == "from AesGcm ✓", decrypted);

    var raw = FromBase64(PowerShell("Add-Type -AssemblyName System.Security; [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('x'), $null, 'CurrentUser'))")) ?? [1];
    var refusal = "";
    try { SafeStorage.DecryptString(raw); }
    catch (InvalidOperationException e) { refusal = e.Message; }
    Check("DecryptString refuses raw DPAPI data as Electron does", refusal == "Error while decrypting the ciphertext provided to safeStorage.decryptString. Ciphertext does not appear to be encrypted.", refusal);

    string asyncOpened;
    SafeStorageDecryptResult? asyncResult = null;
    try
    {
        asyncOpened = Open(key, await SafeStorage.EncryptStringAsync("async ✓"));
        asyncResult = await SafeStorage.DecryptStringAsync(Seal(key, "sealed ✓"));
    }
    catch (InvalidOperationException e) { asyncOpened = e.Message; }
    Check("EncryptStringAsync and DecryptStringAsync use the same key and format", asyncOpened == "async ✓" && asyncResult == new SafeStorageDecryptResult(false, "sealed ✓"), $"{asyncOpened} | {asyncResult}");
}

#pragma warning disable CA1416 // Called only on Windows.
// Toasts need the AppUserModelID registered; an AppUserModelId key with a DisplayName does what a Start-menu shortcut
// does. Shown toasts are read back from the Action Center history through Windows PowerShell's WinRT projection, the
// activator's shortcut through the shell's property system, and its registration from the registry.
async Task NotificationAsync()
{
    var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductName ?? "";
    var atStartup = WindowsShell.ProcessAppUserModelId();
    var defaultId = app.AppUserModelId;
    var afterUse = WindowsShell.ProcessAppUserModelId();
    Check("the AppUserModelID is unset at startup; first use sets electron.app.{product name}",
        atStartup is null && defaultId == "electron.app." + product && afterUse == defaultId, $"{atStartup} | {defaultId} | {afterUse}");

    var id = "Oriel.WindowTest.Toast." + Environment.ProcessId;
    var key = @"Software\Classes\AppUserModelId\" + id;
    var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), product + ".lnk");
    using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key)) k.SetValue("DisplayName", "Oriel WindowTest");
    const string history = "$null = [Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]; ([Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory('{0}') | ForEach-Object {{ $_.Tag + '/' + $_.Group + ' ' + $_.Content.GetXml() }}) -join '|'";
    const string shortcut = "$f = (New-Object -ComObject Shell.Application).NameSpace('{0}').ParseName('{1}'); $l = (New-Object -ComObject WScript.Shell).CreateShortcut('{2}'); if ($f) {{ $l.TargetPath + '|' + $l.Arguments + '|' + $l.Description + '|' + $l.WorkingDirectory + '|' + $f.ExtendedProperty('System.AppUserModel.ID') + '|' + $f.ExtendedProperty('System.AppUserModel.ToastActivatorCLSID') }} else {{ 'missing' }}";
    var clsid = "";
    try
    {
        app.SetAppUserModelId(id);
        Check("SetAppUserModelId sets the process's explicit AppUserModelID", WindowsShell.ProcessAppUserModelId() == id && app.AppUserModelId == id, WindowsShell.ProcessAppUserModelId());
        Check("Notification.IsSupported", Notification.IsSupported());

        var props = "";
        Check("the first notification writes Electron's Start-menu shortcut: the exe, its product name, the AppUserModelID and activator CLSID",
            Eventually(() => (props = PowerShell(string.Format(shortcut, Path.GetDirectoryName(lnk), Path.GetFileName(lnk), lnk)))
                == $"{Environment.ProcessPath}||{product}|{Path.GetDirectoryName(Environment.ProcessPath)}|{id}|{clsid = app.ToastActivatorClsid!}", step), props);
        string? server = null, name = null;
        object? custom = null;
        Check("... and registers that CLSID's LocalServer32 under HKCU",
            Eventually(() =>
            {
                using var c = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\" + clsid);
                using var s = c?.OpenSubKey("LocalServer32");
                (name, custom, server) = (c?.GetValue(null) as string, c?.GetValue("CustomActivator"), s?.GetValue(null) as string);
                return name == "Electron Notification Activator" && custom is 1 && server == Environment.ProcessPath;
            }, step), $"{name} | {custom} | {server}");

        var tag = "oriel-toast-" + Guid.NewGuid().ToString("N")[..8];
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ActivationArguments? activation = null;
        Notification.HandleActivation(a => activation = a);
        var n = Oriel.Cef.CefThread.InvokeOnUi(() => new Notification
        {
            Id = tag, Title = "Oriel toast", Body = "Body text", Silent = true, TimeoutType = "never", Icon = OverlayPng(),
            HasReply = true, ReplyPlaceholder = "Say", Actions = [new() { Text = "Open" }, new() { Type = "selection", Text = "Pick", Items = ["a", "b", "c"] }],
        });
        n.Shown += (_, _) => events.Enqueue("shown");
        n.Closed += (_, r) => events.Enqueue("closed " + r);
        n.Failed += (_, e) => events.Enqueue("failed " + e);
        n.Action += (_, e) => events.Enqueue($"action {e.ActionIndex} {e.SelectionIndex}");
        n.Reply += (_, r) => events.Enqueue("reply " + r);
        var shownDuringShow = Oriel.Cef.CefThread.InvokeOnUi(() => { n.Show(); return events.Contains("shown"); });
        Check("Show raises Shown after it returns, posted as Electron posts it", !shownDuringShow && Eventually(() => events.Contains("shown"), step), $"{shownDuringShow} {string.Join(",", events)}");
        var shown = "";
        Check("the toast reaches the Action Center tagged with its Id in group Notifications, with Electron's reminder, reply and action XML",
            Eventually(() => (shown = PowerShell(string.Format(history, id))).Contains($"{tag}/Notifications <toast scenario=\"reminder\">")
                && shown.Contains("<text>Oriel toast</text><text>Body text</text>")
                && shown.Contains("<actions><action activationType=\"system\" arguments=\"dismiss\" content=\"Close\"/><input id=\"reply\" type=\"text\" placeHolderContent=\"Say\"/>")
                && shown.Contains($"<action activationType=\"foreground\" arguments=\"type=action&amp;action=0&amp;tag={tag}\" content=\"Open\"/>")
                && shown.Contains($"<action activationType=\"foreground\" arguments=\"type=action&amp;action=1&amp;tag={tag}\" content=\"Pick\" hint-inputId=\"selection1\"/>")
                && shown.Contains($"<action activationType=\"foreground\" arguments=\"type=reply&amp;tag={tag}\" content=\"Reply\" hint-inputId=\"reply\"/></actions>"), step), shown);
        var icon = System.Text.RegularExpressions.Regex.Match(shown, "<image id=\"1\" placement=\"appLogoOverride\" hint-crop=\"none\" src=\"([^\"]+)\"/>").Groups[1].Value;
        Check("the icon is a PNG copy in a scoped_dir temporary directory, by plain path",
            icon.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(icon))!.StartsWith("scoped_dir", StringComparison.Ordinal) && File.Exists(icon) && File.ReadAllBytes(icon).AsSpan().StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G']), icon);

        Check("the activator's class object is registered", Eventually(() => WindowsToastActivator.IsRegistered, step));
        var hr = WindowsShell.ActivateToast(Guid.Parse(clsid), id, $"type=action&action=1&tag={tag}", ("selection1", "2"));
        Check("Windows' activation of the CLSID (local server) with a selection raises Action (1, 2)",
            hr == 0 && Eventually(() => events.Contains("action 1 2"), step), $"0x{hr:X8} {string.Join(",", events)}");
        hr = WindowsShell.ActivateToast(Guid.Parse(clsid), id, $"type=reply&tag={tag}", ("reply", "typed ✓"));
        Check("an activation with a reply raises Reply with its text", hr == 0 && Eventually(() => events.Contains("reply typed ✓"), step), $"0x{hr:X8} {string.Join(",", events)}");
        Check("HandleActivation hears every activation with its details",
            activation is { Type: "reply", Reply: "typed ✓" } && activation.UserInputs?.GetValueOrDefault("reply") == "typed ✓", activation?.ToString());

        Oriel.Cef.CefThread.InvokeOnUi(() => { n.Close(); return 0; });
        Check("Close removes it from the Action Center", Eventually(() => !(shown = PowerShell(string.Format(history, id))).Contains(tag), step), shown);
        await Task.Delay(1000);
        Check("Close raises no Closed, as Electron's close() on Windows", !events.Any(e => e.StartsWith("closed", StringComparison.Ordinal)) && !n.IsShown, string.Join(",", events));

        var bad = new Notification { ToastXml = "<toast><visual>" };
        var failed = "";
        bad.Failed += (_, e) => failed = e;
        var failedDuringShow = Oriel.Cef.CefThread.InvokeOnUi(() => { bad.Show(); return failed; });
        Check("malformed ToastXml raises Failed with Electron's text after Show returns, and is not shown",
            failedDuringShow.Length == 0 && Eventually(() => failed.StartsWith("XML: Invalid XML, ERROR  (HRESULT: ", StringComparison.Ordinal), step) && !bad.IsShown, $"[{failedDuringShow}] {failed}");

        var custom2 = new Notification { ToastXml = $"<toast><visual><binding template=\"ToastGeneric\"><text>{tag} custom</text></binding></visual><audio silent=\"true\"/></toast>" };
        Oriel.Cef.CefThread.InvokeOnUi(() => { custom2.Show(); return 0; });
        Check("ToastXml is shown as given", Eventually(() => (shown = PowerShell(string.Format(history, id))).Contains(tag + " custom"), step), shown);
        Oriel.Cef.CefThread.InvokeOnUi(() => { custom2.Close(); return 0; });

        var imaged = Oriel.Cef.CefThread.InvokeOnUi(() => new Notification { Id = tag + "-img", Title = "Oriel image", Silent = true, IconImage = NativeImage.CreateFromPath(OverlayPng()) });
        Oriel.Cef.CefThread.InvokeOnUi(() => { imaged.Show(); return 0; });
        var imageSrc = "";
        Check("an IconImage (NativeImage) is shown as a PNG copy in the scoped_dir temporary directory",
            Eventually(() =>
            {
                shown = PowerShell(string.Format(history, id));
                imageSrc = System.Text.RegularExpressions.Regex.Match(shown, $"{tag}-img/Notifications <toast>.*?<image id=\"1\" placement=\"appLogoOverride\" hint-crop=\"none\" src=\"([^\"]+)\"/>").Groups[1].Value;
                return imageSrc.Length > 0;
            }, step)
            && Path.GetFileName(Path.GetDirectoryName(imageSrc))!.StartsWith("scoped_dir", StringComparison.Ordinal) && File.Exists(imageSrc)
            && File.ReadAllBytes(imageSrc).AsSpan().StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G']), imageSrc.Length > 0 ? imageSrc : shown);
        Oriel.Cef.CefThread.InvokeOnUi(() => { imaged.Close(); return 0; });
        await Task.Delay(500);
    }
    finally
    {
        PowerShell($"$null = [Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]; [Windows.UI.Notifications.ToastNotificationManager]::History.Clear('{id}')");
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        File.Delete(lnk);
        if (app.ToastActivatorClsid is { } registered) Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + registered, throwOnMissingSubKey: false);
    }
}
#pragma warning restore CA1416

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
        "  askSync: (v) => JSON.stringify(ipcRenderer.sendSync('sync-test', v)),",
        "  listen: () => ipcRenderer.on('to-frame', (_e, v) => document.title = 'got:' + v),",
        "  listenOnce: () => ipcRenderer.once('count', () => document.title = 'once:' + (++onceCount)),",
        "  listenThenOff: () => { ipcRenderer.on('off-test', offListener); ipcRenderer.off('off-test', offListener); },",
        "  removeAll: () => ipcRenderer.removeAllListeners('to-frame'),",
        "});",
        // Electron's contextBridge shapes: nested and returned functions, promises, cloneable types, nodes, errors.
        "oriel.contextBridge.exposeInMainWorld('cb', {",
        "  sync: (x) => x * 2, nested: { deeper: { fn: () => ({ returnData: 123 }) } }, flags: ['a', 'b'],",
        "  asyncFn: async () => 123, rejecting: () => Promise.reject(new Error('whoops')), thrower: () => { throw new TypeError('bad'); },",
        "  echo: (v) => v, callBack: (f) => f(20) + 1, makeAdder: (n) => (m) => n + m, tagOf: (el) => el.tagName, title: () => document.querySelector('title'),",
        "});",
        "oriel.contextBridge.exposeInMainWorld('bareFn', (v) => 'bare:' + v);",
        "oriel.contextBridge.exposeInMainWorld('bareValue', 42);",
        "let dupe = 'none'; try { oriel.contextBridge.exposeInMainWorld('cb', {}); } catch (e) { dupe = e.message; }",
        "oriel.contextBridge.exposeInMainWorld('dupeError', dupe);",
        "oriel.contextBridge.exposeInMainWorld('execResult', oriel.contextBridge.executeInMainWorld({ func: (a, b) => typeof window.cb + ':' + (a + b), args: [2, 3] }));"));
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

    // focusedFrame: the frame that last took focus, at any depth.
    host.Focus();
    const string FocusInput = "document.body.insertAdjacentHTML('beforeend', '<input id=f>'); document.getElementById('f').focus(); 1";
    await a.ExecuteJavaScriptAsync(FocusInput);
    Check("FocusedFrame is the iframe that took focus", Eventually(() => host.WebContents.FocusedFrame == a, step), host.WebContents.FocusedFrame?.Name);
    var nested = a.Frames[0];
    await nested.ExecuteJavaScriptAsync(FocusInput);
    Check("FocusedFrame follows focus into a nested iframe", Eventually(() => host.WebContents.FocusedFrame == nested, step), host.WebContents.FocusedFrame?.Name);
    await main.ExecuteJavaScriptAsync(FocusInput);
    Check("FocusedFrame returns to the main frame", Eventually(() => host.WebContents.FocusedFrame == main, step), host.WebContents.FocusedFrame?.Name);
    var reloaded = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: focus reload", Width = 300, Height = 200 });
    Fires(h => reloaded.WebContents.DidFinishLoad += h, h => reloaded.WebContents.DidFinishLoad -= h, () => reloaded.WebContents.LoadFile(page), step);
    reloaded.Focus();
    Eventually(() => reloaded.WebContents.MainFrame?.Frames.Count == 1, step);
    await reloaded.WebContents.MainFrame!.Frames[0].ExecuteJavaScriptAsync(FocusInput);
    Eventually(() => reloaded.WebContents.FocusedFrame?.Name == "a", step);
    Fires(h => reloaded.WebContents.DidFinishLoad += h, h => reloaded.WebContents.DidFinishLoad -= h, () => reloaded.WebContents.LoadFile(page), step);
    Check("a page that loads into a focused window is the focused frame", Eventually(() => reloaded.WebContents.FocusedFrame == reloaded.WebContents.MainFrame, step),
        reloaded.WebContents.FocusedFrame?.Name);
    reloaded.Destroy();

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

    // Electron's dispatch order: the page's ipc-message, then the frame's ipc, the page's ipc, ipcMain.
    var order = new List<string>();
    void Heard(string who) { lock (order) order.Add(who); }
    string Order() { lock (order) return string.Join(",", order); }
    EventHandler<IpcMessageEventArgs> onIpcMessage = (_, m) => Heard($"ipc-message:{m.Channel}:{(m.Event.SenderFrame == a ? "a" : "main")}");
    Action<IpcMainEvent> inFrame = _ => Heard("frame");
    Action<IpcMainEvent> inContents = _ => Heard("contents");
    Action<IpcMainEvent> inGlobal = _ => Heard("ipcMain");
    host.WebContents.IpcMessage += onIpcMessage;
    a.Ipc.On("frame-test", inFrame);
    host.WebContents.Ipc.On("frame-test", inContents);
    IpcMain.On("frame-test", inGlobal);
    await a.ExecuteJavaScriptAsync("bridge.send(5); 1");
    Check("a message reaches ipc-message, then the frame's ipc, the page's, and ipcMain, as Electron's",
        Eventually(() => Order() == "ipc-message:frame-test:a,frame,contents,ipcMain", step), Order());
    lock (order) order.Clear();
    await main.ExecuteJavaScriptAsync("bridge.send(6); 1");
    Check("a frame's ipc hears only that frame", Eventually(() => Order() == "ipc-message:frame-test:main,contents,ipcMain", step), Order());
    host.WebContents.IpcMessage -= onIpcMessage;
    a.Ipc.RemoveListener("frame-test", inFrame);
    host.WebContents.Ipc.RemoveListener("frame-test", inContents);
    IpcMain.RemoveListener("frame-test", inGlobal);

    // invoke: the first of the frame's, the page's and ipcMain's handlers answers.
    IpcMain.Handle("ask", _ => System.Text.Json.Nodes.JsonValue.Create("ipcMain"));
    host.WebContents.Ipc.Handle("ask", _ => System.Text.Json.Nodes.JsonValue.Create("contents"));
    a.Ipc.Handle("ask", _ => System.Text.Json.Nodes.JsonValue.Create("frame"));
    var answers = new List<string?>
    {
        (await a.ExecuteJavaScriptAsync("bridge.ask()"))?.ToString(),
        (await main.ExecuteJavaScriptAsync("bridge.ask()"))?.ToString(),
    };
    host.WebContents.Ipc.RemoveHandler("ask");
    answers.Add((await main.ExecuteJavaScriptAsync("bridge.ask()"))?.ToString());
    a.Ipc.RemoveHandler("ask");
    answers.Add((await a.ExecuteJavaScriptAsync("bridge.ask()"))?.ToString());
    IpcMain.RemoveHandler("ask");
    Check("invoke is answered by the frame's handler, else the page's, else ipcMain's", answers.SequenceEqual(["frame", "contents", "ipcMain", "ipcMain"]), string.Join(",", answers));

    // contextBridge, from the page's side, as Electron's.
    async Task<string?> Page(string code) => (await main.ExecuteJavaScriptAsync(code))?.ToString();
    Check("a bridged function returns its value at once, not a promise", await Page("typeof cb.sync(21) + ':' + cb.sync(21)") == "number:42", await Page("typeof cb.sync(21)"));
    Check("functions nested anywhere in the API are proxied, and the API is copied and frozen",
        await Page("cb.nested.deeper.fn().returnData === 123 && Object.isFrozen(cb) && Object.isFrozen(cb.nested.deeper) && Object.isFrozen(cb.flags) && cb.flags.join() === 'a,b'") == "true");
    Check("promises cross both ways, resolved and rejected, with their Error", await Page("(async () => { const p = cb.asyncFn(); const v = await p; let m; try { await cb.rejecting(); } catch (e) { m = (e instanceof Error) + ':' + e.message; } return (p instanceof Promise) + ':' + v + ':' + m; })()") == "true:123:true:whoops",
        await Page("(async () => { try { await cb.rejecting(); } catch (e) { return String(e); } })()"));
    Check("a thrown error reaches the caller as an Error of its kind", await Page("(() => { try { cb.thrower(); } catch (e) { return e.name + ':' + e.message + ':' + (e instanceof Error); } })()") == "TypeError:bad:true",
        await Page("(() => { try { cb.thrower(); } catch (e) { return String(e); } })()"));
    Check("cloneable types cross as themselves; undefined values stay and symbols are dropped",
        await Page("cb.echo(new Date(5)).getTime() === 5 && cb.echo(new Map([[1, 'a']])).get(1) === 'a' && cb.echo(new Uint8Array([1, 2]))[1] === 2 && 'u' in cb.echo({ u: undefined }) && Object.keys(cb.echo({ s: Symbol('x'), k: 1 })).join() === 'k'") == "true");
    Check("functions cross as arguments and as return values, called synchronously", await Page("cb.callBack((x) => x * 2) + ':' + cb.makeAdder(2)(3)") == "41:5", await Page("cb.callBack((x) => x * 2) + ':' + cb.makeAdder(2)(3)"));
    Check("DOM nodes cross as the same node", await Page("cb.tagOf(document.querySelector('iframe')) + ':' + (cb.title() === document.querySelector('title'))") == "IFRAME:true",
        await Page("cb.tagOf(document.querySelector('iframe')) + ':' + (cb.title() === document.querySelector('title'))"));
    Check("a bare function or value can be the API; an existing key refuses with Electron's error",
        await Page("bareFn('x') + ':' + bareValue + ':' + dupeError") == "bare:x:42:Cannot bind an API on top of an existing property on the window object", await Page("bareFn('x') + ':' + bareValue + ':' + dupeError"));
    Check("executeInMainWorld runs the function in the page's world and returns its result", await Page("execResult") == "object:5", await Page("execResult"));

    // sendSync: the page waits for returnValue; dispatch follows Electron's order; an unset reply errors once collected.
    WebFrameMain? syncSender = null;
    var syncOrder = new List<string>();
    void SyncHeard(string who) { lock (syncOrder) syncOrder.Add(who); }
    EventHandler<IpcMessageEventArgs> onSyncMessage = (_, m) => SyncHeard("ipc-message-sync:" + m.Channel);
    Action<IpcMainEvent> syncInFrame = _ => SyncHeard("frame");
    Action<IpcMainEvent> syncInContents = _ => SyncHeard("contents");
    Action<IpcMainEvent> answerSync = e => { SyncHeard("ipcMain"); syncSender = e.SenderFrame; e.ReturnValue = "got:" + e.Args[0].GetString(); };
    host.WebContents.IpcMessageSync += onSyncMessage;
    a.Ipc.On("sync-test", syncInFrame);
    host.WebContents.Ipc.On("sync-test", syncInContents);
    IpcMain.On("sync-test", answerSync);
    var syncAnswer = (await a.ExecuteJavaScriptAsync("bridge.askSync('x')"))?.ToString();
    string SyncOrder() { lock (syncOrder) return string.Join(",", syncOrder); }
    Check("ipcRenderer.sendSync returns the returnValue a listener sets, from the sending frame", syncAnswer == "\"got:x\"" && syncSender == a, $"{syncAnswer} {syncSender?.Name}");
    Check("sendSync reaches ipc-message-sync, then the frame's ipc, the page's and ipcMain, as Electron's", SyncOrder() == "ipc-message-sync:sync-test,frame,contents,ipcMain", SyncOrder());
    host.WebContents.IpcMessageSync -= onSyncMessage;
    a.Ipc.RemoveListener("sync-test", syncInFrame);
    host.WebContents.Ipc.RemoveListener("sync-test", syncInContents);
    IpcMain.RemoveListener("sync-test", answerSync);

    Action<IpcMainEvent> answerLater = e => Task.Delay(300).ContinueWith(_ => e.ReturnValue = "later");
    IpcMain.On("sync-test", answerLater);
    var waitStarted = DateTime.UtcNow;
    var lateAnswer = (await a.ExecuteJavaScriptAsync("bridge.askSync('y')"))?.ToString();
    var waited = DateTime.UtcNow - waitStarted;
    IpcMain.RemoveListener("sync-test", answerLater);
    Check("a returnValue set later still answers, with the page held until then", lateAnswer == "\"later\"" && waited >= TimeSpan.FromMilliseconds(250), $"{lateAnswer} after {waited.TotalMilliseconds:F0} ms");

    Action<IpcMainEvent> ignore = _ => { };
    IpcMain.On("sync-test", ignore);
    var unanswered = a.ExecuteJavaScriptAsync("bridge.askSync('z')");
    for (var i = 0; i < 50 && !unanswered.IsCompleted; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.WhenAny(unanswered, Task.Delay(100));
    }
    IpcMain.RemoveListener("sync-test", ignore);
    var neverSent = unanswered.IsCompleted ? (await unanswered)?.ToString() : "still waiting";
    Check("an unanswered sendSync gets Electron's error once the event is collected", neverSent == "{\"error\":\"reply was never sent\"}", neverSent);

    var forged = (await a.ExecuteJavaScriptAsync("(() => { const x = new XMLHttpRequest(); x.open('POST', 'oriel-ipc://sync/', false); x.send('{}'); return x.status; })()"))?.ToString();
    Check("the page's own scripts cannot use the sync channel without its isolated world's secret", forged == "400", forged);

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

async Task WebContentsLifecycleAsync(BrowserWindow win)
{
    // webContents.id and its lookups.
    var other = new BrowserWindow(new BrowserWindowOptions { Show = false });
    var otherId = other.WebContents.Id;
    Check("webContents ids are unique and start at 1", win.WebContents.Id >= 1 && otherId >= 1 && otherId != win.WebContents.Id, $"{win.WebContents.Id} {otherId}");
    Check("WebContents.FromId", WebContents.FromId(otherId) == other.WebContents && WebContents.FromId(-1) is null);
    Check("GetAllWebContents lists live pages", WebContents.GetAllWebContents().Contains(win.WebContents) && WebContents.GetAllWebContents().Contains(other.WebContents));
    win.Focus();
    Check("GetFocusedWebContents is the focused window's page", Eventually(() => WebContents.GetFocusedWebContents() == win.WebContents, step), WebContents.GetFocusedWebContents()?.Id);
    other.Destroy();
    Check("a destroyed page leaves FromId and GetAllWebContents",
        Eventually(() => WebContents.FromId(otherId) is null && !WebContents.GetAllWebContents().Any(w => w.Id == otherId), step));
    var next = new BrowserWindow(new BrowserWindowOptions { Show = false });
    Check("webContents ids are never reused", next.WebContents.Id > otherId, $"{next.WebContents.Id} > {otherId}");
    next.Destroy();

    // page-favicon-updated.
    var icon = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    var iconPage = Path.Combine(Path.GetTempPath(), "oriel-window-test-icon.html");
    File.WriteAllText(iconPage, $"<!doctype html><title>icon</title><link rel=icon href=\"{icon}\">");
    IReadOnlyList<string>? favicons = null;
    EventHandler<PageFaviconUpdatedEventArgs> onFavicon = (_, e) => favicons = e.Favicons;
    win.WebContents.PageFaviconUpdated += onFavicon;
    _ = win.WebContents.LoadFile(iconPage);
    Check("PageFaviconUpdated carries the page's icon URLs", Eventually(() => favicons is { Count: 1 } && favicons[0] == icon, step), favicons is null ? "none" : string.Join(",", favicons)[..Math.Min(40, string.Join(",", favicons).Length)]);
    win.WebContents.PageFaviconUpdated -= onFavicon;

    // before-input-event: key fields, cancel, and Char events not reported.
    await Js(win, "document.body.innerHTML = '<input id=k>'; window.keys = []; document.getElementById('k').focus(); addEventListener('keydown', e => keys.push(e.key))");
    win.Focus();
    var inputs = new System.Collections.Concurrent.ConcurrentQueue<KeyInput>();
    EventHandler<BeforeInputEventArgs> onInput = (_, e) =>
    {
        inputs.Enqueue(e.Input);
        if (e.Input.Key is "q" or "F12") e.Cancel = true;
    };
    win.WebContents.BeforeInputEvent += onInput;
    var sent = win.WebContents;
    sent.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "A") { Modifiers = InputModifiers.Shift | InputModifiers.Control });
    sent.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "A") { Modifiers = InputModifiers.Shift | InputModifiers.Control });
    sent.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "Left"));
    sent.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.Char, "x"));
    Check("BeforeInputEvent reports keyDown and keyUp, not Char", Eventually(() => inputs.Count >= 3, step) && inputs.Count == 3, inputs.Count);
    var down = inputs.ElementAtOrDefault(0);
    var up = inputs.ElementAtOrDefault(1);
    var left = inputs.ElementAtOrDefault(2);
    Check("BeforeInputEvent: type, key, code and modifiers of a keyDown",
        down is { Type: KeyboardInputEventType.KeyDown, Key: "A", Code: "KeyA", Shift: true, Control: true, Alt: false, Meta: false, IsAutoRepeat: false, IsComposing: false, Location: 0 }, down);
    Check("BeforeInputEvent: a keyUp", up is { Type: KeyboardInputEventType.KeyUp, Key: "A", Code: "KeyA" }, up);
    Check("BeforeInputEvent: a named key", left is { Key: "ArrowLeft", Code: "ArrowLeft", Shift: false, Control: false }, left);
    Check("an uncancelled key reaches the page", Eventually(() => Js(win, "keys.join()").Result is "\"A,ArrowLeft\"", step), Js(win, "keys.join()").Result);
    sent.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "Q"));
    sent.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "Right"));
    Check("a cancelled key never reaches the page, the next one does", Eventually(() => Js(win, "keys.join()").Result is "\"A,ArrowLeft,ArrowRight\"", step), Js(win, "keys.join()").Result);
    Check("BeforeInputEvent still saw the cancelled key", inputs.Any(i => i.Key == "q"));
    PressF12(win);
    await Task.Delay(1500);
    Check("a cancelled F12 does not open DevTools", !win.WebContents.IsDevToolsOpened());
    win.WebContents.BeforeInputEvent -= onInput;
    PressF12(win);
    Check("F12 opens DevTools again once nothing cancels it", Eventually(() => win.WebContents.IsDevToolsOpened(), step));
    win.WebContents.CloseDevTools();
    Eventually(() => !win.WebContents.IsDevToolsOpened(), step);

    // render-process-gone: chrome://kill and chrome://crash, as a browser-initiated navigation.
    foreach (var url in new[] { "chrome://kill", "chrome://crash" })
    {
        var victim = new BrowserWindow(new BrowserWindowOptions { Show = false });
        _ = victim.LoadUrl("data:text/html,<p>victim</p>");
        await Task.Delay(1000);
        RenderProcessGoneEventArgs? gone = null;
        victim.WebContents.RenderProcessGone += (_, e) => gone = e;
        _ = victim.WebContents.LoadUrl(url).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        Check($"RenderProcessGone is raised by {url}", Eventually(() => gone is not null, TimeSpan.FromSeconds(15)), gone);
        Check($"RenderProcessGone reason for {url}", gone is { Reason: "killed" or "crashed", ExitCode: not 0 }, gone);
        victim.Destroy();
    }

    // unresponsive / responsive: Chromium's hang monitor never fires here (the page is under DevTools, which
    // suspends it, and DevTools-injected input is not tracked), so drive CEF's callbacks by hand.
    var probe = new BrowserWindow(new BrowserWindowOptions { Show = false });
    var unresponsive = 0;
    var responsive = 0;
    probe.WebContents.Unresponsive += (_, _) => unresponsive++;
    probe.WebContents.Responsive += (_, _) => responsive++;
    var (handled, waited) = HangProbe.Unresponsive(probe.WebContents);
    Check("the unresponsive callback is claimed and told to wait", handled == 1 && waited == 1, $"handled={handled} waited={waited}");
    Check("Unresponsive is raised", unresponsive == 1, unresponsive);
    HangProbe.Unresponsive(probe.WebContents);
    Check("a repeated report during one hang raises Unresponsive once", unresponsive == 1, unresponsive);
    HangProbe.Responsive(probe.WebContents);
    Check("Responsive is raised once the page answers", responsive == 1, responsive);
    HangProbe.Responsive(probe.WebContents);
    Check("Responsive without a hang is not raised", responsive == 1, responsive);
    HangProbe.Unresponsive(probe.WebContents);
    Check("a later hang raises Unresponsive again", unresponsive == 2, unresponsive);
    probe.Destroy();
}


static int FreePort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

async Task CommandLineAsync()
{
    Check("CommandLine: an appended switch is readable",
        app.CommandLine.HasSwitch("remote-debugging-port") && app.CommandLine.GetSwitchValue("remote-debugging-port") == debugPort.ToString());
    string? version;
    try
    {
        using var http = new HttpClient { Timeout = step };
        version = await http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/version");
    }
    catch (Exception e) { version = e.Message; }
    Check("CommandLine: Chromium acts on a switch appended before Run", version?.Contains("\"Browser\"") == true, version?[..Math.Min(80, version.Length)]);

    // What reaches Chromium, on a CEF command line of its own.
    var line = new CommandLine([]);
    line.RemoveSwitch("drop");
    line.AppendSwitch("added", "3");
    line.AppendSwitch("flag");
    line.AppendSwitch("gone");
    line.RemoveSwitch("gone");
    line.AppendSwitch("Mixed", "4");
    line.RemoveSwitch("back");
    line.AppendSwitch("back", "5");
    line.AppendArgument("extra.txt");
    var applied = CommandLineProbe.Apply([("keep", "1"), ("drop", "2")], line);
    Check("CommandLine: removals, switches and arguments reach Chromium's command line, in order",
        applied.Contains("--keep=1") && !applied.Contains("--drop") && applied.Contains("--added=3") && applied.Contains("--flag")
        && !applied.Contains("--gone") && applied.Contains("--mixed=4") && applied.Contains("--back=5") && applied.EndsWith("extra.txt"), applied);
}

// Crash and Hang, each in a process of its own; the hanging one also logs as ELECTRON_ENABLE_LOGGING=file asks.
async Task ProcessChildrenAsync()
{
    var crash = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
    crash.ArgumentList.Add(ProcessChild);
    crash.ArgumentList.Add("crash");
    using (var child = System.Diagnostics.Process.Start(crash)!)
    {
        var errors = child.StandardError.ReadToEndAsync();
        await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        // .NET reports a signal as 128 + its number; SIGSEGV is 11. An unhandled .NET exception would abort (134).
        var segv = OperatingSystem.IsWindows() ? unchecked((int)0xC0000005) : 128 + 11;
        Check("Process: Crash ends the process with a segmentation fault, as Electron's", child.ExitCode == segv,
            $"{child.ExitCode} {(await errors).Split('\n').FirstOrDefault()}");
    }

    var userData = Directory.CreateTempSubdirectory("oriel-process-child-").FullName;
    var hang = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
    hang.ArgumentList.Add(ProcessChild);
    hang.ArgumentList.Add("hang");
    hang.ArgumentList.Add(userData);
    hang.Environment["ELECTRON_ENABLE_LOGGING"] = "file";
    hang.Environment.Remove("ELECTRON_LOG_FILE");
    hang.Environment.Remove("ORIEL_LOG_FILE");
    using (var child = System.Diagnostics.Process.Start(hang)!)
    {
        _ = child.StandardError.ReadToEndAsync();
        var lines = new List<string>();
        try
        {
            while (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)) is { } read)
            {
                lines.Add(read);
                if (read.StartsWith("UI-", StringComparison.Ordinal)) break;
            }
        }
        catch (TimeoutException) { }
        try
        {
            Check("Process: Hang stops the UI thread for good", lines.Contains("UI-HUNG") && !child.HasExited, string.Join(",", lines));
            var log = Path.Combine(userData, "oriel_debug.log");
            // The hung child still has its log open for writing, which Windows refuses File.ReadAllText beside.
            string? text = null;
            if (File.Exists(log))
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                text = new StreamReader(stream).ReadToEnd();
            }
            Check("Logging: ELECTRON_ENABLE_LOGGING=file logs to oriel_debug.log in userData", text is not null, text?.Length);
            // Electron's line prefix: [pid:MMDD/HHMMSS.micros:SEVERITY:...]; Chromium's default adds the thread id.
            var first = text?.Split('\n').FirstOrDefault(l => l.StartsWith('[')) ?? "";
            Check("Logging: lines carry the process id and a timestamp only, as Electron's",
                System.Text.RegularExpressions.Regex.IsMatch(first, @"^\[\d+:\d{4}/\d{6}\.\d+:[A-Z]+:"), first);
        }
        finally
        {
            // Whatever the checks did, the hung child and its subprocesses must not outlive the run.
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
    }
    for (var attempt = 0; attempt < 40 && Directory.Exists(userData); attempt++)
    {
        try { Directory.Delete(userData, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(250); }
    }
}

static int ProcessChildMain(string mode, string userData)
{
    if (mode == "crash")
    {
        OrielProcess.Crash();
        return 0;
    }
    var child = OrielApp.Create("Oriel.WindowTest");
    // Its own data directory: Chromium's process singleton would hand this run to the main one.
    child.SetPath("userData", userData);
    child.Ready += (_, _) => Task.Run(() =>
    {
        Console.WriteLine("READY");
        OrielProcess.Hang();
        using var ran = new ManualResetEventSlim();
        Oriel.Cef.CefThread.RunOnUi(ran.Set);
        Console.WriteLine(ran.Wait(3000) ? "UI-RAN" : "UI-HUNG");
    });
    return child.Run();
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

static unsafe class HangProbe
{
    private static int s_waits;

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Wait(Oriel.Cef.Interop.cef_unresponsive_process_callback_t* self) => s_waits++;

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int Release(Oriel.Cef.Interop.cef_base_ref_counted_t* self) => 0;

    internal static (int Handled, int Waited) Unresponsive(WebContents page)
    {
        var callback = (Oriel.Cef.Interop.cef_unresponsive_process_callback_t*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(Oriel.Cef.Interop.cef_unresponsive_process_callback_t));
        callback->@base.release = &Release;
        callback->wait = &Wait;
        var fake = (nint)callback;
        s_waits = 0;
        var handler = page.Request.Native;
        // The page's handlers run on the UI thread, as CEF calls them.
        var handled = Oriel.Cef.CefThread.InvokeOnUi(() => handler->on_render_process_unresponsive(handler, null, (Oriel.Cef.Interop.cef_unresponsive_process_callback_t*)fake));
        System.Runtime.InteropServices.NativeMemory.Free(callback);
        return (handled, s_waits);
    }

    internal static void Responsive(WebContents page)
    {
        var handler = page.Request.Native;
        Oriel.Cef.CefThread.InvokeOnUi(() => { handler->on_render_process_responsive(handler, null); return 0; });
    }
}

static class MacInterop
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct NSPoint { public double X; public double Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct CGRect { public double X; public double Y; public double Width; public double Height; }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] internal static extern nint objc_getClass(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] internal static extern nint sel_registerName(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static extern NSPoint MouseLocation(nint cls, nint selector);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static extern byte SendBool(nint receiver, nint selector);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static extern nint SendNUInt(nint receiver, nint selector, nuint value);
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] internal static extern uint CGMainDisplayID();
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] internal static extern CGRect CGDisplayBounds(uint display);
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] internal static extern byte CGDisplayUsesForceToGray();
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static extern nint Send(nint receiver, nint selector);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint arg);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint SendString(nint receiver, nint selector, string utf8);

    /// <summary>The localizedName of the NSScreen whose NSScreenNumber is <paramref name="id"/>. Main thread only.</summary>
    internal static string ScreenName(uint id)
    {
        var screens = Send(objc_getClass("NSScreen"), sel_registerName("screens"));
        var key = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), "NSScreenNumber");
        for (var i = 0; i < (int)Send(screens, sel_registerName("count")); i++)
        {
            var screen = Send(screens, sel_registerName("objectAtIndex:"), i);
            var number = Send(Send(screen, sel_registerName("deviceDescription")), sel_registerName("objectForKey:"), key);
            if ((uint)Send(number, sel_registerName("unsignedIntValue")) != id) continue;
            var name = Send(screen, sel_registerName("localizedName"));
            return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Send(name, sel_registerName("UTF8String"))) ?? "";
        }
        return "";
    }
}

// Runs on the main thread through its run loop in every common mode, so it also runs inside a modal panel,
// where CEF holds its own tasks back as Chromium does in native nested loops.
static unsafe class MacMain
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Block
    {
        public nint Isa;
        public int Flags, Reserved;
        public nint Invoke;
        public nint* Descriptor;
        public nint Context;
    }

    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly nint s_stackBlock = System.Runtime.InteropServices.NativeLibrary.GetExport(System.Runtime.InteropServices.NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteStackBlock");
    private static readonly nint s_commonModes = *(nint*)System.Runtime.InteropServices.NativeLibrary.GetExport(System.Runtime.InteropServices.NativeLibrary.Load(CF), "kCFRunLoopCommonModes");
    private static readonly nint* s_descriptor = Descriptor();

    internal static T Invoke<T>(Func<T> func, TimeSpan timeout)
    {
        T result = default!;
        Exception? error = null;
        // Not disposed: after a timeout the block may still run and set it.
        var done = new ManualResetEventSlim();
        Action action = () =>
        {
            try { result = func(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        };
        var block = new Block { Isa = s_stackBlock, Invoke = (nint)(delegate* unmanaged<Block*, void>)&Run, Descriptor = s_descriptor, Context = System.Runtime.InteropServices.GCHandle.ToIntPtr(System.Runtime.InteropServices.GCHandle.Alloc(action)) };
        var main = CFRunLoopGetMain();
        CFRunLoopPerformBlock(main, s_commonModes, &block);
        CFRunLoopWakeUp(main);
        if (!done.Wait(timeout)) throw new TimeoutException("The main thread did not run the block.");
        if (error is not null) throw error;
        return result;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void Run(Block* block)
    {
        var handle = System.Runtime.InteropServices.GCHandle.FromIntPtr(block->Context);
        try
        {
            var action = (Action)handle.Target!;
            handle.Free();
            action();
        }
        catch (Exception e) { Console.Error.WriteLine($"MacMain block failed: {e}"); }
    }

    private static nint* Descriptor()
    {
        var d = (nint*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(2 * sizeof(nint)));
        d[1] = sizeof(Block);
        return d;
    }

    [System.Runtime.InteropServices.DllImport(CF)] private static extern nint CFRunLoopGetMain();
    [System.Runtime.InteropServices.DllImport(CF)] private static extern void CFRunLoopPerformBlock(nint loop, nint mode, Block* block);
    [System.Runtime.InteropServices.DllImport(CF)] private static extern void CFRunLoopWakeUp(nint loop);
}

/// <summary>An open or save panel's setup, read back from AppKit while it shows.</summary>
record MacPanelState(string? Title, string? Message, string? Prompt, string? NameFieldLabel, bool ShowsTagField,
    bool CanChooseFiles, bool CanChooseDirectories, bool MultipleSelection, bool ShowsHiddenFiles, bool CanCreateDirectories,
    bool ResolvesAliases, bool TreatsPackagesAsDirectories, bool CanSelectHiddenExtension, bool ExtensionHidden,
    string? Directory, string? NameField, bool IsSheet, nint SheetParent, bool IsModal, nint ModalWindow,
    string AllowedTypes, string Formats, bool AllowsOtherTypes);

static class MacPanel
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [System.Runtime.InteropServices.DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [System.Runtime.InteropServices.DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint arg);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint arg, nint arg2);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendEnd(nint receiver, nint selector, nint sheet, nint code);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendComplete(nint receiver, nint selector, nint code, nint url, nint urls);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendString(nint receiver, nint selector, string value);

    /// <summary>Answers the visible open or save panel with OK or Cancel; false while none shows. A sheet without
    /// <paramref name="paths"/> is ended as its buttons end it; otherwise the panel completes as its out-of-process UI
    /// does when the user accepts <paramref name="paths"/>. Main thread only.</summary>
    internal static bool Answer(bool accept, string[] paths)
    {
        var panel = Find();
        if (panel == 0) return false;
        // The panel's controls run out of process and ignore posted events (and ok: is unimplemented in-process).
        var parent = Send(panel, sel_registerName("sheetParent"));
        if (paths.Length == 0 && parent != 0)
        {
            SendEnd(parent, sel_registerName("endSheet:returnCode:"), panel, accept ? 1 : 0);
            return true;
        }
        var urls = Send(objc_getClass("NSMutableArray"), sel_registerName("array"));
        foreach (var path in paths)
            Send(urls, sel_registerName("addObject:"), Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), NSString(path)));
        var first = paths.Length > 0 ? Send(urls, sel_registerName("objectAtIndex:"), 0) : 0;
        // AppKit raises if a cancel carries any URLs, even an empty list.
        SendComplete(panel, sel_registerName("completeWithReturnCode:url:urls:"), accept ? 1 : 0, accept ? first : 0, accept ? urls : 0);
        return true;
    }

    /// <summary>The panel's setup as AppKit reports it. Main thread only.</summary>
    internal static MacPanelState State(nint panel)
    {
        var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var types = Obj(panel, "allowedFileTypes");
        // A save panel throws for the open-only selectors, though it claims to respond to them.
        var open = (byte)Send(panel, sel_registerName("isKindOfClass:"), objc_getClass("NSOpenPanel")) != 0;
        bool OpenBool(string selector) => open && Bool(panel, selector);
        return new MacPanelState(Text(panel, "title"), Text(panel, "message"), Text(panel, "prompt"), Text(panel, "nameFieldLabel"), Bool(panel, "showsTagField"),
            OpenBool("canChooseFiles"), OpenBool("canChooseDirectories"), OpenBool("allowsMultipleSelection"), Bool(panel, "showsHiddenFiles"),
            Bool(panel, "canCreateDirectories"), OpenBool("resolvesAliases"), Bool(panel, "treatsFilePackagesAsDirectories"),
            Bool(panel, "canSelectHiddenExtension"), Bool(panel, "isExtensionHidden"),
            Text(Obj(panel, "directoryURL"), "path"), Text(panel, "nameFieldStringValue"), Bool(panel, "isSheet"), Obj(panel, "sheetParent"),
            Obj(app, "modalWindow") == panel, Obj(app, "modalWindow"),
            types == 0 ? "(all)" : string.Join(",", Strings(types)), Popup(panel) is var popup and not 0 ? string.Join(",", Strings(Obj(popup, "itemTitles"))) : "",
            Bool(panel, "allowsOtherFileTypes"));
    }

    /// <summary>Picks a Format popup item as a user does (select, then its action) and returns the panel's allowed types.</summary>
    internal static string PickFormat(int index)
    {
        var panel = Find();
        var popup = Popup(panel);
        if (popup == 0) return "no popup";
        Send(popup, sel_registerName("selectItemAtIndex:"), index);
        Send(popup, sel_registerName("sendAction:to:"), Send(popup, sel_registerName("action")), Obj(popup, "target"));
        var types = Obj(panel, "allowedFileTypes");
        return types == 0 ? "(all)" : string.Join(",", Strings(types));
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
            if ((byte)Send(window, sel_registerName("isKindOfClass:"), savePanel) == 0) continue;
            if ((byte)Send(window, sel_registerName("isVisible")) == 0) continue;
            return window;
        }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern unsafe nint SendResolve(nint receiver, nint selector, nint data, nuint options, nint relativeTo, byte* stale, nint* error);

    /// <summary>The path a base64 security-scoped bookmark resolves to, or null. Main thread only.</summary>
    internal static unsafe string? ResolveBookmark(string base64)
    {
        var data = Send(Send(objc_getClass("NSData"), sel_registerName("alloc")), sel_registerName("initWithBase64EncodedString:options:"), NSString(base64), 0);
        if (data == 0) return null;
        byte stale = 0;
        nint error = 0;
        // NSURLBookmarkResolutionWithSecurityScope: only a security-scoped bookmark resolves this way.
        var url = SendResolve(objc_getClass("NSURL"), sel_registerName("URLByResolvingBookmarkData:options:relativeToURL:bookmarkDataIsStale:error:"), data, 1 << 10, 0, &stale, &error);
        Send(data, sel_registerName("release"));
        return url == 0 ? null : Text(url, "path");
    }

    internal static nint Obj(nint obj, string selector) => obj != 0 && Responds(obj, selector) ? Send(obj, sel_registerName(selector)) : 0;

    private static nint Popup(nint panel)
    {
        var subviews = Obj(Obj(panel, "accessoryView"), "subviews");
        var popupClass = objc_getClass("NSPopUpButton");
        for (nint i = 0, n = subviews == 0 ? 0 : Send(subviews, sel_registerName("count")); i < n; i++)
        {
            var view = Send(subviews, sel_registerName("objectAtIndex:"), i);
            if ((byte)Send(view, sel_registerName("isKindOfClass:"), popupClass) != 0) return view;
        }
        return 0;
    }

    private static IEnumerable<string> Strings(nint array)
    {
        for (nint i = 0, n = Send(array, sel_registerName("count")); i < n; i++)
            yield return Str(Send(array, sel_registerName("objectAtIndex:"), i)) ?? "";
    }

    private static bool Responds(nint obj, string selector) => (byte)Send(obj, sel_registerName("respondsToSelector:"), sel_registerName(selector)) != 0;
    private static bool Bool(nint obj, string selector) => obj != 0 && Responds(obj, selector) && (byte)Send(obj, sel_registerName(selector)) != 0;
    private static string? Str(nint nsString) => nsString == 0 ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Send(nsString, sel_registerName("UTF8String")));
    private static string? Text(nint obj, string selector) => Str(Obj(obj, selector));
    private static nint NSString(string value) => SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), value);
}

static class Posix
{
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "realpath")] private static extern nint realpath(string path, nint resolved);
    [System.Runtime.InteropServices.DllImport("libc")] private static extern void free(nint p);

    internal static string? RealPath(string path)
    {
        var p = realpath(path, 0);
        if (p == 0) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p); }
        finally { free(p); }
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

// A CEF command line built here, so what CommandLine.ApplyTo does to Chromium's own can be read back.
static unsafe class CommandLineProbe
{
    internal static string Apply((string Name, string Value)[] switches, CommandLine line) => Oriel.Cef.CefThread.InvokeOnUi(() =>
    {
        var commandLine = Oriel.Cef.Interop.CefNative.cef_command_line_create();
        Oriel.Cef.Interop.cef_string_utf16_t program = default;
        Oriel.Cef.CefString.Set(&program, "prog");
        commandLine->set_program(commandLine, &program);
        Oriel.Cef.CefString.Clear(&program);
        foreach (var (name, value) in switches)
        {
            Oriel.Cef.Interop.cef_string_utf16_t n = default, v = default;
            Oriel.Cef.CefString.Set(&n, name);
            Oriel.Cef.CefString.Set(&v, value);
            commandLine->append_switch_with_value(commandLine, &n, &v);
            Oriel.Cef.CefString.Clear(&n);
            Oriel.Cef.CefString.Clear(&v);
        }
        line.ApplyTo(commandLine);
        var text = Oriel.Cef.CefString.ReadUserFree(commandLine->get_command_line_string(commandLine));
        Oriel.Cef.NativeObject.Release(commandLine);
        return text;
    });
}
