using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using Oriel;
using Cookie = Oriel.Cookie;

// End-to-end Session check: partitions, cookies, per-session protocol and proxy, driven from a worker thread.
// The exit code is the number of failures (-1 when the run never finishes).
// Linux: two client certificates in the user's NSS store, which Chromium opens at start-up.
// With MediaChild the process runs only the getUserMedia checks, on fake devices, for the main run to collect: Linux's
// desktop capture died with the fake-device switch on, so the screen capture checks stay in a process without it.
const string MediaChild = "--oriel-media-child";
var mediaChild = args.Contains(MediaChild);
var clientCertificates = !mediaChild && OperatingSystem.IsLinux() && File.Exists("/usr/bin/certutil") && File.Exists("/usr/bin/pk12util") ? InstallClientCertificates() : false;
var app = OrielApp.Create("Oriel.SessionTest");
if (args.Contains("--enable-sandbox")) app.EnableSandbox();
if (mediaChild)
{
    // Its own data directory: Chromium's process singleton would hand this run to the main one.
    var childData = Path.Combine(Path.GetTempPath(), $"oriel-session-media-{Environment.ProcessId}");
    Directory.CreateDirectory(childData);
    app.SetPath("sessionData", childData);
    // Devices that exist everywhere and need no OS consent; the handler still decides.
    app.CommandLine.AppendSwitch("use-fake-device-for-media-stream");
}
// A second launch for the second-instance check: it hands itself to the main run and stops before Run.
const string SecondInstanceChild = "--oriel-second-instance-child";
if (args.Contains(SecondInstanceChild))
{
    var handedOver = !app.RequestSingleInstanceLock(new System.Text.Json.Nodes.JsonObject { ["child"] = "ok ✓" });
    Console.WriteLine((handedOver ? "HANDED-OVER" : "LOCK-GRANTED") + "|" + Environment.CurrentDirectory);
    return handedOver ? 0 : 1;
}
var failures = -1;
var results = new List<string>();
// Before Run: the lock is free (no other run), and WillFinishLaunching, Ready and WhenReady come in Electron's order.
var gotSingleInstanceLock = mediaChild || app.RequestSingleInstanceLock(new System.Text.Json.Nodes.JsonObject { ["run"] = 1 });
var launchOrder = new List<string>();
app.WillFinishLaunching += (_, _) => { lock (launchOrder) launchOrder.Add("will-finish-launching"); };
app.Ready += (_, _) => { lock (launchOrder) launchOrder.Add("ready"); };
var whenReady = app.WhenReady().ContinueWith(_ => { lock (launchOrder) launchOrder.Add("whenReady"); }, TaskScheduler.Default);

void Check(string name, bool pass, object? detail = null)
{
    var line = $"{(pass ? "PASS" : "FAIL")}  {name}  [{detail}]";
    results.Add(line);
    Console.WriteLine(line);
}

static async Task<Exception?> Throws(Func<Task> act)
{
    try { await act(); return null; }
    catch (Exception e) { return e; }
}

// A section that throws fails one check and lets the rest of the run go on.
async Task Section(string name, Func<Task> run)
{
    try { await run(); }
    catch (Exception e) { Check($"{name} ran to the end", false, e); }
}

static string Names(IEnumerable<Cookie> cookies) => string.Join(",", cookies.Select(c => c.Name).Order());

const string Site = "https://oriel.test/";

// netLog (patch 0006): Electron's session.netLog over the profile's NetLogExporter.
async Task NetLogChecks(BrowserWindow window, string directory)
{
    var netLog = window.WebContents.Session.NetLog;
    var logPath = Path.Combine(directory, "net-log.json");
    if (!Oriel.Cef.CefRuntime.IsPatched)
    {
        var stock = await Throws(() => netLog.StartLoggingAsync(logPath));
        Check("stock CEF: netLog.startLogging throws PlatformNotSupportedException (DRIFT E42)",
            stock is PlatformNotSupportedException && !netLog.CurrentlyLogging, stock?.GetType().Name);
        return;
    }
    Directory.CreateDirectory(directory);
    var start = netLog.StartLoggingAsync(logPath, new NetLogOptions { CaptureMode = NetLogCaptureMode.IncludeSensitive });
    Check("patched (0006): currentlyLogging is true as soon as startLogging returns", netLog.CurrentlyLogging);
    var twice = await Throws(() => netLog.StartLoggingAsync(logPath));
    Check("patched (0006): a second startLogging throws Electron's text", twice?.Message == "There is already a net log running", twice?.Message);
    var started = await Throws(() => start);
    await window.LoadUrl(Site);
    var stopped = await Throws(() => netLog.StopLoggingAsync());
    var text = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
    Check("patched (0006): stopLogging completes a JSON net log with constants and events",
        started is null && stopped is null && !netLog.CurrentlyLogging && text.Contains("\"constants\"") && text.TrimEnd().EndsWith('}'),
        $"{started?.Message} {stopped?.Message} {text.Length}");
    var again = await Throws(() => netLog.StopLoggingAsync());
    Check("patched (0006): stopLogging with none running faults with Electron's text", again?.Message == "No net log in progress", again?.Message);
    var badPath = await Throws(() => netLog.StartLoggingAsync(Path.Combine(directory, "missing", "dir", "net.json")));
    Check("patched (0006): an unopenable path faults with base::File's text and stops logging",
        badPath?.Message.StartsWith("FILE_ERROR_", StringComparison.Ordinal) == true && !netLog.CurrentlyLogging, badPath?.Message);
    var limited = Path.Combine(directory, "net-log-limited.json");
    await netLog.StartLoggingAsync(limited, new NetLogOptions { MaxFileSize = 64 * 1024 });
    await window.LoadUrl(Site);
    await netLog.StopLoggingAsync();
    Check("patched (0006): a log with maxFileSize starts, stops and leaves its file", File.Exists(limited),
        File.Exists(limited) ? new FileInfo(limited).Length : -1);
}
const string CustomScheme = "x-webrequest";
const string Page = "<!doctype html><title>session</title><p>session test</p>";

// Linux without an XDG portal on the bus (none is installed here): Chromium's fallback is GTK's theme.
#pragma warning disable CA1416 // Called only on Linux.
async Task NoPortalThemeAsync()
{
    var w = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await w.WebContents.LoadUrl("about:blank");
    async Task<bool?> Page(string query) => (await w.WebContents.ExecuteJavaScriptAsync($"matchMedia('{query}').matches"))?.GetValue<bool>();
    void Gtk(string name, object value) => Oriel.Cef.CefThread.InvokeOnUi(() =>
    {
        Oriel.Cef.Platform.LinuxGtkTheme.SetSetting(name, value);
        return true;
    });
    var updates = 0;
    EventHandler counter = (_, _) => Interlocked.Increment(ref updates);
    NativeTheme.Updated += counter;
    long? Latency(Action change)
    {
        var before = Volatile.Read(ref updates);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        change();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref updates) == before && DateTime.UtcNow < deadline) Thread.Sleep(10);
        return Volatile.Read(ref updates) > before ? clock.ElapsedMilliseconds : null;
    }

    var settled = Oriel.Cef.Platform.LinuxPortalSettings.WaitSettled(TimeSpan.FromSeconds(5));
    Check("NativeTheme: without a portal Linux reads none", settled && !Oriel.Cef.Platform.LinuxPortalSettings.Appearance.Present,
        Oriel.Cef.Platform.LinuxPortalSettings.Appearance);
    Check("NativeTheme: without a portal GTK's Adwaita is light and not high contrast, for Oriel and the page",
        !NativeTheme.ShouldUseDarkColors && !NativeTheme.ShouldUseHighContrastColors && await Page("(prefers-color-scheme: dark)") == false
        && await Page("(prefers-contrast: more)") == false);
    // The image's settings.ini names Yaru, which is not installed, and GTK's stand-in for it has no dark variant.
    Gtk("gtk-theme-name", "Adwaita");
    var variant = Latency(() => Gtk("gtk-application-prefer-dark-theme", true));
    Check("NativeTheme: without a portal GTK's prefer-dark variant is dark, raised at once", variant is < 300 && NativeTheme.ShouldUseDarkColors,
        $"{variant} ms; page dark={await Page("(prefers-color-scheme: dark)")} (CEF keeps GTK's first colours, DRIFT Y2)");
    var light = Latency(() => Gtk("gtk-application-prefer-dark-theme", false));
    Check("NativeTheme: and back to light, raised at once", light is < 300 && !NativeTheme.ShouldUseDarkColors, $"{light} ms");
    var contrast = Latency(() => Gtk("gtk-theme-name", "HighContrast"));
    Check("NativeTheme: without a portal a GTK theme named high contrast is high contrast, raised at once, for Oriel and the page",
        contrast is < 300 && NativeTheme.ShouldUseHighContrastColors && !NativeTheme.ShouldUseDarkColors && await Page("(prefers-contrast: more)") == true,
        $"{contrast} ms");
    Gtk("gtk-theme-name", "Adwaita");
    NativeTheme.Updated -= counter;
    w.Destroy();
}
#pragma warning restore CA1416

async Task RunAsync()
{
    // Identity and storage.
    var defaultSession = Session.DefaultSession;
    Check("DefaultSession is one object", ReferenceEquals(defaultSession, Session.DefaultSession));
    Check("FromPartition(\"\") is DefaultSession", ReferenceEquals(Session.FromPartition(""), defaultSession));
    Check("DefaultSession is persistent", defaultSession.IsPersistent() && defaultSession.GetStoragePath() is { Length: > 0 },
        defaultSession.GetStoragePath());
    var memory = Session.FromPartition("memory");
    Check("FromPartition returns the same session for a name", ReferenceEquals(memory, Session.FromPartition("memory")));
    Check("in-memory partition is not persistent", !memory.IsPersistent() && memory.GetStoragePath() is null);
    var other = Session.FromPartition("other");
    Check("different names are different sessions", !ReferenceEquals(memory, other));
    var disk = Session.FromPartition("persist:disk");
    var diskPath = disk.GetStoragePath();
    Check("persist: partition lives under the data directory",
        disk.IsPersistent() && diskPath == Path.Combine(defaultSession.GetStoragePath()!, "Partition disk"), diskPath);
    Check("FromPath finds the same persistent session", ReferenceEquals(Session.FromPath(diskPath!), disk));
    Check("FromPath rejects a path outside the data directory",
        await Throws(() => Task.FromResult(Session.FromPath(Path.GetTempPath()))) is ArgumentException);
    Check("FromPath rejects a nested path",
        await Throws(() => Task.FromResult(Session.FromPath(Path.Combine(diskPath!, "nested")))) is ArgumentException);
    Check("FromPath rejects a relative path", await Throws(() => Task.FromResult(Session.FromPath("relative"))) is ArgumentException);
    // Cookies, isolated per session.
    foreach (var s in new[] { defaultSession, memory, other, disk })
        foreach (var c in await s.Cookies.GetAsync(new CookieFilter { Domain = "oriel.test" }))
            await s.Cookies.RemoveAsync(Site, c.Name);
    await defaultSession.Cookies.SetAsync(new CookieDetails(Site) { Name = "inDefault", Value = "1" });
    await memory.Cookies.SetAsync(new CookieDetails(Site) { Name = "inMemory", Value = "2" });
    var inDefault = await defaultSession.Cookies.GetAsync(new CookieFilter { Url = Site });
    var inMemory = await memory.Cookies.GetAsync(new CookieFilter { Url = Site });
    var inOther = await other.Cookies.GetAsync(new CookieFilter { Url = Site });
    Check("a cookie set in a session is read back", inDefault.Any(c => c is { Name: "inDefault", Value: "1" }), Names(inDefault));
    Check("sessions do not share cookies", Names(inDefault) == "inDefault" && Names(inMemory) == "inMemory" && inOther.Count == 0,
        $"{Names(inDefault)} | {Names(inMemory)} | {Names(inOther)}");

    // Attributes round-trip.
    var expires = Math.Floor(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() + 0.0);
    await memory.Cookies.SetAsync(new CookieDetails(Site)
    {
        Name = "full", Value = "v", Domain = ".oriel.test", Path = "/app", Secure = true, HttpOnly = true,
        ExpirationDate = expires, SameSite = CookieSameSite.Strict,
    });
    var full = (await memory.Cookies.GetAsync(new CookieFilter { Name = "full" })).SingleOrDefault();
    Check("domain cookie: domain, not host-only", full is { Domain: ".oriel.test", HostOnly: false }, full);
    Check("path, secure, httpOnly and sameSite round-trip",
        full is { Path: "/app", Secure: true, HttpOnly: true, SameSite: CookieSameSite.Strict }, full);
    Check("expirationDate round-trips; not a session cookie",
        full is { Session: false, ExpirationDate: { } e } && Math.Abs(e - expires) < 1, full?.ExpirationDate);
    var plain = (await memory.Cookies.GetAsync(new CookieFilter { Name = "inMemory" })).SingleOrDefault();
    Check("host cookie: host-only session cookie, Lax by default",
        plain is { Domain: "oriel.test", HostOnly: true, Session: true, ExpirationDate: null, SameSite: CookieSameSite.Lax }, plain);

    // Filters.
    await memory.Cookies.SetAsync(new CookieDetails("https://sub.oriel.test/") { Name = "sub", Value = "3" });
    var byDomain = Names(await memory.Cookies.GetAsync(new CookieFilter { Domain = "oriel.test" }));
    Check("Domain filter includes subdomains", byDomain == "full,inMemory,sub", byDomain);
    var bySubdomain = Names(await memory.Cookies.GetAsync(new CookieFilter { Domain = "sub.oriel.test" }));
    Check("Domain filter excludes parent domains", bySubdomain == "sub", bySubdomain);
    Check("Url filter: only what a request would send",
        Names(await memory.Cookies.GetAsync(new CookieFilter { Url = "https://sub.oriel.test/" })) == "sub");
    Check("HttpOnly filter", Names(await memory.Cookies.GetAsync(new CookieFilter { HttpOnly = true })) == "full");
    Check("Session filter", Names(await memory.Cookies.GetAsync(new CookieFilter { Session = false })) == "full");

    // Remove and invalid cookies.
    await memory.Cookies.RemoveAsync("https://sub.oriel.test/", "sub");
    Check("RemoveAsync deletes the cookie", (await memory.Cookies.GetAsync(new CookieFilter { Name = "sub" })).Count == 0);
    var rejected = await Throws(() => memory.Cookies.SetAsync(new CookieDetails(Site) { Name = "bad", Domain = "elsewhere.test" }));
    Check("a cookie for another domain is rejected", rejected is ArgumentException, rejected?.Message);
    Check("FlushStoreAsync completes", await Throws(() => disk.Cookies.FlushStoreAsync().WaitAsync(TimeSpan.FromSeconds(5))) is null);

    // Cache and connections.
    Check("ClearCacheAsync completes", await Throws(() => memory.ClearCacheAsync().WaitAsync(TimeSpan.FromSeconds(5))) is null);
    Check("ClearAuthCacheAsync completes", await Throws(() => memory.ClearAuthCacheAsync().WaitAsync(TimeSpan.FromSeconds(5))) is null);
    Check("CloseAllConnectionsAsync completes",
        await Throws(() => memory.CloseAllConnectionsAsync().WaitAsync(TimeSpan.FromSeconds(5))) is null);

    // Host resolution.
    var localhost = await memory.ResolveHostAsync("localhost");
    Check("ResolveHostAsync resolves localhost",
        localhost.Endpoints.Any(e => e is { Address: "127.0.0.1", Family: "ipv4" } or { Address: "::1", Family: "ipv6" }),
        string.Join(",", localhost.Endpoints));
    var unresolved = await Throws(() => memory.ResolveHostAsync("does-not-exist.invalid"));
    Check("ResolveHostAsync fails for an unknown host", unresolved is HostResolutionException, unresolved?.Message);

    // Per-session protocol and a window in a partition.
    memory.Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    Check("a session's protocol handler is its own",
        memory.Protocol.IsProtocolHandled("https") && !other.Protocol.IsProtocolHandled("https")
        && !defaultSession.Protocol.IsProtocolHandled("https") && !Protocol.IsProtocolHandled("https"));
    defaultSession.Protocol.Handle("x-default", _ => ProtocolResponse.Text(""));
    var viaStatic = Protocol.IsProtocolHandled("x-default") && defaultSession.Protocol.IsProtocolHandled("x-default");
    defaultSession.Protocol.Unhandle("x-default");
    Check("DefaultSession.Protocol is the static Protocol",
        viaStatic && !Protocol.IsProtocolHandled("x-default") && !defaultSession.Protocol.IsProtocolHandled("x-default"));
    var win = new BrowserWindow(new BrowserWindowOptions
    {
        Title = "Oriel session test", Width = 500, Height = 300,
        WebPreferences = new WebPreferences { Partition = "memory" },
    });
    Check("WebContents.Session follows Partition", ReferenceEquals(win.WebContents.Session, memory));
    // A window opened at once in a brand-new on-disk session, by partition name and by Session object. After win: closing
    // the only window would quit the app.
    var freshWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "persist:fresh" } });
    var freshLoad = await Throws(() => freshWin.LoadUrl("data:text/html,fresh").WaitAsync(TimeSpan.FromSeconds(10)));
    Check("a window opened at once in a new persist: partition loads", freshLoad is null && freshWin.WebContents.GetUrl() == "data:text/html,fresh", freshLoad?.GetType().Name);
    freshWin.Close();
    var freshSession = Session.FromPartition("persist:fresh-object");
    freshSession.Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    var freshObjectWin = new BrowserWindow(new BrowserWindowOptions { Show = false, Width = 640, Height = 480, WebPreferences = new WebPreferences { Session = freshSession } });
    // The constructor returns with the native window made, as for any other session.
    var freshObjectBounds = freshObjectWin.GetBounds();
    var freshObjectLoad = await Throws(() => freshObjectWin.LoadUrl(Site).WaitAsync(TimeSpan.FromSeconds(10)));
    Check("a window opened at once in a new on-disk Session object exists when the constructor returns, and loads", freshObjectLoad is null && freshObjectWin.WebContents.GetUrl() == Site
        && freshObjectBounds.Width == 640 && freshObjectBounds.Height == 480, $"{freshObjectLoad?.GetType().Name} {freshObjectBounds}");
    freshObjectWin.Close();
    await UiThreadFreshSessionChecks();
    await win.LoadUrl(Site);
    var seen = (await win.WebContents.ExecuteJavaScriptAsync("document.cookie"))?.ToString();
    Check("the page sees its session's cookies only", seen == "inMemory=2", seen);
    await win.WebContents.ExecuteJavaScriptAsync("document.cookie = 'fromPage=4; path=/'; 1");
    // The page writes the cookie to the store asynchronously.
    var fromPage = 0;
    for (var i = 0; i < 50 && fromPage == 0; i++)
    {
        fromPage = (await memory.Cookies.GetAsync(new CookieFilter { Name = "fromPage" })).Count;
        if (fromPage == 0) await Task.Delay(100);
    }
    Check("a cookie the page sets lands in its session",
        fromPage == 1 && (await defaultSession.Cookies.GetAsync(new CookieFilter { Name = "fromPage" })).Count == 0, fromPage);
    await Section("CookieChangeChecks", CookieChangeChecks);

    // A popup can't change session: CEF gives it the opener's.
    var created = 0;
    win.WebContents.DidCreateWindow += (_, _) => created++;
    win.WebContents.SetWindowOpenHandler(_ => WindowOpenHandlerResponse.Allow(new BrowserWindowOptions
    {
        WebPreferences = new WebPreferences { Partition = "other" },
    }));
    await win.WebContents.ExecuteJavaScriptAsync("window.open('https://oriel.test/popup'); 1", userGesture: true);
    await Task.Delay(1500);
    Check("a popup asking for another session is blocked", created == 0 && BrowserWindow.GetAllWindows().Count == 1,
        BrowserWindow.GetAllWindows().Count);
    win.WebContents.SetWindowOpenHandler(null);
    // No gesture: a partition gets the default session's popup setting too.
    await win.WebContents.ExecuteJavaScriptAsync("window.open('https://oriel.test/popup'); 1");
    var popup = await WaitFor(() => BrowserWindow.GetAllWindows().FirstOrDefault(w => w != win));
    Check("a popup opens without a gesture and shares its opener's session", popup is not null && ReferenceEquals(popup.WebContents.Session, memory));
    popup?.Close();

    // A persistent partition window creates its directory (deleted before the run).
    var diskWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = disk } });
    disk.Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    await diskWin.LoadUrl(Site);
    await diskWin.WebContents.ExecuteJavaScriptAsync("localStorage.setItem('k', 'v'); 1");
    Check("Session option wins and stores on disk", ReferenceEquals(diskWin.WebContents.Session, disk) && Directory.Exists(diskPath));
    // A profile on disk inherits nothing from the default one, unlike an in-memory partition.
    await diskWin.WebContents.ExecuteJavaScriptAsync("window.open('https://oriel.test/popup'); 1");
    var diskPopup = await WaitFor(() => BrowserWindow.GetAllWindows().FirstOrDefault(w => w.WebContents.Session == disk && w != diskWin));
    Check("a persistent partition allows popups without a gesture", diskPopup is not null);
    diskPopup?.Close();
    diskWin.Close();

    // A session's user agent and languages reach its pages, open ones too; a page's own override wins.
    var agents = Session.FromPartition("agents");
    agents.Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    var agentWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = agents } });
    await agentWin.LoadUrl(Site);
    agents.SetUserAgent("SessionAgent/1.0", "fr-FR");
    await agentWin.LoadUrl(Site + "again");
    async Task<string?> Nav(BrowserWindow w) => (await w.WebContents.ExecuteJavaScriptAsync("navigator.userAgent + '|' + navigator.language"))?.ToString();
    var open = await Nav(agentWin);
    Check("Session.SetUserAgent reaches an open page, with its languages", open == "SessionAgent/1.0|fr-FR", open);
    Check("Session.GetUserAgent and WebContents.GetUserAgent", agents.GetUserAgent() == "SessionAgent/1.0" && agentWin.WebContents.GetUserAgent() == "SessionAgent/1.0");
    agentWin.WebContents.SetUserAgent("PageAgent/1.0");
    await agentWin.LoadUrl(Site + "page");
    var pageOwn = await Nav(agentWin);
    agentWin.WebContents.SetUserAgent("");
    await agentWin.LoadUrl(Site + "cleared");
    var cleared = await Nav(agentWin);
    Check("a page's own user agent wins, and clearing it returns to the session's", pageOwn?.StartsWith("PageAgent/1.0|") == true && cleared == "SessionAgent/1.0|fr-FR",
        $"{pageOwn} / {cleared}");
    var later = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = agents } });
    await later.LoadUrl(Site);
    var laterAgent = await Nav(later);
    Check("a page opened later in the session starts with its user agent", laterAgent == "SessionAgent/1.0|fr-FR", laterAgent);
    // Electron overrides with Chromium's default client hints, so navigator.userAgentData keeps its brands (C3).
    const string Hints = "navigator.userAgentData.getHighEntropyValues(['architecture', 'bitness', 'fullVersionList', 'model', 'platformVersion', 'wow64', 'formFactors']).then(v => JSON.stringify(v))";
    var plainHints = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "memory" } });
    await plainHints.LoadUrl(Site);
    var defaultHints = (await plainHints.WebContents.ExecuteJavaScriptAsync(Hints))?.ToString();
    plainHints.Destroy();
    var sessionHints = (await later.WebContents.ExecuteJavaScriptAsync(Hints))?.ToString();
    later.WebContents.SetUserAgent("PageAgent/2.0");
    await later.LoadUrl(Site + "hints");
    var pageHints = (await later.WebContents.ExecuteJavaScriptAsync(Hints))?.ToString();
    Check("every page has Chromium's full client hints, a session or page user agent included, as Electron's",
        defaultHints is { Length: > 20 } && defaultHints.Contains("\"fullVersionList\":[{") && defaultHints.Contains("\"architecture\":\"") && !defaultHints.Contains("\"architecture\":\"\"")
        && sessionHints == defaultHints && pageHints == defaultHints,
        $"{defaultHints} | {sessionHints} | {pageHints}");
    Check("other sessions keep the app's", defaultSession.GetUserAgent() == app.UserAgentFallback && memory.GetUserAgent() == app.UserAgentFallback);
    later.Destroy();
    agentWin.Destroy();

    await Section("PermissionChecks", () => PermissionChecks());
    // Downloads: Session.WillDownload and DownloadItem, as Electron.
    var downloads = Session.FromPartition("downloads");
    var report = Encoding.UTF8.GetBytes("report body " + new string('x', 4000));
    const int SlowChunks = 40, SlowChunk = 64 * 1024;
    string? downloadHeader = null;
    downloads.Protocol.Handle("https", request =>
    {
        if (request.Headers.TryGetValue("X-Download", out var marked)) downloadHeader = marked;
        var attachment = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"report.txt\"" };
        if (request.Url.EndsWith("/report.txt", StringComparison.Ordinal))
            return new ProtocolResponse { ContentType = "text/plain", Headers = attachment, Body = new MemoryStream(report) };
        if (request.Url.EndsWith("/slow.bin", StringComparison.Ordinal))
            return new ProtocolResponse { ContentType = "application/octet-stream", Headers = attachment, Body = new SlowStream(SlowChunks, SlowChunk), Length = (long)SlowChunks * SlowChunk };
        return ProtocolResponse.Text(Page, "text/html");
    });
    var downloadWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = downloads } });
    await downloadWin.LoadUrl(Site);
    var downloadDir = Path.Combine(Path.GetTempPath(), $"oriel-download-{Environment.ProcessId}");
    if (Directory.Exists(downloadDir)) Directory.Delete(downloadDir, recursive: true);

    // Starts a download and waits for its done state; decide runs inside WillDownload.
    async Task<(DownloadItem? Item, WebContents? Page, string? Filename, DownloadState? State, int DoneCount)> Download(Action trigger, Action<WillDownloadEventArgs> decide)
    {
        var done = new TaskCompletionSource<DownloadState>(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadItem? item = null;
        WebContents? page = null;
        string? filename = null;
        var doneCount = 0;
        void OnWillDownload(object? _, WillDownloadEventArgs e)
        {
            item = e.Item;
            page = e.WebContents;
            filename = e.Item.GetFilename();
            e.Item.Done += (_, d) =>
            {
                Interlocked.Increment(ref doneCount);
                done.TrySetResult(d.State);
            };
            decide(e);
        }
        downloads.WillDownload += OnWillDownload;
        trigger();
        var state = await done.Task.WaitAsync(TimeSpan.FromSeconds(20)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (DownloadState?)null);
        await Task.Delay(500);
        downloads.WillDownload -= OnWillDownload;
        return (item, page, filename, state, doneCount);
    }

    var savePath = Path.Combine(downloadDir, "new", "report.txt");
    var saved = await Download(() => downloadWin.WebContents.DownloadUrl(Site + "report.txt"), e => e.Item.SetSavePath(savePath));
    Check("DownloadUrl raises WillDownload with the page", saved.Item is not null && ReferenceEquals(saved.Page, downloadWin.WebContents));
    Check("SetSavePath saves there, creating the directory, and Done says completed",
        saved.State == DownloadState.Completed && File.Exists(savePath) && File.ReadAllBytes(savePath).SequenceEqual(report), $"{saved.State} {File.Exists(savePath)}");
    var item = saved.Item;
    Check("the item reports its file name, URL, MIME type and save path",
        saved.Filename == "report.txt" && item?.GetUrl() == Site + "report.txt" && item.GetMimeType() == "text/plain" && item.GetSavePath() == savePath && item.GetFilename() == "report.txt"
        && item.GetUrlChain().SequenceEqual([Site + "report.txt"]),
        $"{saved.Filename} {item?.GetUrl()} {item?.GetMimeType()} {item?.GetSavePath()}");
    Check("the item reports its bytes, progress and state", item?.GetReceivedBytes() == report.Length && item.GetTotalBytes() == report.Length
        && item.GetPercentComplete() == 100 && item.GetState() == DownloadState.Completed, $"{item?.GetReceivedBytes()}/{item?.GetTotalBytes()} {item?.GetPercentComplete()}% {item?.GetState()}");
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    Check("the item's start and end times are Unix seconds", item is not null && Math.Abs(item.GetStartTime() - now) < 60 && item.GetEndTime() >= item.GetStartTime(),
        $"{item?.GetStartTime()} {item?.GetEndTime()} now {now}");
    Check("Done is raised once", saved.DoneCount == 1, saved.DoneCount);

    WebFrameMain? linkedFrame = null;
    var linked = await Download(() => _ = downloadWin.WebContents.ExecuteJavaScriptAsync(
        "const a = document.createElement('a'); a.href = '/report.txt'; a.download = ''; document.body.append(a); a.click(); 1", userGesture: true),
        e =>
        {
            linkedFrame = e.Frame;
            e.Item.SetSavePath(Path.Combine(downloadDir, "linked.txt"));
        });
    Check("a download link raises WillDownload", linked.Item?.GetUrl() == Site + "report.txt" && linked.State == DownloadState.Completed, $"{linked.Item?.GetUrl()} {linked.State}");
    if (Oriel.Cef.CefRuntime.IsPatched)
    {
        Check("patched (0005): a link clicked with a gesture reports hasUserGesture and the page's initiator origin",
            linked.Item?.HasUserGesture() == true && linked.Item.GetInitiatorOrigin() == "https://oriel.test",
            $"{linked.Item?.HasUserGesture()} {linked.Item?.GetInitiatorOrigin()}");
        Check("patched (0005): will-download's frame is the page's main frame",
            linkedFrame is not null && ReferenceEquals(linkedFrame, downloadWin.WebContents.MainFrame), linkedFrame?.Url);
    }
    else
        Check("stock CEF: no gesture, initiator or frame (DRIFT E13, E40)",
            linked.Item?.HasUserGesture() == false && linked.Item.GetInitiatorOrigin() == "" && linkedFrame is null);
    var prevented = await Download(() => _ = downloadWin.WebContents.ExecuteJavaScriptAsync("location.href = '/report.txt'; 1", userGesture: true), e => e.Cancel = true);
    Check("a navigation to an attachment raises WillDownload", prevented.Item?.GetUrl() == Site + "report.txt", prevented.Item?.GetUrl());
    Check("cancelling WillDownload cancels the download, once", prevented.State == DownloadState.Cancelled && prevented.DoneCount == 1
        && prevented.Item?.GetState() == DownloadState.Cancelled, $"{prevented.State} {prevented.DoneCount}");

    var cancelled = await Download(() => downloadWin.WebContents.DownloadUrl(Site + "report.txt"), e =>
    {
        e.Item.SetSavePath(Path.Combine(downloadDir, "cancelled.txt"));
        e.Item.Cancel();
    });
    Check("item.Cancel() during WillDownload cancels it", cancelled.State == DownloadState.Cancelled && !File.Exists(Path.Combine(downloadDir, "cancelled.txt")), cancelled.State);

    var slowPath = Path.Combine(downloadDir, "slow.bin");
    var progressed = 0;
    string? pauseResult = null;
    var slow = await Download(() => downloadWin.WebContents.DownloadUrl(Site + "slow.bin"), e =>
    {
        e.Item.SetSavePath(slowPath);
        e.Item.Updated += (_, u) =>
        {
            if (u.State == DownloadState.Progressing && Interlocked.Increment(ref progressed) == 1)
                _ = Task.Run(async () =>
                {
                    e.Item.Pause();
                    for (var i = 0; i < 60 && !e.Item.IsPaused(); i++) await Task.Delay(50);
                    var paused = e.Item.IsPaused() && e.Item.CanResume();
                    var before = e.Item.GetReceivedBytes();
                    await Task.Delay(800);
                    var held = e.Item.GetReceivedBytes() == before && e.Item.GetState() == DownloadState.Progressing;
                    pauseResult = $"{paused} {held}";
                    e.Item.Resume();
                });
        };
    });
    Check("Updated reports progress", progressed > 0, progressed);
    Check("Pause pauses the download: IsPaused, CanResume, no bytes while paused", pauseResult == "True True", pauseResult);
    Check("Resume finishes it", slow.State == DownloadState.Completed && new FileInfo(slowPath).Length == (long)SlowChunks * SlowChunk && slow.Item?.IsPaused() == false && !slow.Item.CanResume(),
        $"{slow.State} {(File.Exists(slowPath) ? new FileInfo(slowPath).Length : -1)}");

    var midway = await Download(() => downloadWin.WebContents.DownloadUrl(Site + "slow.bin"), e =>
    {
        e.Item.SetSavePath(Path.Combine(downloadDir, "midway.bin"));
        e.Item.Updated += (_, _) => e.Item.Cancel();
    });
    Check("Cancel during the download: Done says cancelled", midway.State == DownloadState.Cancelled && midway.DoneCount == 1, $"{midway.State} {midway.DoneCount}");

    await NetLogChecks(downloadWin, downloadDir);

    // A real HTTP server: Content-Disposition, ETag and Last-Modified, a redirect, range requests, and a reset mid-body.
    var served = new DownloadServer();
    var downloadListener = new TcpListener(IPAddress.Loopback, 0);
    downloadListener.Start();
    _ = ServeDownloads(downloadListener, served);
    var httpOrigin = $"http://127.0.0.1:{((IPEndPoint)downloadListener.LocalEndpoint).Port}/";
    var transitions = new System.Collections.Concurrent.ConcurrentQueue<string>();
    string? savePathAtStart = null;
    var renamedPath = Path.Combine(downloadDir, "renamed.bin");
    var viaRedirect = await Download(() => downloads.DownloadUrl(httpOrigin + "redirect.bin"), e =>
    {
        savePathAtStart = e.Item.GetSavePath();
        e.Item.SetSavePath(renamedPath);
        e.Item.Updated += (_, u) => transitions.Enqueue($"updated:{u.State}:{(e.Item.IsPaused() ? "paused" : "running")}");
        e.Item.Done += (_, d) => transitions.Enqueue($"done:{d.State}");
    });
    var viaRedirectItem = viaRedirect.Item;
    Check("HTTP: getURLChain after a redirect is the original URL then the final one; getURL is the final one",
        viaRedirectItem?.GetUrlChain().SequenceEqual([httpOrigin + "redirect.bin", httpOrigin + "file.bin"]) == true && viaRedirectItem.GetUrl() == httpOrigin + "file.bin",
        $"{string.Join(" > ", viaRedirectItem?.GetUrlChain() ?? [])} | {viaRedirectItem?.GetUrl()}");
    Check("HTTP: getContentDisposition and getMimeType read back the response's headers",
        viaRedirectItem?.GetContentDisposition() == DownloadServer.Disposition && viaRedirectItem.GetMimeType() == "application/octet-stream",
        $"{viaRedirectItem?.GetContentDisposition()} | {viaRedirectItem?.GetMimeType()}");
    Check("HTTP: getFilename is the Content-Disposition name, not the save path's", viaRedirectItem?.GetFilename() == "named.bin" && viaRedirect.Filename == "named.bin",
        $"{viaRedirect.Filename} {viaRedirectItem?.GetFilename()}");
    Check("HTTP: getSavePath is empty in will-download until set, then the path set", savePathAtStart == "" && viaRedirectItem?.GetSavePath() == renamedPath,
        $"'{savePathAtStart}' {viaRedirectItem?.GetSavePath()}");
    if (Oriel.Cef.CefRuntime.IsPatched)
        Check("patched (0005): getETag and getLastModifiedTime are the response's validators",
            viaRedirectItem?.GetETag() == "\"oriel-v1\"" && viaRedirectItem.GetLastModifiedTime() == "Tue, 06 Oct 2026 12:00:00 GMT",
            $"{viaRedirectItem?.GetETag()} | {viaRedirectItem?.GetLastModifiedTime()}");
    else
        Check("HTTP: getETag, getLastModifiedTime and hasUserGesture are CEF's blanks (DRIFT E13)",
            viaRedirectItem?.GetETag() == "" && viaRedirectItem.GetLastModifiedTime() == "" && !viaRedirectItem.HasUserGesture());
    Check("HTTP: the file arrives whole, with its total and received bytes", viaRedirect.State == DownloadState.Completed && File.Exists(renamedPath)
        && File.ReadAllBytes(renamedPath).SequenceEqual(DownloadServer.Body) && viaRedirectItem?.GetTotalBytes() == DownloadServer.Body.Length
        && viaRedirectItem.GetReceivedBytes() == DownloadServer.Body.Length && viaRedirectItem.GetPercentComplete() == 100,
        $"{viaRedirect.State} {viaRedirectItem?.GetReceivedBytes()}/{viaRedirectItem?.GetTotalBytes()}");
    var sequence = transitions.ToArray();
    Check("HTTP: state transitions are updated (progressing) then a single done (completed), and nothing after it",
        sequence.Length >= 2 && sequence[..^1].All(t => t == "updated:Progressing:running") && sequence[^1] == "done:Completed",
        string.Join(",", sequence.Distinct()));

    var pausedHttpPath = Path.Combine(downloadDir, "paused-http.bin");
    string? httpPause = null;
    var pausedUpdate = 0;
    var pausedHttp = await Download(() => downloads.DownloadUrl(httpOrigin + "file.bin"), e =>
    {
        e.Item.SetSavePath(pausedHttpPath);
        var started = 0;
        e.Item.Updated += (_, u) =>
        {
            if (e.Item.IsPaused()) Interlocked.Increment(ref pausedUpdate);
            if (u.State != DownloadState.Progressing || Interlocked.Increment(ref started) != 1) return;
            _ = Task.Run(async () =>
            {
                e.Item.Pause();
                for (var i = 0; i < 60 && !e.Item.IsPaused(); i++) await Task.Delay(50);
                var paused = e.Item.IsPaused() && e.Item.CanResume();
                var before = e.Item.GetReceivedBytes();
                await Task.Delay(800);
                httpPause = $"{paused} {e.Item.GetReceivedBytes() == before} {e.Item.GetState()}";
                e.Item.Resume();
            });
        };
    });
    Check("HTTP: pause holds the bytes with isPaused and canResume, and updated reports the pause", httpPause == "True True Progressing" && pausedUpdate > 0,
        $"{httpPause} paused updates={pausedUpdate}");
    Check("HTTP: resume finishes the file and canResume is then false", pausedHttp.State == DownloadState.Completed
        && File.Exists(pausedHttpPath) && File.ReadAllBytes(pausedHttpPath).SequenceEqual(DownloadServer.Body) && pausedHttp.Item?.CanResume() == false,
        $"{pausedHttp.State}");

    var droppedPath = Path.Combine(downloadDir, "dropped.bin");
    var interruptedUpdates = 0;
    var canResumeWhenInterrupted = false;
    var dropped = await Download(() => downloads.DownloadUrl(httpOrigin + "drop.bin"), e =>
    {
        e.Item.SetSavePath(droppedPath);
        e.Item.Updated += (_, u) =>
        {
            if (u.State != DownloadState.Interrupted || Interlocked.Increment(ref interruptedUpdates) != 1) return;
            canResumeWhenInterrupted = e.Item.CanResume();
            e.Item.Resume();
        };
    });
    Check("HTTP: a reset mid-body raises updated (interrupted), not done, and canResume is true",
        interruptedUpdates >= 1 && canResumeWhenInterrupted, $"interrupted updates={interruptedUpdates} canResume={canResumeWhenInterrupted}");
    Check("HTTP: resume after the reset asks for the rest with Range and completes the file, done once",
        dropped.State == DownloadState.Completed && dropped.DoneCount == 1 && served.Ranges.Any(r => r.StartsWith("bytes=", StringComparison.Ordinal) && !r.StartsWith("bytes=0-", StringComparison.Ordinal))
        && File.Exists(droppedPath) && File.ReadAllBytes(droppedPath).SequenceEqual(DownloadServer.Body),
        $"{dropped.State} done={dropped.DoneCount} ranges={string.Join(",", served.Ranges)}");

    var forbidden = await Download(() => downloads.DownloadUrl(httpOrigin + "forbidden.bin"), e => e.Item.SetSavePath(Path.Combine(downloadDir, "forbidden.bin")));
    Check("HTTP: a 403 is an interruption it cannot resume from: done (interrupted), canResume false",
        forbidden.State == DownloadState.Interrupted && forbidden.Item?.CanResume() == false && forbidden.DoneCount == 1, $"{forbidden.State} {forbidden.DoneCount}");
    downloadListener.Stop();
    // Session.DownloadUrl: no page, through the session's hidden page, which no window list or event sees.
    var windowsBefore = BrowserWindow.GetAllWindows().Count;
    var pagesMade = 0;
    EventHandler<WebContents> countPages = (_, _) => Interlocked.Increment(ref pagesMade);
    app.WebContentsCreated += countPages;
    var sessionPath = Path.Combine(downloadDir, "session.txt");
    var bySession = await Download(() => downloads.DownloadUrl(Site + "report.txt"), e => e.Item.SetSavePath(sessionPath));
    app.WebContentsCreated -= countPages;
    Check("Session.DownloadUrl raises WillDownload with no page and saves the file", bySession.Item is not null && bySession.Page is null
        && bySession.State == DownloadState.Completed && File.Exists(sessionPath) && File.ReadAllBytes(sessionPath).SequenceEqual(report),
        $"{bySession.Item?.GetUrl()} page={bySession.Page is not null} {bySession.State}");
    Check("Session.DownloadUrl's hidden page is no window and raises no WebContentsCreated",
        BrowserWindow.GetAllWindows().Count == windowsBefore && pagesMade == 0, $"{BrowserWindow.GetAllWindows().Count} vs {windowsBefore}, {pagesMade}");
    Check("Session.DownloadUrl sends no extra header unasked", downloadHeader is null, downloadHeader);
    var headerPath = Path.Combine(downloadDir, "headers.txt");
    var withHeaders = await Download(() => downloads.DownloadUrl(Site + "report.txt", new DownloadUrlOptions { Headers = new Dictionary<string, string> { ["X-Download"] = "yes" } }),
        e => e.Item.SetSavePath(headerPath));
    Check("Session.DownloadUrl's headers reach the server", withHeaders.State == DownloadState.Completed && downloadHeader == "yes" && File.Exists(headerPath),
        $"{withHeaders.State} {downloadHeader}");
    downloadHeader = null;
    var afterHeaders = await Download(() => downloads.DownloadUrl(Site + "report.txt"), e => e.Item.SetSavePath(Path.Combine(downloadDir, "after.txt")));
    Check("a later Session.DownloadUrl without headers sends none", afterHeaders.State == DownloadState.Completed && downloadHeader is null,
        $"{afterHeaders.State} {downloadHeader}");
    // A session of their own, so their first calls race interception being switched on.
    await DownloadUrlHeaderChecks(Session.FromPartition("download-headers"), downloadDir);
    downloadWin.Destroy();
    Directory.Delete(downloadDir, recursive: true);
    await Section("StorageChecks", StorageChecks);
    await Section("ServiceWorkerChecks", ServiceWorkerChecks);

    // webRequest: the session's listeners see, cancel, redirect and change requests, as Electron's.
    var watched = Session.FromPartition("webrequest");
    watched.Protocol.Handle("https", request =>
    {
        var path = new Uri(request.Url).AbsolutePath;
        return path switch
        {
            "/data" => ProtocolResponse.Text("data"),
            "/echo" => ProtocolResponse.Text($"added={request.Headers.GetValueOrDefault("X-Added")} removed={request.Headers.GetValueOrDefault("X-Removed")}"),
            "/server-redirect" => new ProtocolResponse { StatusCode = 302, Headers = new Dictionary<string, string> { ["Location"] = Site + "data" } },
            "/shared.js" => ProtocolResponse.Text("fetch('/from-shared');", "text/javascript"),
            "/sw.js" => ProtocolResponse.Text("self.addEventListener('install', e => e.waitUntil(fetch('/from-sw')));", "text/javascript"),
            _ => ProtocolResponse.Text(Page, "text/html"),
        };
    });
    var requestWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = watched } });
    var events = new System.Collections.Concurrent.ConcurrentQueue<(string Event, WebRequestDetails Details)>();
    WebRequestDetails? Saw(string ev, string path) => events.LastOrDefault(s => s.Event == ev && s.Details.Url == Site + path).Details;
    async Task<string?> Fetch(string path, string init = "{}")
        => (await requestWin.WebContents.ExecuteJavaScriptAsync($"fetch('{path}', {init}).then(r => r.text()).catch(e => 'failed')"))?.ToString();

    watched.WebRequest.OnBeforeRequest(new WebRequestFilter { Urls = ["https://oriel.test/*"], ExcludeUrls = ["https://oriel.test/unfiltered"] }, details =>
    {
        events.Enqueue(("before", details));
        var path = new Uri(details.Url).AbsolutePath;
        return Task.FromResult(path switch
        {
            "/blocked" => new WebRequestResponse { Cancel = true },
            "/moved" => new WebRequestResponse { RedirectUrl = Site + "data" },
            _ => new WebRequestResponse(),
        });
    });
    watched.WebRequest.OnBeforeSendHeaders(new WebRequestFilter { Urls = ["https://oriel.test/echo"] }, async details =>
    {
        events.Enqueue(("beforeSend", details));
        await Task.Delay(50);
        if (details.RequestHeaders!.ContainsKey("X-Cancel")) return new BeforeSendHeadersResponse { Cancel = true };
        var headers = details.RequestHeaders!.Where(kv => !kv.Key.Equals("X-Removed", StringComparison.OrdinalIgnoreCase)).ToDictionary();
        headers["X-Added"] = "yes";
        return new BeforeSendHeadersResponse { RequestHeaders = headers };
    });
    watched.WebRequest.OnSendHeaders(null, details => events.Enqueue(("send", details)));
    watched.WebRequest.OnHeadersReceived(null, details => events.Enqueue(("headers", details)));
    watched.WebRequest.OnResponseStarted(null, details => events.Enqueue(("started", details)));
    watched.WebRequest.OnBeforeRedirect(null, details => events.Enqueue(("redirect", details)));
    watched.WebRequest.OnCompleted(null, details => events.Enqueue(("completed", details)));
    watched.WebRequest.OnErrorOccurred(null, details => events.Enqueue(("error", details)));

    await requestWin.LoadUrl(Site);
    var page = await WaitFor(() => Saw("before", ""));
    Check("onBeforeRequest sees the page's navigation", page is { Method: "GET", ResourceType: "mainFrame" } && ReferenceEquals(page.WebContents, requestWin.WebContents) && page.Timestamp > 0,
        $"{page?.Method} {page?.ResourceType} {page?.Timestamp}");
    var data = await Fetch("/data");
    var fetched = await WaitFor(() => Saw("completed", "data"));
    Check("a fetch is an xhr, and onCompleted reports its response", data == "data" && Saw("before", "data")?.ResourceType == "xhr"
        && fetched is { StatusCode: 200, StatusLine: "HTTP/1.1 200 OK", Error: "net::OK" } && fetched.ResponseHeaders?["Content-Type"].Single().StartsWith("text/plain") == true
        && fetched.Id == Saw("before", "data")?.Id, $"{data} {fetched?.StatusCode} {fetched?.StatusLine} {fetched?.Error}");
    Check("onHeadersReceived and onResponseStarted see the response", Saw("headers", "data")?.StatusCode == 200 && Saw("started", "data")?.StatusCode == 200);
    Check("onSendHeaders sees the request being sent", Saw("send", "data")?.RequestHeaders is not null);

    var blocked = await Fetch("/blocked");
    var blockedError = await WaitFor(() => Saw("error", "blocked"));
    Check("cancel in onBeforeRequest stops the request, and onErrorOccurred reports it", blocked == "failed" && blockedError?.Error == "net::ERR_BLOCKED_BY_CLIENT"
        && Saw("completed", "blocked") is null, $"{blocked} {blockedError?.Error}");

    var moved = await Fetch("/moved");
    var internalRedirect = await WaitFor(() => Saw("redirect", "moved"));
    Check("redirectURL in onBeforeRequest sends the request elsewhere, as an internal redirect", moved == "data"
        && internalRedirect is { StatusCode: 307, StatusLine: "HTTP/1.1 307 Internal Redirect", RedirectUrl: Site + "data" },
        $"{moved} {internalRedirect?.StatusLine} {internalRedirect?.RedirectUrl}");

    var echoed = await Fetch("/echo", "{ headers: { 'X-Removed': 'gone' } }");
    var sentEcho = await WaitFor(() => Saw("send", "echo"));
    Check("onBeforeSendHeaders sees the headers and can replace them", echoed == "added=yes removed="
        && Saw("beforeSend", "echo")?.RequestHeaders?.GetValueOrDefault("X-Removed") == "gone", echoed);
    Check("onSendHeaders sees the replaced headers", sentEcho?.RequestHeaders?.GetValueOrDefault("X-Added") == "yes" && sentEcho.RequestHeaders.ContainsKey("X-Removed") == false,
        sentEcho is null ? "none" : string.Join(",", sentEcho.RequestHeaders!.Keys));
    Check("onBeforeSendHeaders follows its filter", Saw("beforeSend", "data") is null);
    var refused = await Fetch("/echo", "{ headers: { 'X-Cancel': '1' } }");
    Check("cancel in onBeforeSendHeaders stops the request", refused == "failed", refused);
    var failedLoads = new List<int>();
    Task? blockedTask = null;
    var settledBeforeEvent = false;
    // Electron rejects loadURL from its own did-fail-load listener, so the app's earlier listener runs first.
    requestWin.WebContents.DidFailLoad += (_, e) => { failedLoads.Add(e.ErrorCode); settledBeforeEvent |= blockedTask?.IsCompleted == true; };
    var blockedLoad = await Throws(() => (blockedTask = requestWin.LoadUrl(Site + "blocked")).WaitAsync(TimeSpan.FromSeconds(10)));
    Check("a navigation a listener cancels fails LoadUrl and DidFailLoad with ERR_BLOCKED_BY_CLIENT, as Electron's",
        blockedLoad is LoadFailedException { ErrorCode: -20 } && failedLoads.SequenceEqual([-20]) && !settledBeforeEvent,
        $"{blockedLoad?.Message} [{string.Join(",", failedLoads)}] settled before the event: {settledBeforeEvent}");
    // A did-fail-load handler that loads elsewhere gets its own task: the failure settles only the load that failed, which
    // the new navigation aborted first, as Electron's loadURL rejects on did-start-navigation before its own did-fail-load.
    Task? fallbackTask = null;
    EventHandler<LoadFailedEventArgs> retry = (_, e) => { if (e.ErrorCode == -20 && fallbackTask is null) fallbackTask = requestWin.LoadUrl(Site + "fallback"); };
    requestWin.WebContents.DidFailLoad += retry;
    var retriedLoad = await Throws(() => requestWin.LoadUrl(Site + "blocked").WaitAsync(TimeSpan.FromSeconds(10)));
    var fallbackLoad = fallbackTask is null ? new InvalidOperationException("no retry") : await Throws(() => fallbackTask.WaitAsync(TimeSpan.FromSeconds(10)));
    requestWin.WebContents.DidFailLoad -= retry;
    Check("a LoadUrl a did-fail-load handler starts loads, while the failed one fails as aborted, as Electron's",
        retriedLoad is LoadFailedException { ErrorCode: -3 } && fallbackLoad is null && requestWin.WebContents.GetUrl() == Site + "fallback",
        $"{retriedLoad?.Message} | {fallbackLoad?.Message ?? "loaded"} | {requestWin.WebContents.GetUrl()}");
    var unfiltered = await Fetch("/unfiltered");
    Check("onBeforeRequest follows its filter", unfiltered == Page && Saw("before", "unfiltered") is null && await WaitFor(() => Saw("completed", "unfiltered")) is not null);

    var followed = await Fetch("/server-redirect");
    var redirect = await WaitFor(() => Saw("redirect", "server-redirect"));
    Check("onBeforeRedirect reports a server redirect", followed == "data" && redirect is { StatusCode: 302, RedirectUrl: Site + "data" },
        $"{followed} {redirect?.StatusCode} {redirect?.RedirectUrl}");

    // Details Electron fills from the request's body, frame and page.
    var getDetails = Saw("before", "data");
    var mainFrame = requestWin.WebContents.MainFrame;
    Check("details carry webContentsId and the requesting frame, and no uploadData without a body",
        getDetails is { UploadData: null, Frame: not null } && getDetails.WebContentsId == requestWin.WebContents.Id && ReferenceEquals(getDetails.Frame, mainFrame),
        $"{getDetails?.WebContentsId}/{requestWin.WebContents.Id} {getDetails?.Frame?.Url} {getDetails?.UploadData?.Count}");
    // Electron's initiatorOrigin: the page's origin for what it fetches, kept across a redirect, absent for loadURL.
    var origin = Site.TrimEnd('/');
    await requestWin.LoadUrl(Site + "browser-started");
    var browserStarted = await WaitFor(() => Saw("before", "browser-started"));
    Check("initiatorOrigin is the page's origin for its fetch and redirect, and absent for a navigation the app started",
        getDetails?.InitiatorOrigin == origin && redirect?.InitiatorOrigin == origin && browserStarted is { InitiatorOrigin: null },
        $"{getDetails?.InitiatorOrigin} | {redirect?.InitiatorOrigin} | {browserStarted?.InitiatorOrigin ?? "absent"}");
    var posted = await Fetch("/data", "{ method: 'POST', body: 'name=post%20test&type=string' }");
    var postBefore = Saw("before", "data");
    var postCompleted = await WaitFor(() => Saw("completed", "data") is { Method: "POST" } d ? d : null);
    static string? Body(WebRequestDetails? d) => d?.UploadData is [{ Type: "rawData", Bytes: { } bytes }] ? Encoding.UTF8.GetString(bytes) : null;
    Check("a POST's uploadData holds its bytes, in onBeforeRequest and onCompleted", posted == "data" && postBefore?.Method == "POST"
        && Body(postBefore) == "name=post%20test&type=string" && Body(postCompleted) == Body(postBefore), $"{Body(postBefore)} | {Body(postCompleted)}");

    await requestWin.WebContents.ExecuteJavaScriptAsync("new Promise(r => { const f = document.createElement('iframe'); f.name = 'sub'; f.src = '/frame'; f.onload = r; document.body.append(f); })");
    var iframe = requestWin.WebContents.MainFrame?.Frames.FirstOrDefault();
    var fromFrame = iframe is null ? null : (await iframe.ExecuteJavaScriptAsync("fetch('/from-frame').then(r => r.text())"))?.ToString();
    var frameRequest = await WaitFor(() => Saw("before", "from-frame"));
    Check("a request from an iframe carries that frame and the page's webContentsId", fromFrame == Page && iframe is not null
        && ReferenceEquals(frameRequest?.Frame, iframe) && frameRequest?.WebContentsId == requestWin.WebContents.Id,
        $"{fromFrame?.Length} {frameRequest?.Frame?.Url} {frameRequest?.WebContentsId}");
    var subFrameNav = Saw("before", "frame");
    Check("an iframe's navigation is a subFrame request of the page", subFrameNav is { ResourceType: "subFrame" } && subFrameNav.WebContentsId == requestWin.WebContents.Id,
        $"{subFrameNav?.ResourceType} frame={subFrameNav?.Frame?.Url ?? "null"}");

    // Requests without a page reach the session's listeners with no webContents, as Electron's.
    await requestWin.WebContents.ExecuteJavaScriptAsync("new SharedWorker('/shared.js'); 1");
    var fromShared = await WaitFor(() => Saw("completed", "from-shared"));
    Check("a shared worker's fetch reaches webRequest without a page", fromShared is { WebContents: null, WebContentsId: null, Frame: null } && Saw("before", "from-shared") is not null,
        fromShared is null ? "not seen" : $"{fromShared.WebContents is null} {fromShared.ResourceType}");
    var registered = (await requestWin.WebContents.ExecuteJavaScriptAsync("navigator.serviceWorker.register('/sw.js').then(() => 'ok', e => String(e))"))?.ToString();
    var fromServiceWorker = await WaitFor(() => Saw("completed", "from-sw"));
    Check("a service worker's fetch reaches webRequest without a page", registered == "ok" && fromServiceWorker is { WebContents: null, WebContentsId: null, Frame: null },
        fromServiceWorker is null ? $"{registered}; not seen" : $"{fromServiceWorker.ResourceType} script={Saw("before", "sw.js")?.WebContents is null}");

    // A custom scheme's requests are shown, as Electron's ("can capture CORS requests"); Oriel's sendSync channel is not.
    watched.Protocol.Handle(CustomScheme, _ => new ProtocolResponse
    {
        ContentType = "text/plain", Body = new MemoryStream(Encoding.UTF8.GetBytes("custom")),
        Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
    });
    var custom = await Fetch($"{CustomScheme}://app/data");
    var customDone = await WaitFor(() => events.LastOrDefault(s => s.Event == "completed" && s.Details.Url == $"{CustomScheme}://app/data").Details);
    Check("webRequest sees a request to a custom scheme a session protocol handler serves", custom == "custom" && customDone is { StatusCode: 200 }
        && events.Any(s => s.Event == "send" && s.Details.Url == $"{CustomScheme}://app/data"), $"{custom} {customDone?.StatusCode}");
    var syncFile = Path.Combine(Path.GetTempPath(), $"oriel-webrequest-sync-{Environment.ProcessId}.js");
    File.WriteAllText(syncFile, "oriel.contextBridge.exposeInMainWorld('sync', { ask: (v) => oriel.ipcRenderer.sendSync('webrequest-sync', v) });");
    var ipcWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = watched, Preload = syncFile } });
    await ipcWin.LoadUrl(Site);
    Action<IpcMainEvent> answerSync = e => e.ReturnValue = "sync:" + e.Args[0].GetString();
    IpcMain.On("webrequest-sync", answerSync);
    var syncAnswer = (await ipcWin.WebContents.ExecuteJavaScriptAsync("sync.ask('ok')"))?.ToString();
    IpcMain.RemoveListener("webrequest-sync", answerSync);
    await Task.Delay(300);
    Check("webRequest never sees Oriel's sendSync requests", syncAnswer == "sync:ok" && !events.Any(s => s.Details.Url.StartsWith("oriel-ipc:", StringComparison.Ordinal)),
        $"{syncAnswer} {events.Count(s => s.Details.Url.StartsWith("oriel-ipc:", StringComparison.Ordinal))}");
    ipcWin.Destroy();
    File.Delete(syncFile);
    // The default session's requests without a page come through the global request context's handler.
    var defaultSeen = new System.Collections.Concurrent.ConcurrentQueue<WebRequestDetails>();
    defaultSession.Protocol.Handle("https", r => new Uri(r.Url).AbsolutePath == "/shared.js"
        ? ProtocolResponse.Text("fetch('/from-default-shared');", "text/javascript") : ProtocolResponse.Text(Page, "text/html"));
    defaultSession.WebRequest.OnCompleted(null, defaultSeen.Enqueue);
    var defaultWin = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await defaultWin.LoadUrl(Site);
    await defaultWin.WebContents.ExecuteJavaScriptAsync("new SharedWorker('/shared.js'); 1");
    var defaultShared = await WaitFor(() => defaultSeen.FirstOrDefault(d => d.Url == Site + "from-default-shared"));
    defaultSession.WebRequest.OnCompleted(null, null);
    defaultSession.Protocol.Unhandle("https");
    defaultWin.Destroy();
    Check("the default session's webRequest sees a shared worker's fetch without a page", defaultShared is { WebContents: null, WebContentsId: null },
        defaultShared is null ? "not seen" : defaultShared.ResourceType);

    var typed = new System.Collections.Concurrent.ConcurrentQueue<string>();
    watched.WebRequest.OnCompleted(new WebRequestFilter { Types = ["mainFrame"] }, details => typed.Enqueue(details.Url));
    await Fetch("/data");
    await requestWin.LoadUrl(Site + "typed");
    await WaitFor(() => typed.IsEmpty ? null : "x");
    Check("a types filter limits the listener", typed.SequenceEqual([Site + "typed"]), string.Join(",", typed));

    watched.WebRequest.OnBeforeRequest(null, _ => throw new InvalidOperationException("listener failure"));
    var survived = await Fetch("/data");
    Check("a listener that throws leaves the request as it was", survived == "data", survived);
    watched.WebRequest.OnBeforeRequest(null, _ => Task.FromResult(new WebRequestResponse { Cancel = true }));
    watched.WebRequest.OnBeforeRequest(null);
    var countBefore = events.Count(s => s.Event == "before");
    var unwatched = await Fetch("/blocked");
    Check("OnBeforeRequest(null) removes the listener", unwatched == Page && events.Count(s => s.Event == "before") == countBefore, unwatched);
    Check("onSendHeaders sees a request no listener decides", await WaitFor(() => Saw("send", "blocked")) is not null);
    Check("an invalid URL pattern throws, as Electron's", await Throws(() =>
    {
        watched.WebRequest.OnCompleted(new WebRequestFilter { Urls = ["oriel.test"] }, _ => { });
        return Task.CompletedTask;
    }) is ArgumentException);
    requestWin.Destroy();

    // Session preload scripts: run in every page of the session, in order, before the page's own preload, as Electron's.
    var preloaded = Session.FromPartition("preloads");
    preloaded.Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    var preloadDir = Directory.CreateTempSubdirectory("oriel-preloads-").FullName;
    string PreloadFile(string name)
    {
        var path = Path.Combine(preloadDir, name + ".js");
        File.WriteAllText(path, $"(globalThis.order ??= []).push('{name}');" +
            "document.addEventListener('DOMContentLoaded', () => document.body.dataset.order = globalThis.order.join(','));");
        return path;
    }
    var s1 = preloaded.RegisterPreloadScript(new PreloadScriptRegistration("frame", PreloadFile("s1")) { Id = "first" });
    var s2 = preloaded.RegisterPreloadScript(new PreloadScriptRegistration("frame", PreloadFile("s2")));
    Check("RegisterPreloadScript keeps a given id and makes one otherwise; GetPreloadScripts lists them in order",
        s1 == "first" && Guid.TryParse(s2, out _) && preloaded.GetPreloadScripts().Select(p => (p.Id, p.Type, Path.GetFileName(p.FilePath))).SequenceEqual([("first", "frame", "s1.js"), (s2, "frame", "s2.js")]));
    var preloadWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = preloaded, Preload = PreloadFile("app") } });
    async Task<string?> Order(string path)
    {
        await preloadWin.LoadUrl(Site + path);
        return (await preloadWin.WebContents.ExecuteJavaScriptAsync("document.body.dataset.order ?? 'none'"))?.ToString();
    }
    var first = await Order("one");
    Check("session preloads run in order, before the page's own", first == "s1,s2,app", first);
    preloaded.RegisterPreloadScript(new PreloadScriptRegistration("frame", PreloadFile("s3")));
    var added = await Order("two");
    Check("a preload registered later runs from an open page's next load", added == "s1,s2,s3,app", added);
    preloaded.UnregisterPreloadScript("first");
    var removed = await Order("three");
    Check("UnregisterPreloadScript stops it from the next load", removed == "s2,s3,app" && preloaded.GetPreloadScripts().Count == 2, removed);
    preloaded.RegisterPreloadScript(new PreloadScriptRegistration("frame", Path.Combine(preloadDir, "missing.js")));
    var missing = await Order("four");
    Check("a missing preload file leaves the others running", missing == "s2,s3,app", missing);
    var throwing = Path.Combine(preloadDir, "throws.js");
    File.WriteAllText(throwing, "(globalThis.order ??= []).push('throws'); throw new TypeError('boom');");
    var unparsable = Path.Combine(preloadDir, "unparsable.js");
    File.WriteAllText(unparsable, "(globalThis.order ??= []).push('unparsable'); this is not javascript(");
    preloaded.RegisterPreloadScript(new PreloadScriptRegistration("frame", throwing));
    preloaded.RegisterPreloadScript(new PreloadScriptRegistration("frame", unparsable));
    var preloadErrors = new List<string>();
    var unableLogs = new List<string>();
    preloadWin.WebContents.PreloadError += (_, e) => { lock (preloadErrors) preloadErrors.Add($"{Path.GetFileName(e.PreloadPath)}:{e.ErrorName}:{e.Message.Replace(preloadDir, "~")}"); };
    preloadWin.WebContents.ConsoleMessage += (_, e) => { if (e.Message.StartsWith("Unable to load preload script: ", StringComparison.Ordinal)) lock (unableLogs) unableLogs.Add($"{e.Level}:{Path.GetFileName(e.Message)}"); };
    var failing = await Order("five");
    var reported = Until(() => { lock (preloadErrors) return preloadErrors.Count >= 3; }, TimeSpan.FromSeconds(5));
    string Errors() { lock (preloadErrors) return string.Join(" | ", preloadErrors); }
    Check("preload-error reports a missing, a throwing and an unparsable preload, and the rest still run, as Electron's",
        reported && failing == "s2,s3,throws,app" && Errors() == $"missing.js:Error:Cannot find module '~{Path.DirectorySeparatorChar}missing.js' | throws.js:TypeError:boom | unparsable.js:SyntaxError:Unexpected identifier 'is'",
        $"{failing} :: {Errors()}");
    string Unable() { lock (unableLogs) return string.Join(",", unableLogs); }
    Check("each failing preload logs Electron's 'Unable to load preload script' as an error",
        Until(() => Unable() == "Error:missing.js,Error:throws.js,Error:unparsable.js", TimeSpan.FromSeconds(5)), Unable());
    preloadWin.Destroy();
    var elsewhere = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = Session.FromPartition("preloads-otherOrder") } });
    Session.FromPartition("preloads-otherOrder").Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    await elsewhere.LoadUrl(Site);
    var otherOrder = (await elsewhere.WebContents.ExecuteJavaScriptAsync("document.body.dataset.order ?? 'none'"))?.ToString();
    Check("another session's pages run none of them", otherOrder == "none", otherOrder);
    elsewhere.Destroy();
    Check("RegisterPreloadScript refuses a relative path, a taken id and a service worker script, as Electron's",
        await Throws(() => Task.FromResult(preloaded.RegisterPreloadScript(new PreloadScriptRegistration("frame", "relative.js")))) is ArgumentException
        && await Throws(() => Task.FromResult(preloaded.RegisterPreloadScript(new PreloadScriptRegistration("frame", PreloadFile("again")) { Id = s2 }))) is ArgumentException
        && await Throws(() => Task.FromResult(preloaded.RegisterPreloadScript(new PreloadScriptRegistration("service-worker", PreloadFile("sw"))))) is NotSupportedException);
    // Electron's deprecated setPreloads replaces every frame preload, registered ones included; getPreloads lists their paths.
#pragma warning disable CS0618
    var legacy = Session.FromPartition("preloads-legacy");
    legacy.Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    legacy.RegisterPreloadScript(new PreloadScriptRegistration("frame", PreloadFile("registered")));
    legacy.SetPreloads([PreloadFile("legacyA"), PreloadFile("legacyB")]);
    var legacyList = legacy.GetPreloads();
    var legacyWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = legacy } });
    await legacyWin.LoadUrl(Site);
    var legacyOrder = (await legacyWin.WebContents.ExecuteJavaScriptAsync("document.body.dataset.order ?? 'none'"))?.ToString();
    legacy.SetPreloads([]);
    var emptied = legacy.GetPreloadScripts().Count;
#pragma warning restore CS0618
    legacyWin.Destroy();
    Check("SetPreloads replaces the frame preloads and GetPreloads lists them, as Electron's deprecated pair",
        legacyOrder == "legacyA,legacyB" && legacyList.SequenceEqual([PreloadFile("legacyA"), PreloadFile("legacyB")]) && emptied == 0,
        $"{legacyOrder} [{string.Join(",", legacyList.Select(Path.GetFileName))}] left={emptied}");
    // sendSync from a page in its own session reaches the main process too.
    var syncPreload = Path.Combine(preloadDir, "sync.js");
    File.WriteAllText(syncPreload, "oriel.contextBridge.exposeInMainWorld('sync', { ask: (v) => oriel.ipcRenderer.sendSync('session-sync', v) });");
    var syncSession = Session.FromPartition("sync-session");
    syncSession.Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    var syncWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = syncSession, Preload = syncPreload } });
    await syncWin.LoadUrl(Site);
    Action<IpcMainEvent> sessionSync = e => e.ReturnValue = "session:" + e.Args[0].GetString();
    IpcMain.On("session-sync", sessionSync);
    var sessionAnswer = (await syncWin.WebContents.ExecuteJavaScriptAsync("sync.ask('ok')"))?.ToString();
    IpcMain.RemoveListener("session-sync", sessionSync);
    Check("ipcRenderer.sendSync works from a page in another session", sessionAnswer == "session:ok", sessionAnswer);
    syncWin.Destroy();
    Directory.Delete(preloadDir, recursive: true);

    // Spell checker: Chromium's, driven through the session as Electron's.
    var spelling = Session.FromPartition("persist:spell");
    // A data: page: an https handler here would also answer Chromium's dictionary download.
    const string SpellPage = "data:text/html,<!doctype html><textarea id=t spellcheck=true style='font:20px monospace;width:400px;height:60px'></textarea>";
    Check("the spell checker is on by default, with a language", spelling.IsSpellCheckerEnabled() && (OperatingSystem.IsMacOS() || spelling.GetSpellCheckerLanguages().Count > 0),
        string.Join(",", spelling.GetSpellCheckerLanguages()));
    if (!OperatingSystem.IsMacOS())
    {
        spelling.SetSpellCheckerLanguages(["fr", "en-US"]);
        Check("SetSpellCheckerLanguages / GetSpellCheckerLanguages", spelling.GetSpellCheckerLanguages().SequenceEqual(["fr", "en-US"]), string.Join(",", spelling.GetSpellCheckerLanguages()));
        spelling.SetSpellCheckerLanguages(["en-US"]);
    }
    var badLanguage = await Throws(() => { spelling.SetSpellCheckerLanguages(["en-US", "xx"]); return Task.CompletedTask; });
    Check("an unknown language throws Electron's error", badLanguage is ArgumentException && badLanguage.Message.StartsWith("Invalid language code provided: \"xx\" is not a valid language code"), badLanguage?.Message);
    // On top: Chromium hides a page whose window other windows cover (Windows occlusion), and a hidden page drops mouse input.
    var spellWin = new BrowserWindow(new BrowserWindowOptions { Width = 500, Height = 200, AlwaysOnTop = true, WebPreferences = new WebPreferences { Session = spelling } });
    await spellWin.LoadUrl(SpellPage);
    // Chromium checks what is typed, once a word ends.
    async Task TypeMisspelling()
    {
        spellWin.Focus();
        await spellWin.WebContents.ExecuteJavaScriptAsync("t.value = ''; t.focus(); 1", userGesture: true);
        foreach (var c in "helo world ") spellWin.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.Char, c.ToString()));
        await Task.Delay(1500);
    }
    async Task<ContextMenuParams?> RightClickFirstWord()
    {
        var asked = new TaskCompletionSource<ContextMenuParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ContextMenuParams> onMenu = (_, p) => asked.TrySetResult(p);
        spellWin.WebContents.ContextMenu += onMenu;
        var x = int.Parse((await spellWin.WebContents.ExecuteJavaScriptAsync("String(Math.round(t.getBoundingClientRect().left + 20))"))!.ToString()!);
        var y = int.Parse((await spellWin.WebContents.ExecuteJavaScriptAsync("String(Math.round(t.getBoundingClientRect().top + 14))"))!.ToString()!);
        spellWin.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, x, y) { Button = MouseButton.Right });
        spellWin.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseUp, x, y) { Button = MouseButton.Right });
        var menu = await asked.Task.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
        spellWin.WebContents.ContextMenu -= onMenu;
        return menu;
    }
    // Linux downloads the dictionary first, and text typed before it arrives stays unchecked. Windows' platform checker
    // has no readiness signal, so each attempt's state goes in the detail.
    ContextMenuParams? misspelled = null;
    var attempts = new List<string>();
    var spellClock = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 15 && misspelled?.MisspelledWord is not { Length: > 0 }; i++)
    {
        await TypeMisspelling();
        misspelled = await RightClickFirstWord();
        var pageState = await spellWin.WebContents.ExecuteJavaScriptAsync("document.visibilityState + (document.hasFocus() ? '+focus' : '-focus')");
        attempts.Add($"{(misspelled is null ? "nomenu" : misspelled.MisspelledWord is { Length: > 0 } w ? w : "-")}/{pageState}@{spellClock.Elapsed.TotalSeconds:F0}s");
    }
    Check("a misspelled word reaches the context menu with suggestions", misspelled is { MisspelledWord: "helo" } && misspelled.DictionarySuggestions.Contains("hello"),
        $"{misspelled?.MisspelledWord} [{string.Join(",", misspelled?.DictionarySuggestions ?? [])}] menu={misspelled is not null} editable={misspelled?.IsEditable} value={await spellWin.WebContents.ExecuteJavaScriptAsync("t.value")} attempts={string.Join(" ", attempts)}");
    spellWin.WebContents.ReplaceMisspelling("hello");
    await Task.Delay(300);
    var replaced = (await spellWin.WebContents.ExecuteJavaScriptAsync("t.value"))?.ToString();
    Check("ReplaceMisspelling replaces the word", replaced == "hello world ", replaced);
    Check("AddWordToSpellCheckerDictionary adds to a persistent session's dictionary", spelling.AddWordToSpellCheckerDictionary("orielword")
        && (await spelling.ListWordsInSpellCheckerDictionaryAsync()).Contains("orielword"));
    var memoryPage = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = Session.FromPartition("spell-memory") } });
    await memoryPage.LoadUrl(SpellPage);
    Check("an in-memory session has no dictionary to add to, as Electron's", !Session.FromPartition("spell-memory").AddWordToSpellCheckerDictionary("orielword"));
    memoryPage.Destroy();
    spelling.SetSpellCheckerEnabled(false);
    await spellWin.LoadUrl(SpellPage + "<!-- off -->");
    await TypeMisspelling();
    var unchecked_ = await RightClickFirstWord();
    Check("SetSpellCheckerEnabled(false) turns it off", !spelling.IsSpellCheckerEnabled() && unchecked_ is { MisspelledWord: "" }, unchecked_?.MisspelledWord);
    spelling.SetSpellCheckerEnabled(true);
    Check("IsSpellCheckerEnabled follows SetSpellCheckerEnabled", spelling.IsSpellCheckerEnabled());
    spellWin.Destroy();
    Check("removing a dictionary word and the download URL are not supported (CEF)",
        await Throws(() => Task.FromResult(spelling.RemoveWordFromSpellCheckerDictionary("orielword"))) is NotSupportedException
        && await Throws(() => { spelling.SetSpellCheckerDictionaryDownloadUrl("https://x/"); return Task.CompletedTask; }) is NotSupportedException);

    // certificate-error: a self-signed TLS server; the page's handler runs, then the app's, as Electron's.
    using var tlsKey = System.Security.Cryptography.RSA.Create(2048);
    var tlsRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=oriel-tls.test, O=Oriel Tests", tlsKey,
        System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    using var tlsSelfSigned = tlsRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    using var tlsCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
        tlsSelfSigned.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12), null);
    using var tlsListener = new TcpListener(IPAddress.Loopback, 0);
    tlsListener.Start();
    var tlsUrl = $"https://127.0.0.1:{((IPEndPoint)tlsListener.LocalEndpoint).Port}/";
    _ = ServeTls(tlsListener, tlsCert);
    async Task<(string? Body, List<string> Order, CertificateErrorEventArgs? Args)> VisitTls(string partition, Action<CertificateErrorEventArgs>? decide)
    {
        var order = new List<string>();
        CertificateErrorEventArgs? seenArgs = null;
        var w = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = partition } });
        w.WebContents.CertificateError += (_, e) =>
        {
            order.Add("page");
            seenArgs = e;
            decide?.Invoke(e);
        };
        EventHandler<CertificateErrorEventArgs> onApp = (sender, e) => order.Add(ReferenceEquals(sender, app) && ReferenceEquals(e, seenArgs) ? "app" : "app?");
        app.CertificateError += onApp;
        var failed = await Throws(() => w.LoadUrl(tlsUrl).WaitAsync(TimeSpan.FromSeconds(15)));
        var body = failed is null ? (await w.WebContents.ExecuteJavaScriptAsync("document.body.innerText"))?.ToString() : null;
        app.CertificateError -= onApp;
        w.Destroy();
        return (body, order, seenArgs);
    }
    var refusedTls = await VisitTls("tls-default", null);
    Check("an invalid certificate is refused by default, as Electron's", refusedTls.Body is null && refusedTls.Order.SequenceEqual(["page", "app"]), $"{refusedTls.Body} [{string.Join(",", refusedTls.Order)}]");
    var tlsArgs = refusedTls.Args;
    Check("certificate-error carries the URL, error and certificate", tlsArgs is { Url: var u, Error: var err, IsMainFrame: true } && u == tlsUrl && err.StartsWith("net::ERR_CERT_")
        && tlsArgs.Certificate is { SubjectName: "oriel-tls.test", IssuerName: "oriel-tls.test" } && tlsArgs.Certificate.Fingerprint == "sha256/" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(tlsCert.RawData)),
        $"{tlsArgs?.Url} {tlsArgs?.Error} {tlsArgs?.Certificate?.SubjectName}");
    var trustedTls = await VisitTls("tls-trust", e => { e.Cancel = true; e.Callback(true); });
    Check("preventDefault and callback(true) trust it", trustedTls.Body == "tls ok", trustedTls.Body);
    var laterTls = await VisitTls("tls-later", e => { e.Cancel = true; _ = Task.Delay(300).ContinueWith(_ => e.Callback(true)); });
    Check("the callback may answer later", laterTls.Body == "tls ok", laterTls.Body);
    var deniedTls = await VisitTls("tls-deny", e => { e.Cancel = true; e.Callback(false); });
    Check("preventDefault and callback(false) refuse it", deniedTls.Body is null, deniedTls.Body);
    var framed = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "tls-frame" } });
    var frameErrors = 0;
    framed.WebContents.CertificateError += (_, e) => { frameErrors++; e.Cancel = true; e.Callback(true); };
    await framed.LoadUrl("data:text/html,<iframe src='" + tlsUrl + "'></iframe>");
    await Task.Delay(3000);
    // Drift E26: Electron raises it with isMainFrame false; CEF refuses a subframe's certificate without asking.
    Check("a subframe's invalid certificate raises no event (CEF)", frameErrors == 0, frameErrors);
    framed.Destroy();
    tlsListener.Stop();

    // login: an HTTP server asking for Basic credentials; the page's handler runs, then the app's.
    using var authListener = new TcpListener(IPAddress.Loopback, 0);
    authListener.Start();
    var authPort = ((IPEndPoint)authListener.LocalEndpoint).Port;
    _ = ServeBasicAuth(authListener);
    async Task<(string? Body, List<string> Order, LoginEventArgs? Args)> VisitAuth(Action<LoginEventArgs>? decide)
    {
        var order = new List<string>();
        LoginEventArgs? seenArgs = null;
        var w = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "auth-" + Guid.NewGuid() } });
        w.WebContents.Login += (_, e) =>
        {
            order.Add("page");
            seenArgs = e;
            decide?.Invoke(e);
        };
        EventHandler<LoginEventArgs> onApp = (sender, e) => order.Add(ReferenceEquals(sender, app) && ReferenceEquals(e, seenArgs) ? "app" : "app?");
        app.Login += onApp;
        await Throws(() => w.LoadUrl($"http://127.0.0.1:{authPort}/private").WaitAsync(TimeSpan.FromSeconds(15)));
        var body = (await w.WebContents.ExecuteJavaScriptAsync("document.body?.innerText ?? ''"))?.ToString();
        app.Login -= onApp;
        w.Destroy();
        return (body, order, seenArgs);
    }
    var noLogin = await VisitAuth(null);
    Check("login is cancelled by default, as Electron's", noLogin.Body == "denied" && noLogin.Order.SequenceEqual(["page", "app"]), $"{noLogin.Body} [{string.Join(",", noLogin.Order)}]");
    Check("login carries the URL and the challenge", noLogin.Args is { AuthInfo: { IsProxy: false, Scheme: "basic", Host: "127.0.0.1", Realm: "oriel" } info } && info.Port == authPort
        && noLogin.Args.Url.StartsWith($"http://127.0.0.1:{authPort}"), $"{noLogin.Args?.Url} {noLogin.Args?.AuthInfo}");
    var loggedIn = await VisitAuth(e => { e.Cancel = true; e.Callback("oriel", "secret"); });
    Check("preventDefault and callback(user, password) log in", loggedIn.Body == "logged in", loggedIn.Body);
    var wrong = await VisitAuth(e => { e.Cancel = true; e.Callback(); });
    Check("callback() with no credentials cancels", wrong.Body == "denied", wrong.Body);
    authListener.Stop();

    // Navigation events, as Electron's: who started it decides will-navigate; commits report their status.
    var navSession = Session.FromPartition("nav");
    navSession.Protocol.Handle("https", request => new Uri(request.Url).AbsolutePath switch
    {
        "/" or "/start" => ProtocolResponse.Text("<!doctype html><a id=go href='/next'>go</a><iframe name=f src='/frameNav'></iframe>", "text/html"),
        "/missing" => ProtocolResponse.Text("<!doctype html>missing", "text/html", 404),
        "/redirect" => new ProtocolResponse { StatusCode = 302, Headers = new Dictionary<string, string> { ["Location"] = Site + "next" } },
        _ => ProtocolResponse.Text("<!doctype html>" + new Uri(request.Url).AbsolutePath, "text/html"),
    });
    var navWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = navSession } });
    var navLog = new List<string>();
    var cancelNext = "";
    void Log(string name, string url, bool isMain) => navLog.Add($"{name} {new Uri(url).PathAndQuery}{new Uri(url).Fragment}{(isMain ? "" : " sub")}");
    navWin.WebContents.DidStartNavigation += (_, e) => Log("start" + (e.IsSameDocument ? "-same" : ""), e.Url, e.IsMainFrame);
    navWin.WebContents.WillFrameNavigate += (_, e) => { Log("will-frame", e.Url, e.IsMainFrame); if (cancelNext == "will-frame") e.Cancel = true; };
    navWin.WebContents.WillNavigate += (_, e) => { Log("will", e.Url, e.IsMainFrame); if (cancelNext == "will") e.Cancel = true; };
    navWin.WebContents.WillRedirect += (_, e) => { Log("will-redirect", e.Url, e.IsMainFrame); if (cancelNext == "will-redirect") e.Cancel = true; };
    navWin.WebContents.DidRedirectNavigation += (_, e) => Log("did-redirect", e.Url, e.IsMainFrame);
    navWin.WebContents.DidFrameNavigate += (_, e) => Log($"did-frame {e.HttpResponseCode} {e.HttpStatusText}", e.Url, e.IsMainFrame);
    navWin.WebContents.DidNavigate += (_, e) => Log($"did {e.HttpResponseCode} {e.HttpStatusText}", e.Url, e.IsMainFrame);
    navWin.WebContents.DidNavigateInPage += (_, e) => Log("in-page", e.Url, e.IsMainFrame);
    // Runs act, then waits for the log to show until (if given) and a little more for stragglers.
    async Task<string> Steps(Func<Task> act, string cancel = "", string until = "")
    {
        navLog.Clear();
        cancelNext = cancel;
        await act();
        for (var i = 0; i < 100 && until.Length > 0 && !string.Join(" | ", navLog).Contains(until); i++) await Task.Delay(50);
        await Task.Delay(700);
        cancelNext = "";
        return string.Join(" | ", navLog);
    }
    Task Js(string code) => navWin.WebContents.ExecuteJavaScriptAsync(code + "; 1", userGesture: true);
    var loaded = await Steps(() => navWin.LoadUrl(Site + "start"));
    Check("LoadUrl starts a navigation but raises no will-navigate, as Electron's", loaded.StartsWith("start /start | did-frame 200 OK /start | did 200 OK /start") && !loaded.Contains("will /") && !loaded.Contains("will-frame /start"), loaded);
    var clicked = await Steps(() => Js("go.click()"));
    Check("a link click: did-start-navigation, will-frame-navigate, will-navigate, then the commit with its status",
        clicked.StartsWith("start /next | will-frame /next | will /next | did-frame 200 OK /next | did 200 OK /next"), clicked);
    await navWin.LoadUrl(Site + "start");
    var stopped = await Steps(() => Js("go.click()"), "will");
    Check("preventDefault in will-navigate keeps the page", stopped == "start /next | will-frame /next | will /next" && navWin.WebContents.GetUrl() == Site + "start", $"{stopped} @ {navWin.WebContents.GetUrl()}");
    var reloaded = await Steps(() => { navWin.WebContents.Reload(); return Task.CompletedTask; }, until: "did 200 OK /start");
    Check("Reload raises no will-navigate", reloaded.Contains("did 200 OK /start") && !reloaded.Contains("will /") && !reloaded.Contains("will-frame /start"), reloaded);
    var pageReload = await Steps(() => Js("location.reload()"), until: "did 200 OK /start");
    Check("the page's own location.reload() is the page's navigation: will-navigate", pageReload.StartsWith("start /start | will-frame /start | will /start"), pageReload);
    var hashed = await Steps(() => Js("location.hash = 'part'"));
    Check("a fragment change: did-start-navigation (same document) and did-navigate-in-page, no will-navigate",
        hashed == "start-same /start#part | in-page /start#part", hashed);
    var pushed = await Steps(() => Js("history.pushState({}, '', '/pushed')"));
    Check("history.pushState raises did-navigate-in-page", pushed.Contains("in-page /pushed") && !pushed.Contains("will"), pushed);
    // The iframe's commit comes through DevTools and can trail the load, so wait for it before the next step clears the log.
    await Steps(() => navWin.LoadUrl(Site + "start"), until: "did-frame 200 OK /frameNav sub");
    var frameNav = await Steps(() => Js("frames.f.location = '/elsewhere'"));
    var frameArgs = navWin.WebContents.MainFrame?.Frames.FirstOrDefault();
    Check("an iframe's navigation: will-frame-navigate and did-frame-navigate for that frame, no will-navigate or did-navigate",
        frameNav.StartsWith("start /elsewhere sub | will-frame /elsewhere sub | did-frame 200 OK /elsewhere sub") && !frameNav.Contains("will /") && !frameNav.Contains("did 200"), frameNav);
    WebFrameMain? frameSeen = null;
    EventHandler<NavigationEventArgs> onFrame = (_, e) => frameSeen = e.Frame;
    navWin.WebContents.WillFrameNavigate += onFrame;
    var frameStopped = await Steps(() => Js("frames.f.location = '/blocked-frame'"), "will-frame");
    navWin.WebContents.WillFrameNavigate -= onFrame;
    Check("will-frame-navigate names the frame, and preventDefault stops it", frameSeen is { Name: "f" } && frameSeen == frameArgs && frameStopped == "start /blocked-frame sub | will-frame /blocked-frame sub"
        && (await frameSeen.ExecuteJavaScriptAsync("location.pathname"))?.ToString() == "/elsewhere", $"{frameSeen?.Name} {frameStopped} at={(frameSeen is null ? "" : await frameSeen.ExecuteJavaScriptAsync("location.pathname"))}");
    Check("after the main frame loads a new document, Frames lists only that document's iframes", navWin.WebContents.MainFrame?.Frames.Count == 1,
        navWin.WebContents.MainFrame?.Frames.Count);
    var redirected = await Steps(() => navWin.LoadUrl(Site + "redirect"));
    Check("a server redirect: will-redirect and did-redirect-navigation, then the commit", redirected == "start /redirect | will-redirect /next | did-redirect /next | did-frame 200 OK /next | did 200 OK /next", redirected);
    var redirectStopped = await Steps(() => Throws(() => navWin.LoadUrl(Site + "redirect").WaitAsync(TimeSpan.FromSeconds(10))), "will-redirect");
    Check("preventDefault in will-redirect stops the navigation", redirectStopped == "start /redirect | will-redirect /next" && navWin.WebContents.GetUrl() == Site + "next", $"{redirectStopped} @ {navWin.WebContents.GetUrl()}");
    var notFound = await Steps(() => navWin.LoadUrl(Site + "missing"));
    Check("did-navigate reports an error status", notFound.EndsWith("did 404 Not Found /missing"), notFound);
    var unreachable = await Steps(() => Throws(() => navWin.LoadUrl("http://oriel-nowhere.invalid/").WaitAsync(TimeSpan.FromSeconds(15))), until: "start /");
    Check("a navigation that fails commits no did-navigate, as Electron's", unreachable.StartsWith("start /") && !unreachable.Contains("did "), unreachable);
    navWin.Destroy();

    // contentTracing: Chromium's trace, as Electron's.
    var traced = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "trace" } });
    Session.FromPartition("trace").Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    await traced.LoadUrl(Site);
    async Task<(string Path, string[] Categories)> Trace(Func<Task> start, string? path = null)
    {
        await start();
        await traced.LoadUrl(Site + "traced");
        var written = await ContentTracing.StopRecordingAsync(path);
        using var json = JsonDocument.Parse(File.ReadAllText(written));
        var cats = json.RootElement.GetProperty("traceEvents").EnumerateArray()
            .Select(e => e.TryGetProperty("cat", out var c) ? c.GetString() ?? "" : "").Distinct().ToArray();
        return (written, cats);
    }
    var included = await Trace(() => ContentTracing.StartRecordingAsync(new TraceConfig { IncludedCategories = ["devtools.timeline"] }));
    Check("StartRecordingAsync records the included categories only; StopRecordingAsync writes a temporary trace file",
        File.Exists(included.Path) && included.Categories.Contains("devtools.timeline")
        && included.Categories.All(c => c is "__metadata" or "" || c.Split(',').Contains("devtools.timeline")), string.Join(",", included.Categories));
    File.Delete(included.Path);
    var chosenPath = Path.Combine(Path.GetTempPath(), $"oriel-trace-{Environment.ProcessId}.json");
    // An event in several categories stays while any of them is recorded, so only devtools.timeline alone is gone.
    var excluded = await Trace(() => ContentTracing.StartRecordingAsync("-devtools.timeline"), chosenPath);
    Check("an excluding filter leaves the category out, and the trace goes to the path given",
        excluded.Path == chosenPath && File.Exists(chosenPath) && excluded.Categories.Length > 1 && !excluded.Categories.Contains("devtools.timeline"), string.Join(",", excluded.Categories.Take(8)));
    File.Delete(chosenPath);
    var categories = await ContentTracing.GetCategoriesAsync();
    Check("GetCategoriesAsync lists Chromium's categories, sorted", categories.Count > 20 && categories.Contains("toplevel") && categories.Contains("blink")
        && categories.SequenceEqual(categories.Order(StringComparer.Ordinal)), $"{categories.Count}: {string.Join(",", categories.Take(6))}");
    var idle = await ContentTracing.GetTraceBufferUsageAsync();
    Check("GetTraceBufferUsageAsync is zeros when not recording, as Electron's", idle is { Value: 0, Percentage: 0 }, idle);
    var mixed = await Trace(() => ContentTracing.StartRecordingAsync(new TraceConfig { IncludedCategories = ["*"], ExcludedCategories = ["toplevel"] }));
    Check("a TraceConfig with included and excluded categories records the first without the second",
        mixed.Categories.Length > 1 && !mixed.Categories.Contains("toplevel") && mixed.Categories.Contains("blink"), string.Join(",", mixed.Categories.Take(8)));
    File.Delete(mixed.Path);
    // A recording with options runs in the first page's DevTools session, which traces that page's renderer (drift P14).
    var host = WebContents.GetAllWebContents().First(w => !w.IsDestroyed());
    async Task<(string[] Categories, string[] Marks, string Path)> TraceInHost(Func<Task> start, string script, string? path = null)
    {
        await start();
        await host.ExecuteJavaScriptAsync(script);
        var written = await ContentTracing.StopRecordingAsync(path);
        using var json = JsonDocument.Parse(File.ReadAllText(written));
        File.Delete(written);
        var events = json.RootElement.GetProperty("traceEvents").EnumerateArray().ToArray();
        var cats = events.Select(e => e.TryGetProperty("cat", out var c) ? c.GetString() ?? "" : "").Distinct().ToArray();
        var marks = events.Where(e => e.TryGetProperty("cat", out var c) && c.GetString() == "blink.user_timing" && e.TryGetProperty("name", out _))
            .Select(e => e.GetProperty("name").GetString()!).Distinct().ToArray();
        return (cats, marks, written);
    }
    const string MarkScript = "performance.mark('oriel-mark'); new Promise(r => setTimeout(r, 700))";
    Exception? again = null;
    var bufferSized = await TraceInHost(async () =>
    {
        await ContentTracing.StartRecordingAsync(new TraceConfig { IncludedCategories = ["blink.user_timing", "devtools.timeline"], ExcludedCategories = ["devtools.timeline"], TraceBufferSizeInKb = 8192 });
        again = await Throws(() => ContentTracing.StartRecordingAsync("blink"));
    }, MarkScript + ".then(() => new Promise(r => setTimeout(r, 300)))");
    idle = await ContentTracing.GetTraceBufferUsageAsync();
    Check("a TraceConfig with a buffer size records through DevTools only the categories it includes, the page's renderer among them",
        bufferSized.Marks.Contains("oriel-mark") && bufferSized.Categories.All(c => c is "__metadata" or "" || c.Split(',').Any(p => p is "blink.user_timing" or "devtools.timeline")),
        $"{string.Join(",", bufferSized.Categories.Take(8))} marks={string.Join(",", bufferSized.Marks)}");
    Check("starting while recording resolves, as Electron's", again is null, again?.Message);
    TraceBufferUsage? recordingUsage = null;
    var excludedByConfig = await TraceInHost(async () =>
    {
        await ContentTracing.StartRecordingAsync(new TraceConfig { IncludedCategories = ["*"], ExcludedCategories = ["toplevel", "blink.user_timing"], EnableArgumentFilter = true });
    }, MarkScript + ".then(async () => { await new Promise(r => setTimeout(r, 300)); })");
    Check("a DevTools TraceConfig leaves out its excluded categories", excludedByConfig.Categories.Length > 2 && excludedByConfig.Marks.Length == 0
        && !excludedByConfig.Categories.Contains("toplevel") && !excludedByConfig.Categories.Contains("blink.user_timing"), string.Join(",", excludedByConfig.Categories.Take(8)));
    var usageRun = await TraceInHost(async () =>
    {
        await ContentTracing.StartRecordingAsync("blink.user_timing,devtools.timeline", "record-as-much-as-possible");
        await host.ExecuteJavaScriptAsync("for (let i = 0; i < 3000; i++) performance.mark('fill' + i); new Promise(r => setTimeout(r, 700))");
        recordingUsage = await ContentTracing.GetTraceBufferUsageAsync();
    }, MarkScript);
    Check("traceOptions start a DevTools recording of the filter's categories", usageRun.Marks.Contains("oriel-mark") && usageRun.Marks.Contains("fill2999"), usageRun.Marks.Length);
    Check("GetTraceBufferUsageAsync while recording: Value 0 as Electron's, Percentage in (0, 1]; zeros again once stopped",
        recordingUsage is { Value: 0, Percentage: > 0 and <= 1 } && idle is { Value: 0, Percentage: 0 }, $"{recordingUsage} / {idle}");
    // A 512 KB buffer that fills: until-full keeps the first marks and drops the rest; continuously keeps the last.
    const string FillScript = "performance.mark('first'); for (let i = 0; i < 30000; i++) performance.mark('m' + i); performance.mark('last'); new Promise(r => setTimeout(r, 500))";
    var untilFull = await TraceInHost(() => ContentTracing.StartRecordingAsync(new TraceConfig { IncludedCategories = ["blink.user_timing"], ExcludedCategories = ["*"], TraceBufferSizeInKb = 512 }), FillScript, chosenPath);
    Check("a DevTools recording goes to the path given", untilFull.Path == chosenPath && !File.Exists(chosenPath), untilFull.Path);
    var ring = await TraceInHost(() => ContentTracing.StartRecordingAsync(new TraceConfig { IncludedCategories = ["blink.user_timing"], ExcludedCategories = ["*"], TraceBufferSizeInKb = 512, RecordingMode = "record-continuously" }),
        FillScript + ".then(() => new Promise(r => setTimeout(r, 6000))).then(() => { for (let i = 0; i < 50; i++) performance.mark('tail'); return new Promise(r => setTimeout(r, 500)); })");
    Check("RecordingMode and TraceBufferSizeInKb reach Chromium: until-full keeps the start, record-continuously the end (re-interned after Chromium clears incremental state)",
        untilFull.Marks.Contains("first") && !untilFull.Marks.Contains("last") && !untilFull.Marks.Contains("tail") && ring.Marks.Contains("tail") && !ring.Marks.Contains("first"),
        $"untilFull first={untilFull.Marks.Contains("first")} last={untilFull.Marks.Contains("last")} n={untilFull.Marks.Length}; ring first={ring.Marks.Contains("first")} tail={ring.Marks.Contains("tail")} n={ring.Marks.Length}");
    await ContentTracing.StartRecordingAsync("blink");
    var twice = await Throws(() => ContentTracing.StartRecordingAsync("blink"));
    var cefRecordingUsage = await Throws(() => ContentTracing.GetTraceBufferUsageAsync());
    File.Delete(await ContentTracing.StopRecordingAsync());
    var unstarted = await Throws(() => ContentTracing.StopRecordingAsync());
    Check("starting twice resolves; stopping what was not started fails with Electron's message",
        twice is null && unstarted is InvalidOperationException { Message: "Failed to stop tracing - no trace in progress" }, $"{twice?.Message} / {unstarted?.Message}");
    Check("buffer usage of a categories-only recording throws NotSupportedException (drift P14)", cefRecordingUsage is NotSupportedException, cefRecordingUsage?.GetType().Name);
    Check("options no route carries throw NotSupportedException (drift P15)",
        await Throws(() => ContentTracing.StartRecordingAsync(new TraceConfig { IncludedProcessIds = [Environment.ProcessId] })) is NotSupportedException
        && await Throws(() => ContentTracing.StartRecordingAsync(new TraceConfig { HistogramNames = ["Memory.Browser"] })) is NotSupportedException);
    traced.Destroy();

    // select-client-certificate: a TLS server that asks for one; the first is used unless a handler chooses.
    if (clientCertificates)
    {
        using var mutualListener = new TcpListener(IPAddress.Loopback, 0);
        mutualListener.Start();
        var mutualUrl = $"https://127.0.0.1:{((IPEndPoint)mutualListener.LocalEndpoint).Port}/";
        _ = ServeTls(mutualListener, tlsCert, requireClient: true);
        async Task<(string? Body, List<string> Order, SelectClientCertificateEventArgs? Args)> VisitMutual(Action<SelectClientCertificateEventArgs>? decide)
        {
            var order = new List<string>();
            SelectClientCertificateEventArgs? seenArgs = null;
            var w = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "mutual-" + Guid.NewGuid() } });
            w.WebContents.CertificateError += (_, e) => { e.Cancel = true; e.Callback(true); };
            w.WebContents.SelectClientCertificate += (_, e) => { order.Add("page"); seenArgs = e; decide?.Invoke(e); };
            EventHandler<SelectClientCertificateEventArgs> onApp = (sender, e) => order.Add(ReferenceEquals(sender, app) && ReferenceEquals(e, seenArgs) ? "app" : "app?");
            app.SelectClientCertificate += onApp;
            var failed = await Throws(() => w.LoadUrl(mutualUrl).WaitAsync(TimeSpan.FromSeconds(15)));
            var body = failed is null ? (await w.WebContents.ExecuteJavaScriptAsync("document.body.innerText"))?.ToString() : failed.Message;
            app.SelectClientCertificate -= onApp;
            w.Destroy();
            return (body, order, seenArgs);
        }
        var firstPick = await VisitMutual(null);
        Check("with no choice, the first client certificate is used, as Electron's", firstPick.Body is "tls ok client=oriel-client-1" or "tls ok client=oriel-client-2"
            && firstPick.Order.SequenceEqual(["page", "app"]), $"{firstPick.Body} [{string.Join(",", firstPick.Order)}]");
        var list = firstPick.Args?.CertificateList.Select(c => c.SubjectName).ToList() ?? [];
        Check("select-client-certificate carries host:port and the candidates", firstPick.Args?.Url == mutualUrl[8..^1]
            && list.Order().SequenceEqual(["oriel-client-1", "oriel-client-2"]) && firstPick.Body == "tls ok client=" + list[0], $"{firstPick.Args?.Url} {string.Join(",", list)}");
        string? picked = null;
        var chosen = await VisitMutual(e => { e.Cancel = true; picked = e.CertificateList[^1].SubjectName; e.Callback(e.CertificateList[^1]); });
        Check("preventDefault and callback(certificate) use that certificate, not the first", picked is not null && picked != list.FirstOrDefault()
            && chosen.Body == "tls ok client=" + picked, $"{picked}: {chosen.Body}");
        var none = await VisitMutual(e => { e.Cancel = true; _ = Task.Delay(200).ContinueWith(_ => e.Callback()); });
        Check("callback() with none continues without a certificate", none.Body == "tls ok client=none", none.Body);
        mutualListener.Stop();
    }
    if (OperatingSystem.IsWindows() && !mediaChild) await WindowsClientCertificateChecks(tlsCert);

    // app events, as Electron's: creations in order, sessions once each, window focus.
    static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < end) Thread.Sleep(50);
        return condition();
    }
    var appCreated = new System.Collections.Concurrent.ConcurrentQueue<string>();
    EventHandler<WebContents> onPage = (sender, page) => appCreated.Enqueue(ReferenceEquals(sender, app) ? "web-contents" : "web-contents?");
    EventHandler<BrowserWindow> onWindow = (_, window) => appCreated.Enqueue("browser-window:" + window.Id);
    app.WebContentsCreated += onPage;
    app.BrowserWindowCreated += onWindow;
    var made = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "app-events" } });
    Check("a new window raises WebContentsCreated, then BrowserWindowCreated with the window", appCreated.SequenceEqual(["web-contents", "browser-window:" + made.Id]), string.Join(",", appCreated));
    Session.FromPartition("app-events").Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
    await made.LoadUrl(Site);
    appCreated.Clear();
    await made.WebContents.ExecuteJavaScriptAsync("window.open('https://oriel.test/popup'); 1", userGesture: true);
    Check("a window the page opens raises them too", Until(() => appCreated.Count >= 2, TimeSpan.FromSeconds(10)) && appCreated.First() == "web-contents" && appCreated.Any(c => c.StartsWith("browser-window:")),
        string.Join(",", appCreated));
    app.WebContentsCreated -= onPage;
    app.BrowserWindowCreated -= onWindow;
    foreach (var w in BrowserWindow.GetAllWindows().Where(w => w != made && w.WebContents.Session == Session.FromPartition("app-events"))) w.Destroy();
    made.Destroy();

    var sessions = new List<string>();
    EventHandler<Session> onSession = (_, s) =>
    {
        sessions.Add(s.IsPersistent() ? "disk" : "memory");
        // A handler may ask for another session.
        if (sessions.Count == 1) Session.FromPartition("app-events-nested");
    };
    app.SessionCreated += onSession;
    var fresh = Session.FromPartition("app-events-fresh");
    Session.FromPartition("app-events-fresh");
    Session.FromPartition("app-events");
    app.SessionCreated -= onSession;
    Check("SessionCreated is raised once for each new session, and a handler may ask for another", sessions.SequenceEqual(["memory", "memory"]) && fresh is not null,
        string.Join(",", sessions));

    var focusLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var focusA = new BrowserWindow(new BrowserWindowOptions { Title = "app focus 1", Width = 300, Height = 200 });
    var focusB = new BrowserWindow(new BrowserWindowOptions { Title = "app focus 2", Width = 300, Height = 200, X = 400, Y = 100 });
    await Task.Delay(500);
    EventHandler<BrowserWindow> onFocus = (_, w) => focusLog.Enqueue("focus:" + (w == focusA ? 1 : w == focusB ? 2 : 0));
    EventHandler<BrowserWindow> onBlur = (_, w) => focusLog.Enqueue("blur:" + (w == focusA ? 1 : w == focusB ? 2 : 0));
    app.BrowserWindowFocus += onFocus;
    app.BrowserWindowBlur += onBlur;
    focusA.Focus();
    var focusGot = Until(() => focusLog.Contains("focus:1"), TimeSpan.FromSeconds(5));
    focusB.Focus();
    var focusMoved = Until(() => focusLog.Contains("blur:1") && focusLog.Contains("focus:2"), TimeSpan.FromSeconds(5));
    app.BrowserWindowFocus -= onFocus;
    app.BrowserWindowBlur -= onBlur;
    Check("BrowserWindowFocus and BrowserWindowBlur follow focus between windows", focusGot && focusMoved, string.Join(",", focusLog));
    focusA.Destroy();
    focusB.Destroy();

    // app.getPath / setPath, as Electron's.
    Check("GetPath(userData) is where the default session keeps its data", app.GetPath("userData") == defaultSession.GetStoragePath() && app.GetPath("sessionData") == app.GetPath("userData"),
        $"{app.GetPath("userData")} vs {defaultSession.GetStoragePath()}");
    Check("GetPath(home), (temp) and (exe) are the OS's", app.GetPath("home") == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        && app.GetPath("temp") == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && app.GetPath("exe") == Environment.ProcessPath, app.GetPath("temp"));
    if (OperatingSystem.IsWindows())
    {
        // DIR_ASSETS is DIR_MODULE of the libcef actually loaded, which every Windows layout puts beside the exe.
        var loadedLibcef = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .FirstOrDefault(m => string.Equals(m.ModuleName, "libcef.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
        var assets = app.GetPath("assets");
        Check("GetPath(assets) is the exe's directory, where libcef and its resources are, on Windows",
            string.Equals(assets, Path.GetDirectoryName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(assets, Path.GetDirectoryName(loadedLibcef), StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(assets, "resources.pak")), $"{assets} vs exe {Environment.ProcessPath}, libcef {loadedLibcef}");
    }
    if (OperatingSystem.IsLinux())
    {
        // DIR_ASSETS is DIR_MODULE of the module holding Chromium's base: the libcef.so the process mapped.
        var mappedLibcef = File.ReadLines("/proc/self/maps").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 6 && p[5].EndsWith("/libcef.so", StringComparison.Ordinal)).Select(p => p[5]).FirstOrDefault();
        var linuxAssets = app.GetPath("assets");
        Check("GetPath(assets) is the directory of the libcef.so loaded, with CEF's data beside it, on Linux",
            mappedLibcef is not null && linuxAssets == Path.GetDirectoryName(mappedLibcef) && File.Exists(Path.Combine(linuxAssets, "icudtl.dat")),
            $"{linuxAssets} vs libcef {mappedLibcef}");
    }
    if (OperatingSystem.IsLinux())
    {
        // Paths are cached as by PathService, and the default session has already asked for downloads; any override clears it.
        void ClearPathCache() => app.SetPath("temp", app.GetPath("temp"));
        var userDirs = Path.Combine(app.GetPath("appData"), "user-dirs.dirs");
        // A desktop's own user-dirs.dirs is put back afterwards.
        var savedUserDirs = File.Exists(userDirs) ? File.ReadAllBytes(userDirs) : null;
        string downloadsDir, plainDownloadsDir;
        try
        {
            File.WriteAllText(userDirs, "XDG_DOWNLOAD_DIR=\"$HOME/Fetched\"\n");
            ClearPathCache();
            downloadsDir = app.GetPath("downloads");
            File.Delete(userDirs);
            ClearPathCache();
            plainDownloadsDir = app.GetPath("downloads");
        }
        finally
        {
            if (savedUserDirs is not null) File.WriteAllBytes(userDirs, savedUserDirs);
            else File.Delete(userDirs);
            ClearPathCache();
        }
        Check("GetPath(downloads) follows the XDG user directories on Linux", downloadsDir == Path.Combine(app.GetPath("home"), "Fetched")
            && plainDownloadsDir == Path.Combine(app.GetPath("home"), "Downloads"), $"{downloadsDir} then {plainDownloadsDir}");
    }
    var unknownPath = await Throws(() => Task.FromResult(app.GetPath("nope")));
    var originalLogs = app.GetPath("logs");
    var missingLogs = Path.Combine(Path.GetTempPath(), "oriel-no-such-dir-" + Guid.NewGuid());
    var missingDir = await Throws(() => { app.SetPath("logs", missingLogs); return Task.CompletedTask; });
    var relativeDir = await Throws(() => { app.SetPath("logs", "relative"); return Task.CompletedTask; });
    Check("GetPath and SetPath refuse what Electron refuses, and SetPath takes a directory that does not exist without making it",
        unknownPath?.Message.StartsWith("Failed to get 'nope' path") == true && missingDir is null && app.GetPath("logs") == missingLogs
        && !Directory.Exists(missingLogs) && relativeDir is ArgumentException, $"{unknownPath?.Message} | {missingDir?.Message} | {relativeDir?.Message}");
    app.SetPath("logs", originalLogs);
    await Section("AppAuditChecks", AppAuditChecks);
    await Section("SessionAuditChecks", SessionAuditChecks);
    await Section("GpuHardwareChecks", GpuHardwareChecks);
    var movedTo = Directory.CreateTempSubdirectory("oriel-userdata-").FullName;
    var originalUserData = app.GetPath("userData");
    app.SetPath("userData", movedTo);
    Check("SetPath overrides GetPath, and sessionData follows a moved userData", app.GetPath("userData") == movedTo && app.GetPath("sessionData") == movedTo);
    app.SetPath("userData", originalUserData);
    Directory.Delete(movedTo);

    Check("Version is the project's, prerelease kept and build metadata dropped, and names the app in its user agent", app.Version == "1.4.2-beta.3"
        && app.UserAgentFallback.Contains($" {app.Name.Replace(" ", "")}/1.4.2-beta.3 ", StringComparison.Ordinal), $"{app.Version} | {app.UserAgentFallback}");

    // app.getPreferredSystemLanguages / getSystemLocale / getLocaleCountryCode, read back from another process.
    static string Output(string tool, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        // Output to EOF only: WaitForExit can hang inside the CEF process on Linux.
        return process.StandardOutput.ReadToEnd().Trim();
    }
    var languages = string.Join(",", app.GetPreferredSystemLanguages());
    var systemLocale = app.GetSystemLocale();
    var countryCode = app.GetLocaleCountryCode();
    if (OperatingSystem.IsMacOS())
    {
        var expected = Output("/usr/bin/osascript", "-l", "JavaScript", "-e",
            "ObjC.import('Foundation'); var l = $.NSLocale.currentLocale; [ObjC.deepUnwrap($.NSLocale.preferredLanguages).join(','), l.localeIdentifier.js.replace(/_/g, '-'), l.countryCode.js || ''].join('|')");
        Check("The locale getters answer as NSLocale does on macOS", $"{languages}|{systemLocale}|{countryCode}" == expected, $"{languages}|{systemLocale}|{countryCode} vs {expected}");
    }
    if (OperatingSystem.IsLinux())
    {
        // locale(1) resolves LC_ALL, the category and LANG as setlocale does.
        string Category(string name) => Output("/usr/bin/locale").Split('\n').First(l => l.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..].Trim('"');
        var messages = Category("LC_MESSAGES");
        var timeName = Category("LC_TIME");
        // The C library falls back to C for a locale it does not have; locale -a lists codesets as glibc normalises them.
        static string Normalised(string name) => name.IndexOf('.') is var dot and >= 0
            ? name[..dot] + "." + new string(name[(dot + 1)..].Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()) : name;
        var installed = Output("/usr/bin/locale", "-a").Split('\n').Select(Normalised).Contains(Normalised(timeName));
        var time = installed ? timeName.Split('.')[0] : "C";
        var region = time.Contains('_') ? time[(time.IndexOf('_') + 1)..] : "";
        var expectedLocale = messages is "C" or "POSIX" || messages.StartsWith("C.", StringComparison.Ordinal) ? "en-US@posix" : messages.Split('.')[0].Replace('_', '-');
        Check("The locale getters follow the C library's locale on Linux", systemLocale == expectedLocale && countryCode == (region.Length == 2 ? region : "")
            && (Environment.GetEnvironmentVariable("LANGUAGE") is { Length: > 0 } || languages.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == (expectedLocale == "en-US@posix" ? null : expectedLocale)),
            $"{languages}|{systemLocale}|{countryCode} vs LC_MESSAGES={messages} LC_TIME={timeName} installed={installed}");
    }

    if (OperatingSystem.IsWindows())
    {
        // The same Win32 calls from Windows PowerShell, a process with its own COM setup.
        const string script = """
            Add-Type -Namespace L -Name N -MemberDefinition '[DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern int GetLocaleInfoEx(string n, uint t, System.Text.StringBuilder b, int c);'
            function Info($n, $t) { if ($null -eq $n) { $n = [NullString]::Value }; $b = New-Object System.Text.StringBuilder 85; if ([L.N]::GetLocaleInfoEx($n, $t, $b, 85) -gt 0) { $b.ToString() } else { '' } }
            $null = [Windows.System.UserProfile.GlobalizationPreferences, Windows.System.UserProfile, ContentType=WindowsRuntime]
            $c = Info $null 0x5A
            if ($c.Length -eq 0) { $c = Info '!x-sys-default-locale' 0x5A }
            ([Windows.System.UserProfile.GlobalizationPreferences]::Languages -join ',') + '|' + (Info $null 0x5C) + '|' + $c
            """;
        var raw = Output("powershell.exe", "-NoProfile", "-Command", script).Split('|');
        // ICU prints an ordinary Windows locale name unchanged; a sort-order suffix or legacy alias would differ.
        Check("The locale getters answer as the Win32 locale APIs do on Windows",
            raw.Length == 3 && languages == raw[0] && systemLocale == raw[1] && countryCode == (raw[2].Length == 2 ? raw[2] : ""),
            $"{languages}|{systemLocale}|{countryCode} vs {string.Join('|', raw)}");
    }

    // setBadgeCount on Linux: the launcher entry signal as dbus-monitor decodes it off the session bus.
    if (OperatingSystem.IsLinux())
    {
        using var monitor = Oriel.Samples.LauncherEntryMonitor.Start();
        Check("a session bus and dbus-monitor are there to see the launcher entry", monitor is not null, Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        if (monitor is not null)
        {
            static (string, string)[] Badge(string count, bool visible) => [("count", "int64 " + count), ("count-visible", visible ? "boolean true" : "boolean false")];
            const string Uri = "application://oriel-sessiontest.desktop";
            var calls = new List<string>();
            foreach (var count in new int?[] { 7, 0, null, -3 }) calls.Add($"{app.SetBadgeCount(count)}:{app.GetBadgeCount()}");
            // Sent from a thread of its own, so a probe sent after it can overtake it.
            var synced = Until(() => monitor.Updates().Count >= 3, TimeSpan.FromSeconds(5)) && monitor.Sync();
            Check("SetBadgeCount sends Update(application://<desktop name>, {count: x, count-visible: b}); null sends nothing and returns false",
                synced && calls.SequenceEqual(["True:7", "True:0", "False:0", "True:-3"]) && monitor.Updates().SequenceEqual([
                    Oriel.Samples.LauncherEntryMonitor.Update(Uri, Badge("7", true)), Oriel.Samples.LauncherEntryMonitor.Update(Uri, Badge("0", false)),
                    Oriel.Samples.LauncherEntryMonitor.Update(Uri, Badge("-3", true))]),
                string.Join(",", calls) + " | " + string.Join(" / ", monitor.Updates()));
            var updatesBefore = monitor.Updates().Count;
            app.SetDesktopName("");
            var noName = app.SetBadgeCount(5);
            // Long enough for the sender thread to have sent anything it was given.
            await Task.Delay(300);
            synced = monitor.Sync();
            Check("without a desktop name SetBadgeCount sends nothing, returns false and keeps the count",
                synced && !noName && app.GetBadgeCount() == -3 && monitor.Updates(updatesBefore).Count == 0, $"{noName}:{app.GetBadgeCount()} {string.Join(" / ", monitor.Updates(updatesBefore))}");
            app.SetDesktopName("custom-name.desktop");
            var renamed = app.SetBadgeCount(2);
            synced = Until(() => monitor.Updates(updatesBefore).Count >= 1, TimeSpan.FromSeconds(5)) && monitor.Sync();
            Check("SetDesktopName names the app in the signal", synced && renamed && monitor.Updates(updatesBefore).SequenceEqual([
                Oriel.Samples.LauncherEntryMonitor.Update("application://custom-name.desktop", Badge("2", true))]), string.Join(" / ", monitor.Updates(updatesBefore)));
            app.SetDesktopName("oriel-sessiontest.desktop");
            app.SetBadgeCount(0);
        }
    }

    // app.hide / show / isHidden / focus, setBadgeCount and app.dock, read back by another process (macOS).
    if (OperatingSystem.IsMacOS())
    {
        var pid = Environment.ProcessId;
        string AppState() => Output("/usr/bin/osascript", "-l", "JavaScript", "-e",
            $"ObjC.import('AppKit'); var a = $.NSRunningApplication.runningApplicationWithProcessIdentifier({pid}); [a.hidden, a.active].join('|')");
        string DockLabel() => Output("/usr/bin/lsappinfo", "info", "-only", "StatusLabel", pid.ToString(CultureInfo.InvariantCulture));
        var stateWin = new BrowserWindow(new BrowserWindowOptions { Width = 300, Height = 200 });
        app.Focus(new AppFocusOptions { Steal = true });
        var focused = Until(() => AppState() == "false|true", TimeSpan.FromSeconds(5));
        app.Hide();
        var hid = Until(() => app.IsHidden() && AppState() == "true|false", TimeSpan.FromSeconds(5));
        app.Show();
        var shown = Until(() => !app.IsHidden() && AppState() == "false|false", TimeSpan.FromSeconds(5));
        Check("Focus activates the app, Hide hides it, Show unhides it, as AppKit reports to another process", focused && hid && shown, AppState());
        var badges = new List<string>();
        foreach (var count in new int?[] { 7, 150, null, 0 })
        {
            app.SetBadgeCount(count);
            badges.Add($"{app.GetBadgeCount()}:{app.Dock.GetBadge()}:{DockLabel()}");
        }
        app.Dock.SetBadge("new");
        var label = app.Dock.GetBadge() + ":" + DockLabel();
        app.Dock.SetBadge("");
        Check("SetBadgeCount shows Chromium's badge text on the Dock tile; 0 clears it", badges.SequenceEqual([
            "7:7:\"StatusLabel\"={ \"label\"=\"7\" }", "150:99+:\"StatusLabel\"={ \"label\"=\"99+\" }",
            "0:\u2022:\"StatusLabel\"={ \"label\"=\"\u2022\" }", "0::\"StatusLabel\"={ \"label\"=\"\" }"]) && label == "new:\"StatusLabel\"={ \"label\"=\"new\" }",
            string.Join(" / ", badges) + " / " + label);

#pragma warning disable CA1416 // AppKit calls in lambdas, inside the IsMacOS() block the analyzer cannot see into.
        // app.dock: the icon's presence as LaunchServices reports it, the menu as the Dock asks for it, the icon's size.
        string AppType() => Output("/usr/bin/lsappinfo", "info", "-only", "ApplicationType", pid.ToString(CultureInfo.InvariantCulture));
        var stateHandle = stateWin.GetNativeWindowHandle();
        await Task.Delay(1100);
        app.Dock.Hide();
        var dockHidden = Until(() => !app.Dock.IsVisible() && AppType().Contains("UIElement", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        var cannotHide = !Oriel.Cef.CefThread.InvokeOnUi(() => Oriel.Cef.Platform.MacWindow.CanHide(stateHandle));
        await app.Dock.ShowAsync();
        var dockShown = app.Dock.IsVisible() && Until(() => AppType().Contains("Foreground", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        app.Dock.Hide();
        await Task.Delay(500);
        var hideIgnored = app.Dock.IsVisible() && AppType().Contains("Foreground", StringComparison.Ordinal);
        Check("Dock.Hide drops the Dock icon and keeps windows from hiding; ShowAsync brings it back", dockHidden && cannotHide && dockShown, AppType());
        Check("Dock.Hide right after ShowAsync is ignored, as Electron's", hideIgnored, AppType());
        // Again while active, which takes Electron's other path: the Dock is activated first, then the app again.
        await Task.Delay(1100);
        app.Dock.Hide();
        var hiddenAgain = Until(() => AppType().Contains("UIElement", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        app.Focus(new AppFocusOptions { Steal = true });
        var activeHidden = Until(() => AppState().EndsWith("|true", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await app.Dock.ShowAsync();
        var shownActive = app.Dock.IsVisible() && AppType().Contains("Foreground", StringComparison.Ordinal) && AppState().EndsWith("|true", StringComparison.Ordinal);
        Check("Dock.ShowAsync on an active app shows the icon and leaves the app active", hiddenAgain && activeHidden && shownActive, $"{AppType()} {AppState()}");

        var dockClicks = new List<string>();
        var dockMenu = new Menu();
        dockMenu.Append(new MenuItem { Label = "Alpha", Click = (_, _, _) => dockClicks.Add("Alpha") });
        var more = new Menu();
        more.Append(new MenuItem { Label = "Beta" });
        dockMenu.Append(new MenuItem { Label = "More", Submenu = more });
        app.Dock.SetMenu(dockMenu);
        var dockAsked = Oriel.Cef.CefThread.InvokeOnUi(() =>
        {
            var native = Oriel.Cef.Platform.MacActivation.AskDockMenu();
            var titles = Enumerable.Range(0, Oriel.Cef.Platform.MacMenu.ItemCount(native)).Select(i => Oriel.Cef.Platform.MacMenu.Title(Oriel.Cef.Platform.MacMenu.ItemAt(native, i))).ToList();
            Oriel.Cef.Platform.MacMenu.PerformAction(native, 0);
            return string.Join(",", titles);
        });
        var dockClicked = Until(() => dockClicks.Count == 1, TimeSpan.FromSeconds(3));
        Check("Dock.SetMenu is what the Dock gets from the app delegate, and its items click", dockAsked == "Alpha,More" && dockClicked && ReferenceEquals(app.Dock.GetMenu(), dockMenu),
            $"{dockAsked} delegate={Oriel.Cef.CefThread.InvokeOnUi(Oriel.Cef.Platform.MacActivation.DelegateClass)} clicks={string.Join(",", dockClicks)}");

        var icon = NativeImage.CreateFromBitmap(new byte[20 * 10 * 4], new NativeImageBitmapOptions { Width = 20, Height = 10 });
        icon.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, Width = 40, Height = 20, Buffer = new byte[40 * 20 * 4] });
        app.Dock.SetIcon(icon);
        var both = Oriel.Cef.CefThread.InvokeOnUi(Oriel.Cef.Platform.MacActivation.IconSize);
        app.Dock.SetIcon(NativeImage.CreateFromBitmap(new byte[64 * 32 * 4], new NativeImageBitmapOptions { Width = 64, Height = 32, ScaleFactor = 2 }));
        var retinaOnly = Oriel.Cef.CefThread.InvokeOnUi(Oriel.Cef.Platform.MacActivation.IconSize);
        app.Dock.SetIcon(NativeImage.CreateEmpty());
        var restored = Oriel.Cef.CefThread.InvokeOnUi(Oriel.Cef.Platform.MacActivation.IconSize);
        Check("Dock.SetIcon sizes the icon in points from every representation; an empty image restores the app's", both == (20, 10) && retinaOnly == (32, 16) && restored != (32, 16),
            $"{both} {retinaOnly} {restored}");
#pragma warning restore CA1416
        stateWin.Destroy();
    }

    // process: what the OS says, read back through other tools.
    var sysMemory = OrielProcess.GetSystemMemoryInfo();
    var processCreated = OrielProcess.GetCreationTime();
    var startedAgo = DateTime.UtcNow - DateTime.UnixEpoch - TimeSpan.FromMilliseconds(processCreated ?? 0);
    Check("GetCreationTime is when this process started", processCreated is not null && startedAgo > TimeSpan.Zero && startedAgo < TimeSpan.FromMinutes(15), startedAgo);
    if (OperatingSystem.IsLinux())
    {
        var freeLine = Output("/usr/bin/free", "-k").Split('\n').First(l => l.StartsWith("Mem:", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Check("GetSystemMemoryInfo's total and available match free(1), with Linux's fields", sysMemory.Total == long.Parse(freeLine[1], CultureInfo.InvariantCulture)
            && sysMemory.Available is { } available && Math.Abs(available - long.Parse(freeLine[6], CultureInfo.InvariantCulture)) < 65536
            && sysMemory.SwapTotal is not null && sysMemory.SwapFree is not null && sysMemory.FileBacked is null && sysMemory.Purgeable is null && sysMemory.Free <= sysMemory.Total,
            $"{sysMemory} vs {string.Join(' ', freeLine)}");
        Check("GetSystemVersion is the kernel release, as uname -r", OrielProcess.GetSystemVersion() == Output("/bin/uname", "-r"), OrielProcess.GetSystemVersion());
        string SoftFiles() => File.ReadAllLines("/proc/self/limits").First(l => l.StartsWith("Max open files", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
        string HardFiles() => File.ReadAllLines("/proc/self/limits").First(l => l.StartsWith("Max open files", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[4];
        // .NET raises the soft limit to the hard one at start-up, so lower it first. A sandboxed patched run already holds
        // about 1024 descriptors here, and a limit at or under that fails every later open with EMFILE.
        var openFds = Directory.GetFileSystemEntries("/proc/self/fd").Length;
        var floor = Math.Max(1024, (openFds + 256 + 1023) / 1024 * 1024);
        var lowered = FdLimit.LowerSoft((ulong)floor);
        var soft = long.Parse(SoftFiles(), CultureInfo.InvariantCulture);
        var hard = HardFiles() == "unlimited" ? long.MaxValue : long.Parse(HardFiles(), CultureInfo.InvariantCulture);
        OrielProcess.SetFdLimit((int)Math.Max(1, soft / 2));
        var notLowered = SoftFiles();
        var target = (int)Math.Min(hard, soft + 16);
        OrielProcess.SetFdLimit(target);
        var raised = SoftFiles();
        if (hard < int.MaxValue / 2) OrielProcess.SetFdLimit((int)hard * 2);
        Check("SetFdLimit raises the soft open-files limit up to the hard limit, never lowers it", lowered && soft == floor && soft < hard && notLowered == soft.ToString(CultureInfo.InvariantCulture)
            && raised == target.ToString(CultureInfo.InvariantCulture) && SoftFiles() == hard.ToString(CultureInfo.InvariantCulture), $"open {openFds}, soft {soft} hard {hard}: {notLowered}, {raised}, then {SoftFiles()}");
    }
    if (OperatingSystem.IsMacOS())
    {
        var product = Output("/usr/bin/sw_vers", "-productVersion").Split('.');
        var expectedVersion = $"{product[0]}.{(product.Length > 1 ? product[1] : "0")}.{(product.Length > 2 ? product[2] : "0")}";
        Check("GetSystemVersion is macOS's own version, as sw_vers", OrielProcess.GetSystemVersion() == expectedVersion, $"{OrielProcess.GetSystemVersion()} vs {expectedVersion}");
        Check("GetSystemMemoryInfo's total is hw.memsize, with macOS's fields", sysMemory.Total == long.Parse(Output("/usr/sbin/sysctl", "-n", "hw.memsize"), CultureInfo.InvariantCulture) / 1024
            && sysMemory.FileBacked is not null && sysMemory.Purgeable is not null && sysMemory.Available is null && sysMemory.SwapTotal is null && sysMemory.Free <= sysMemory.Total, sysMemory);
    }
    if (OperatingSystem.IsWindows())
    {
        static long[] Cim(string expression) => Output("powershell.exe", "-NoProfile", "-Command", $"$o = Get-CimInstance Win32_OperatingSystem; {expression}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => long.Parse(l.Trim(), CultureInfo.InvariantCulture)).ToArray();
        // Win32_OperatingSystem is GlobalMemoryStatusEx in KiB: physical total and free, then the commit limit and what is left of it.
        var cim = Cim("$o.TotalVisibleMemorySize; $o.FreePhysicalMemory; $o.TotalVirtualMemorySize; $o.FreeVirtualMemory");
        var after = OrielProcess.GetSystemMemoryInfo();
        Check("GetSystemMemoryInfo's total and free match Win32_OperatingSystem, with Windows' fields", sysMemory.Total == cim[0] && Math.Abs(after.Free - cim[1]) < 512 * 1024
            && Math.Abs(sysMemory.SwapTotal!.Value - cim[2]) < 1024 && Math.Abs(after.SwapFree!.Value - cim[3]) < 512 * 1024
            && sysMemory.Available is null && sysMemory.FileBacked is null && sysMemory.Purgeable is null && sysMemory.Free <= sysMemory.Total, $"{after} vs {string.Join(' ', cim)}");
        var registry = Output("powershell.exe", "-NoProfile", "-Command", "$k = Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion'; \"$($k.CurrentMajorVersionNumber).$($k.CurrentMinorVersionNumber).$($k.CurrentBuildNumber)\"");
        Check("GetSystemVersion is major.minor.build, without the UBR, as the registry says", OrielProcess.GetSystemVersion() == registry && OrielProcess.GetSystemVersion().Split('.').Length == 3, $"{OrielProcess.GetSystemVersion()} vs {registry}");
        var ownMemory = await OrielProcess.GetProcessMemoryInfoAsync();
        var psRead = Output("powershell.exe", "-NoProfile", "-Command", $"$p = Get-Process -Id {Environment.ProcessId}; $p.WorkingSet64 / 1024; $p.PrivateMemorySize64 / 1024")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (long)double.Parse(l.Trim(), CultureInfo.InvariantCulture)).ToArray();
        Check("GetProcessMemoryInfo's resident set and private are this process's working set and private bytes, shared 0",
            ownMemory.ResidentSet > 10 * 1024 && Math.Abs(ownMemory.ResidentSet!.Value - psRead[0]) < 0.2 * psRead[0] && ownMemory.Private > 10 * 1024 && Math.Abs(ownMemory.Private - psRead[1]) < 0.2 * psRead[1] && ownMemory.Shared == 0, $"{ownMemory} vs {string.Join(' ', psRead)}");
        Check("WindowsStore is false outside an MSIX package", !OrielProcess.WindowsStore);
    }
    if (OperatingSystem.IsLinux())
    {
        // ps reads VmRSS; RssAnon and VmSwap straight from the status file, through awk rather than Oriel's parser.
        var linuxMemory = await OrielProcess.GetProcessMemoryInfoAsync();
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var rss = long.Parse(Output("/bin/ps", "-o", "rss=", "-p", pid), CultureInfo.InvariantCulture);
        var anonSwap = long.Parse(Output("/usr/bin/awk", "/^(RssAnon|VmSwap):/ { s += $2 } END { print s }", $"/proc/{pid}/status"), CultureInfo.InvariantCulture);
        Check("on Linux, GetProcessMemoryInfo's resident set is VmRSS and private is RssAnon plus swap, as Chromium's; shared 0",
            linuxMemory.ResidentSet is { } resident && Math.Abs(resident - rss) < 0.2 * rss && Math.Abs(linuxMemory.Private - anonSwap) < 0.2 * anonSwap && linuxMemory.Shared == 0,
            $"{linuxMemory} vs rss {rss}, anon+swap {anonSwap}");
    }
    if (OperatingSystem.IsMacOS())
    {
        // vmmap reports the physical footprint independently; Electron leaves residentSet out on macOS.
        var macMemory = await OrielProcess.GetProcessMemoryInfoAsync();
        var summary = Output("/usr/bin/vmmap", "--summary", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var footprintLine = summary.Split('\n').FirstOrDefault(l => l.StartsWith("Physical footprint:", StringComparison.Ordinal)) ?? "";
        var parts = footprintLine["Physical footprint:".Length..].Trim();
        var number = double.TryParse(parts[..^1], CultureInfo.InvariantCulture, out var n) ? n : -1;
        var footprintKb = parts.EndsWith('G') ? number * 1024 * 1024 : parts.EndsWith('M') ? number * 1024 : parts.EndsWith('K') ? number : -1;
        Check("on macOS, GetProcessMemoryInfo's private is the physical footprint, with no resident set, as Electron's",
            macMemory.ResidentSet is null && footprintKb > 0 && Math.Abs(macMemory.Private - footprintKb) < 0.25 * footprintKb && macMemory.Shared == 0, $"{macMemory} vs {footprintLine}");
    }
    OrielProcess.GetCPUUsage();
    // Every CPU busy: the percent is of all of them, so it nears 100 only when divided by their count.
    var spinUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
    var spins = 0L;
    // Threads of their own: the thread pool adds threads too slowly to busy every CPU of a large machine in 400 ms.
    var spinners = Enumerable.Range(0, Environment.ProcessorCount)
        .Select(_ => new Thread(() => { while (DateTime.UtcNow < spinUntil) Interlocked.Increment(ref spins); })).ToList();
    spinners.ForEach(t => t.Start());
    spinners.ForEach(t => t.Join());
    var usage = OrielProcess.GetCPUUsage();
    Check("GetCPUUsage measures since the previous call, across all CPUs, with the running total", usage.PercentCPUUsage > 50
        && usage.PercentCPUUsage <= 110 && usage.CumulativeCPUUsage > 0.3 && (OperatingSystem.IsWindows() ? usage.IdleWakeupsPerSecond == 0 : usage.IdleWakeupsPerSecond > 0), $"{usage} spins={spins}");
    Check("process type and versions are Electron's shape", OrielProcess.Type == "browser" && OrielProcess.Versions.Chrome.Split('.').Length == 4, OrielProcess.Versions);

    // app.getAppMetrics: this process and the Chromium children it started, as ps lists them.
    var busyWin = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await busyWin.LoadUrl("data:text/html,<p>busy</p>");
    // ps before and after the metrics: a short-lived utility process can start or exit between two snapshots.
    var psBefore = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? Output("/bin/ps", "-eo", "pid=,ppid=,args=") : "";
    var firstMetrics = app.GetAppMetrics();
    Check("GetAppMetrics starts with this process as Browser, and every CPU reading starts at 0", firstMetrics[0] is { Type: "Browser" } browserMetric
        && browserMetric.Pid == Environment.ProcessId && Math.Abs(browserMetric.CreationTime - (OrielProcess.GetCreationTime() ?? 0)) < 1500
        && firstMetrics.All(m => m.Cpu.PercentCPUUsage == 0 && m.Cpu.CumulativeCPUUsage >= 0), string.Join(" ", firstMetrics.Select(m => $"{m.Pid}:{m.Type}:{m.Cpu.PercentCPUUsage}")));
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        static List<(int Pid, int Parent, string Args)> Rows(string ps) => ps.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length == 3)
            .Select(p => (Pid: int.Parse(p[0], CultureInfo.InvariantCulture), Parent: int.Parse(p[1], CultureInfo.InvariantCulture), Args: p[2])).ToList();
        static string KindOf(string args) => args.Contains("--type=renderer") ? "Tab" : args.Contains("--type=gpu-process") ? "GPU" : args.Contains("--type=utility") ? "Utility" : "";
        static List<string> ChildrenIn(List<(int Pid, int Parent, string Args)> rows)
        {
            bool Under(int pid) { for (var i = 0; i < 64; i++) { var row = rows.FirstOrDefault(t => t.Pid == pid); if (row.Pid == 0) return false; if (row.Parent == Environment.ProcessId) return true; pid = row.Parent; } return false; }
            return [.. rows.Where(t => Under(t.Pid) && KindOf(t.Args) != "").Select(t => $"{t.Pid}:{KindOf(t.Args)}").Order()];
        }
        var table = Rows(Output("/bin/ps", "-eo", "pid=,ppid=,args="));
        var before = ChildrenIn(Rows(psBefore));
        var after = ChildrenIn(table);
        // Every child in both snapshots is listed, and nothing listed is absent from both.
        var expectedChildren = before.Intersect(after).ToList();
        var listedChildren = firstMetrics.Skip(1).Select(m => $"{m.Pid}:{m.Type}").Order().ToList();
        var seenChildren = before.Union(after).ToHashSet();
        Check("the children are the renderers, GPU and utility processes ps shows under this one, with a utility's service",
            expectedChildren.All(listedChildren.Contains) && listedChildren.All(seenChildren.Contains) && listedChildren.Any(c => c.EndsWith(":Tab", StringComparison.Ordinal))
            && firstMetrics.Any(m => m.ServiceName == "network.mojom.NetworkService") && firstMetrics.All(m => m.Name is null)
            // Electron reports memory and sandboxing on macOS only; Oriel's macOS children are unsandboxed (drift A1).
            && firstMetrics.All(m => OperatingSystem.IsMacOS() ? m.Memory is { WorkingSetSize: > 0 } mem && mem.PeakWorkingSetSize >= mem.WorkingSetSize && m.Sandboxed == false : m.Memory is null),
            $"{string.Join(",", expectedChildren)} vs {string.Join(",", listedChildren)}");
        // etime, [[dd-]hh:]mm:ss, is what both Linux and macOS ps print.
        static double Elapsed(string etime)
        {
            var days = etime.Contains('-') ? double.Parse(etime[..etime.IndexOf('-')], CultureInfo.InvariantCulture) : 0;
            var parts = etime[(etime.IndexOf('-') + 1)..].Split(':').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).Reverse().ToArray();
            return days * 86400 + parts[0] + (parts.Length > 1 ? parts[1] * 60 : 0) + (parts.Length > 2 ? parts[2] * 3600 : 0);
        }
        // A child can exit between the metrics and ps, which then prints nothing for it.
        var etimes = firstMetrics.Skip(1)
            .Select(m => (m, Etime: Output("/bin/ps", "-o", "etime=", "-p", m.Pid.ToString(CultureInfo.InvariantCulture)).Trim()))
            .Where(e => e.Etime.Length > 0)
            .Select(e => (e.m, Seconds: Elapsed(e.Etime))).ToList();
        var nowMs = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        Check("each child's creation time is when ps says it started", etimes.All(e => Math.Abs(nowMs - e.Seconds * 1000 - e.m.CreationTime) < 2500),
            string.Join(",", etimes.Select(e => $"{e.m.Pid}:{nowMs - e.Seconds * 1000 - e.m.CreationTime:F0}")));
    }
#pragma warning disable CA1416
    if (OperatingSystem.IsWindows())
    {
        // Win32_Process is WMI's own view: parent, command line, start time and working set, in one table.
        var table = Output("powershell.exe", "-NoProfile", "-Command",
            "Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId)|$($_.ParentProcessId)|$(([DateTimeOffset]$_.CreationDate).ToUnixTimeMilliseconds())|$($_.WorkingSetSize)|$($_.CommandLine)\" }")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+\|\d+\|\d+\|\d+\|")).Select(l => l.Split('|', 5))
            .Select(p => (Pid: int.Parse(p[0], CultureInfo.InvariantCulture), Parent: int.Parse(p[1], CultureInfo.InvariantCulture), Created: double.Parse(p[2], CultureInfo.InvariantCulture),
                WorkingSet: long.Parse(p[3], CultureInfo.InvariantCulture), Args: p[4])).ToList();
        bool Under(int pid) { for (var i = 0; i < 64; i++) { var row = table.FirstOrDefault(t => t.Pid == pid); if (row.Pid == 0) return false; if (row.Parent == Environment.ProcessId) return true; pid = row.Parent; } return false; }
        string KindOf(string args) => args.Contains("--type=renderer") ? "Tab" : args.Contains("--type=gpu-process") ? "GPU" : args.Contains("--type=utility") ? "Utility" : "";
        var expectedChildren = table.Where(t => Under(t.Pid) && KindOf(t.Args) != "").Select(t => $"{t.Pid}:{KindOf(t.Args)}").Order().ToList();
        var listedChildren = firstMetrics.Skip(1).Select(m => $"{m.Pid}:{m.Type}").Order().ToList();
        Check("on Windows, the children are the renderers, GPU and utility processes Win32_Process shows under this one, with a utility's service",
            expectedChildren.SequenceEqual(listedChildren) && listedChildren.Any(c => c.EndsWith(":Tab", StringComparison.Ordinal))
            && firstMetrics.Any(m => m.ServiceName == "network.mojom.NetworkService") && firstMetrics.All(m => m.Name is null),
            $"{string.Join(",", expectedChildren)} vs {string.Join(",", listedChildren)}");
        Check("each process's creation time is Win32_Process's CreationDate", firstMetrics.All(m => table.FirstOrDefault(t => t.Pid == m.Pid) is { Pid: not 0 } row && Math.Abs(row.Created - m.CreationTime) < 2500),
            string.Join(",", firstMetrics.Select(m => $"{m.Pid}:{m.CreationTime - table.FirstOrDefault(t => t.Pid == m.Pid).Created:F0}")));
        Check("each process's memory is in KiB: its working set as Win32_Process says, the peak above it, private bytes above 1 MiB",
            firstMetrics.All(m => m.Memory is { } mem && table.FirstOrDefault(t => t.Pid == m.Pid) is { Pid: not 0 } row
                && Math.Abs(mem.WorkingSetSize - row.WorkingSet / 1024) < Math.Max(10 * 1024, 0.25 * mem.WorkingSetSize) && mem.PeakWorkingSetSize >= mem.WorkingSetSize && mem.PrivateBytes > 1024),
            string.Join(",", firstMetrics.Select(m => $"{m.Pid}:{m.Memory}")));
        Check("each process's integrityLevel is its token's, and sandboxed means below medium",
            firstMetrics.All(m => TokenProbe.IntegrityName(m.Pid) is { } name && m.IntegrityLevel == name && m.Sandboxed == (name is "untrusted" or "low")),
            string.Join(",", firstMetrics.Select(m => $"{m.Pid}:{m.IntegrityLevel}/{m.Sandboxed}/{TokenProbe.IntegrityName(m.Pid)}")));
    }
#pragma warning restore CA1416
    await busyWin.WebContents.ExecuteJavaScriptAsync("const end = performance.now() + 500; while (performance.now() < end) {} 1");
    var secondMetrics = app.GetAppMetrics();
    Check("a page that keeps its renderer busy shows CPU use since the last call", secondMetrics.Any(m => m.Type == "Tab" && m.Cpu.PercentCPUUsage > 1)
        // One renderer thread uses at most one CPU, so its share of all of them stays near 1/count.
        && secondMetrics.All(m => m.Type != "Tab" || m.Cpu.PercentCPUUsage <= 120.0 / Environment.ProcessorCount),
        string.Join(" ", secondMetrics.Select(m => $"{m.Pid}:{m.Type}:{m.Cpu.PercentCPUUsage:F1}")));
    busyWin.Destroy();
    // After the metrics checks above: they read the first GetAppMetrics call, and these windows start renderers.
    await SandboxSessionChecks();

    // Proxy: requests go to a local listener posing as the proxy.
    var proxied = Session.FromPartition("proxied");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var requestLine = ServeOneProxyRequest(listener);
    await proxied.SetProxyAsync(new ProxyConfig { ProxyRules = $"http=127.0.0.1:{port}" });
    var proxyWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = proxied } });
    var load = proxyWin.LoadUrl("http://proxied.test/page");
    var line = await requestLine.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : t.Exception?.InnerException?.Message);
    Check("SetProxyAsync routes requests through the proxy", line == "GET http://proxied.test/page HTTP/1.1", line);
    Check("the page loads through the proxy", await Throws(() => load.WaitAsync(TimeSpan.FromSeconds(10))) is null);
    await proxied.SetProxyAsync(new ProxyConfig { Mode = ProxyMode.Direct });
    await proxied.CloseAllConnectionsAsync();
    var direct = await Throws(() => proxyWin.LoadUrl("http://proxied.test/again").WaitAsync(TimeSpan.FromSeconds(10)));
    Check("Direct mode stops using the proxy", direct is LoadFailedException, direct?.Message);

    // Electron's ProxyConfigDictionary: a bypassed host goes direct, and fixed_servers without rules is direct.
    var bypassed = ServeOneProxyRequest(listener);
    await proxied.SetProxyAsync(new ProxyConfig { ProxyRules = $"http=127.0.0.1:{port}", ProxyBypassRules = "proxied.test" });
    await proxied.CloseAllConnectionsAsync();
    var bypassLoad = await Throws(() => proxyWin.LoadUrl("http://proxied.test/bypassed").WaitAsync(TimeSpan.FromSeconds(10)));
    Check("ProxyBypassRules sends the host direct", bypassLoad is LoadFailedException && !bypassed.IsCompleted,
        $"{bypassLoad?.GetType().Name} {bypassLoad?.Message} proxied={bypassed.IsCompleted} url={proxyWin.WebContents.GetUrl()}");
    await proxied.SetProxyAsync(new ProxyConfig { Mode = ProxyMode.FixedServers, ProxyBypassRules = "<local>" });
    var noRules = await Throws(() => proxyWin.LoadUrl("http://proxied.test/norules").WaitAsync(TimeSpan.FromSeconds(10)));
    Check("FixedServers without ProxyRules is direct", noRules is LoadFailedException && !bypassed.IsCompleted, noRules?.Message);

    if (Oriel.Cef.CefRuntime.IsPatched)
    {
        // Patch 0008: Electron's ses.resolveProxy and app.resolveProxy answer in PAC format.
        await proxied.SetProxyAsync(new ProxyConfig { ProxyRules = $"http=127.0.0.1:{port}" });
        var viaProxy = await proxied.ResolveProxyAsync("http://proxied.test/page");
        var notHttp = await proxied.ResolveProxyAsync("https://proxied.test/page");
        Check("patched (0008): ResolveProxyAsync names the session's proxy for http and DIRECT for https",
            viaProxy == $"PROXY 127.0.0.1:{port}" && notHttp == "DIRECT", $"{viaProxy} | {notHttp}");
        var pac = Convert.ToBase64String(Encoding.ASCII.GetBytes($"function FindProxyForURL(url, host) {{ return 'PROXY 127.0.0.1:{port + 1}'; }}"));
        await proxied.SetProxyAsync(new ProxyConfig { PacScript = $"data:application/x-ns-proxy-autoconfig;base64,{pac}" });
        var viaPac = await proxied.ResolveProxyAsync("http://proxied.test/page");
        Check("patched (0008): a PacScript decides what ResolveProxyAsync answers", viaPac == $"PROXY 127.0.0.1:{port + 1}", viaPac);
        await proxied.SetProxyAsync(new ProxyConfig { Mode = ProxyMode.Direct });
        Check("patched (0008): and Direct answers DIRECT", await proxied.ResolveProxyAsync("http://proxied.test/page") == "DIRECT");

        await app.SetProxyAsync(new ProxyConfig { ProxyRules = $"127.0.0.1:{port}" });
        // The system network context picks the Local State change up asynchronously.
        var systemProxy = "";
        for (var i = 0; i < 20 && systemProxy != $"PROXY 127.0.0.1:{port}"; i++)
        {
            systemProxy = await app.ResolveProxyAsync("http://system.test/");
            if (systemProxy != $"PROXY 127.0.0.1:{port}") await Task.Delay(100);
        }
        Check("patched (0008): app.SetProxyAsync sets the system context's proxy, which app.ResolveProxyAsync reads", systemProxy == $"PROXY 127.0.0.1:{port}", systemProxy);
        Check("patched (0008): and leaves the sessions' own alone", await Oriel.Session.DefaultSession.ResolveProxyAsync("http://system.test/") == "DIRECT");
        await app.SetProxyAsync(new ProxyConfig { Mode = ProxyMode.Direct });
    }
    else
    {
        var resolveError = await Throws(() => proxied.ResolveProxyAsync("http://proxied.test/"));
        var appResolveError = await Throws(() => app.ResolveProxyAsync("http://proxied.test/"));
        Check("stock CEF: ResolveProxyAsync and app.ResolveProxyAsync throw PlatformNotSupportedException (E8, A35)",
            resolveError is PlatformNotSupportedException && appResolveError is PlatformNotSupportedException, $"{resolveError?.Message} | {appResolveError?.Message}");
        Check("stock CEF: app.SetProxyAsync is accepted (the global proxy preference)",
            await Throws(() => app.SetProxyAsync(new ProxyConfig { Mode = ProxyMode.Direct })) is null);
    }
    proxyWin.Close();
    listener.Stop();
}

// cookies.on('changed') and set()'s rejection messages, in a session of their own while win keeps the app alive.
// Session storage clearing: Electron's clearStorageData, clearData and getCacheSize, against data real pages stored.
async Task StorageChecks()
{
    var store = Session.FromPartition("storage");
    store.Protocol.Handle("https", request => new Uri(request.Url).AbsolutePath == "/sw.js"
        ? ProtocolResponse.Text("self.addEventListener('install', () => self.skipWaiting());", "text/javascript")
        : ProtocolResponse.Text(Page, "text/html"));
    var storeWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = store } });
    const string A = "https://a.oriel.test", B = "https://b.oriel.test";
    async Task Fill(string origin)
    {
        await storeWin.LoadUrl(origin + "/");
        await storeWin.WebContents.ExecuteJavaScriptAsync("""
            (async () => {
              localStorage.setItem('k', 'v');
              await new Promise((ok, fail) => { const r = indexedDB.open('db', 1); r.onupgradeneeded = () => r.result.createObjectStore('s');
                r.onsuccess = () => { r.result.close(); ok(); }; r.onerror = () => fail(r.error); });
              await (await caches.open('c')).put('/cached', new Response('x'));
              await navigator.serviceWorker.register('/sw.js');
              await navigator.serviceWorker.ready;
              document.cookie = 'page=1; path=/; max-age=3600';
              return 1;
            })()
            """);
    }
    // What the origin still has, read by a fresh load of it.
    async Task<string> Held(string origin)
    {
        await storeWin.LoadUrl(origin + "/");
        return (await storeWin.WebContents.ExecuteJavaScriptAsync("""
            (async () => [localStorage.getItem('k') ? 'ls' : '', (await indexedDB.databases()).some(d => d.name === 'db') ? 'idb' : '',
              (await caches.keys()).includes('c') ? 'cache' : '', (await navigator.serviceWorker.getRegistrations()).length ? 'sw' : '',
              document.cookie.includes('page=1') ? 'cookie' : ''].filter(Boolean).join(','))()
            """))?.ToString() ?? "?";
    }
    const string Full = "ls,idb,cache,sw,cookie";
    await Fill(A);
    await Fill(B);
    var filled = $"{await Held(A)} | {await Held(B)}";
    Check("pages store localStorage, IndexedDB, Cache Storage, a service worker and a cookie", filled == $"{Full} | {Full}", filled);

    await store.ClearStorageDataAsync(new ClearStorageDataOptions { Origin = A, Storages = ["LocalStorage"] });
    var lsOnly = $"{await Held(A)} | {await Held(B)}";
    Check("ClearStorageDataAsync with an origin and storages clears only those, there (names in any case)", lsOnly == $"idb,cache,sw,cookie | {Full}", lsOnly);
    await store.ClearStorageDataAsync(new ClearStorageDataOptions { Origin = A });
    var originOnly = $"{await Held(A)} | {await Held(B)}";
    Check("ClearStorageDataAsync with an origin clears all its storage and cookies, not another origin's", originOnly == $" | {Full}", originOnly);
    await Fill(A);
    await store.ClearStorageDataAsync(new ClearStorageDataOptions { Storages = ["indexdb", "websql", "nope"] });
    var idbEverywhere = $"{await Held(A)} | {await Held(B)}";
    Check("ClearStorageDataAsync with storages and no origin clears them for every origin, ignoring unknown names",
        idbEverywhere == "ls,cache,sw,cookie | ls,cache,sw,cookie", idbEverywhere);
    var noneCleared = await Throws(() => store.ClearStorageDataAsync(new ClearStorageDataOptions { Storages = ["websql"] }));
    var stillThere = await Held(B);
    Check("ClearStorageDataAsync with no known storage completes and clears nothing", noneCleared is null && stillThere == "ls,cache,sw,cookie", $"{noneCleared?.Message} {stillThere}");
    await store.ClearStorageDataAsync();
    var allCleared = $"{await Held(A)} | {await Held(B)}";
    Check("ClearStorageDataAsync with no options clears every origin", allCleared == " | ", allCleared);

    // clearData.
    await Fill(A);
    await Fill(B);
    await store.ClearDataAsync(new ClearDataOptions { DataTypes = ["localStorage"], Origins = [A] });
    var dataLs = $"{await Held(A)} | {await Held(B)}";
    Check("ClearDataAsync with data types and origins clears those types there only", dataLs == $"idb,cache,sw,cookie | {Full}", dataLs);
    await store.ClearDataAsync(new ClearDataOptions { Origins = [] });
    var emptyOrigins = await Held(B);
    Check("ClearDataAsync with an empty origins list clears nothing", emptyOrigins == Full, emptyOrigins);
    await store.ClearDataAsync(new ClearDataOptions { DataTypes = [] });
    var emptyTypes = await Held(B);
    Check("ClearDataAsync with an empty data type list clears nothing", emptyTypes == Full, emptyTypes);
    await store.ClearDataAsync(new ClearDataOptions { Origins = [B] });
    var allOfB = $"{await Held(A)} | {await Held(B)}";
    // Cookies go by registrable domain: B's clears A's host cookie too, both being under oriel.test.
    Check("ClearDataAsync with origins and no data types clears all of their data, and their site's cookies", allOfB == "idb,cache,sw | ", allOfB);

    async Task SiteCookies()
    {
        foreach (var url in new[] { "https://sub.site1.test/", "https://other.site1.test/", "https://site2.test/" })
            await store.Cookies.SetAsync(new CookieDetails(url) { Name = new Uri(url).Host.Replace(".", "_"), Value = "1" });
    }
    string SiteNames(IEnumerable<Cookie> cookies) => string.Join(",", cookies.Where(c => c.Domain.Contains("site")).Select(c => c.Name).Order());
    await SiteCookies();
    await store.ClearDataAsync(new ClearDataOptions { DataTypes = ["cookies"], Origins = ["https://sub.site1.test"] });
    var byDomain = SiteNames(await store.Cookies.GetAsync());
    Check("ClearDataAsync clears cookies by registrable domain", byDomain == "site2_test", byDomain);
    await SiteCookies();
    await store.ClearDataAsync(new ClearDataOptions { DataTypes = ["cookies"], ExcludeOrigins = ["https://site2.test"] });
    var kept = SiteNames(await store.Cookies.GetAsync());
    var pageCookie = (await store.Cookies.GetAsync(new CookieFilter { Name = "page" })).Count;
    Check("ClearDataAsync with excludeOrigins keeps only their cookies", kept == "site2_test" && pageCookie == 0, $"{kept} page={pageCookie}");

    var both = await Throws(() => store.ClearDataAsync(new ClearDataOptions { Origins = [A], ExcludeOrigins = [B] }));
    Check("ClearDataAsync refuses origins with excludeOrigins, as Electron", both is ArgumentException { Message: "Cannot provide both 'origins' and 'excludeOrigins'" }, both?.Message);
    var opaque = await Throws(() => store.ClearDataAsync(new ClearDataOptions { Origins = ["data:text/plain,x"] }));
    Check("ClearDataAsync refuses an opaque origin, as Electron", opaque is ArgumentException { Message: "Invalid origin: 'data:text/plain,x'" }, opaque?.Message);
    var keepStorage = await Throws(() => store.ClearDataAsync(new ClearDataOptions { DataTypes = ["localStorage"], ExcludeOrigins = [B] }));
    Check("ClearDataAsync cannot keep some origins' storage", keepStorage is NotSupportedException, keepStorage?.GetType().Name);
    await Fill(A);
    await Fill(B);
    await store.ClearDataAsync();
    var dataAll = $"{await Held(A)} | {await Held(B)}";
    Check("ClearDataAsync with no options clears everything", dataAll == " | " && SiteNames(await store.Cookies.GetAsync()) == "", dataAll);
    storeWin.Destroy();

    // getCacheSize: an HTTP server's cacheable script lands in the session's cache on disk.
    var cached = Session.FromPartition("persist:cache");
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var script = Encoding.ASCII.GetBytes("//" + new string('x', 600_000));
    _ = Task.Run(async () =>
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                using var _ = client;
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var first = await reader.ReadLineAsync() ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                var big = first.Contains("/big.js");
                var body = big ? script : Encoding.ASCII.GetBytes("<!doctype html><script src=/big.js></script>");
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: {(big ? "text/javascript" : "text/html")}\r\nContent-Length: {body.Length}\r\n"
                    + $"{(big ? "Cache-Control: max-age=3600\r\n" : "Cache-Control: no-store\r\n")}Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(body);
            });
        }
    });
    await cached.ClearCacheAsync();
    var empty = await cached.GetCacheSizeAsync();
    var cacheWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = cached } });
    await cacheWin.LoadUrl($"http://127.0.0.1:{port}/");
    long grown = 0;
    for (var i = 0; i < 50 && grown < empty + script.Length; i++)
    {
        grown = await cached.GetCacheSizeAsync();
        if (grown < empty + script.Length) await Task.Delay(100);
    }
    Check("GetCacheSizeAsync grows by a cached response", grown >= empty + script.Length, $"{empty} -> {grown}");
    await cached.ClearCacheAsync();
    long shrunk = grown;
    for (var i = 0; i < 50 && shrunk >= script.Length; i++)
    {
        shrunk = await cached.GetCacheSizeAsync();
        if (shrunk >= script.Length) await Task.Delay(100);
    }
    Check("GetCacheSizeAsync shrinks after ClearCacheAsync", shrunk < script.Length, $"{grown} -> {shrunk}");
    // clearData's cache type empties the HTTP cache too.
    // A new URL: the page's own memory cache would answer the old one without the HTTP cache.
    await cacheWin.LoadUrl($"http://127.0.0.1:{port}/big.js?again");
    long refilled = 0;
    for (var i = 0; i < 50 && refilled < script.Length; i++)
    {
        refilled = await cached.GetCacheSizeAsync();
        if (refilled < script.Length) await Task.Delay(100);
    }
    await cached.ClearDataAsync(new ClearDataOptions { DataTypes = ["cache"] });
    long dataCleared = refilled;
    for (var i = 0; i < 50 && dataCleared >= script.Length; i++)
    {
        dataCleared = await cached.GetCacheSizeAsync();
        if (dataCleared >= script.Length) await Task.Delay(100);
    }
    Check("ClearDataAsync with the cache type empties the HTTP cache", refilled >= script.Length && dataCleared < script.Length, $"{refilled} -> {dataCleared}");
    cacheWin.Destroy();
    listener.Stop();
    var inMemory = await Throws(() => store.GetCacheSizeAsync());
    Check("GetCacheSizeAsync refuses an in-memory session", inMemory is NotSupportedException, inMemory?.GetType().Name);
}

// session.serviceWorkers: a worker that a page from an HTTP server on 127.0.0.1 (a secure context) registers.
async Task ServiceWorkerChecks()
{
    var swSession = Session.FromPartition("service-workers");
    // Read before the page loads: the DevTools lists arrive after the first use (drift SW1).
    var workers = swSession.ServiceWorkers;
    var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var statuses = new System.Collections.Concurrent.ConcurrentQueue<ServiceWorkerRunningStatusChangedEventArgs>();
    var messages = new System.Collections.Concurrent.ConcurrentQueue<ServiceWorkerConsoleMessageEventArgs>();
    workers.RegistrationCompleted += (_, e) => completed.TrySetResult(e.Scope);
    workers.RunningStatusChanged += (_, e) => statuses.Enqueue(e);
    workers.ConsoleMessage += (_, e) => messages.Enqueue(e);

    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    // Line 3 logs at start-up; a message from the page logs a warning.
    var script = "self.addEventListener('install', () => self.skipWaiting());\n"
        + "self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));\n"
        + "console.log('sw says hi', 42);\n"
        + "self.addEventListener('message', e => console.warn('ping', e.data));\n";
    _ = Task.Run(async () =>
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                using var _ = client;
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var first = await reader.ReadLineAsync() ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                var isScript = first.Contains("/sw.js");
                var body = Encoding.UTF8.GetBytes(isScript ? script : "<!doctype html><title>sw</title><p>service worker test</p>");
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: {(isScript ? "text/javascript" : "text/html")}\r\nContent-Length: {body.Length}\r\n"
                    + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(body);
            });
        }
    });

    var swWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = swSession } });
    await swWin.LoadUrl(origin + "/");
    var registered = await swWin.WebContents.ExecuteJavaScriptAsync("navigator.serviceWorker.register('/sw.js').then(r => r.scope)");
    var scope = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
    Check("serviceWorkers: registration-completed fires with the registration's scope", scope == origin + "/", $"{scope} (register: {registered})");

    long versionId = 0;
    ServiceWorkerInfo? info = null;
    for (var i = 0; i < 100 && info is null; i++)
    {
        foreach (var (id, running) in workers.GetAllRunning())
            if (running.ScriptUrl == origin + "/sw.js") (versionId, info) = (id, running);
        if (info is null) await Task.Delay(100);
    }
    Check("serviceWorkers: getAllRunning lists the worker with its scope and scriptURL",
        info is { RenderProcessId: 0 } && info.Scope == origin + "/" && info.ScriptUrl == origin + "/sw.js", info);
    Check("serviceWorkers: getInfoFromVersionID reads the same worker", info is not null && workers.GetInfoFromVersionId(versionId) == info);
    var transitions = statuses.Where(e => e.VersionId == versionId).Select(e => e.RunningStatus).ToList();
    Check("serviceWorkers: running-status-changed goes starting, then running",
        transitions.Take(2).SequenceEqual([ServiceWorkerRunningStatus.Starting, ServiceWorkerRunningStatus.Running]), string.Join(",", transitions));

    var hi = await WaitFor(() => messages.FirstOrDefault(m => m.Message == "sw says hi 42"));
    Check("serviceWorkers: console-message carries the worker's console.log with Electron's fields",
        hi is { Source: "console-api", Level: ConsoleMessageLevel.Info, LineNumber: 3 } && hi.VersionId == versionId && hi.SourceUrl == origin + "/sw.js", hi);
    await swWin.WebContents.ExecuteJavaScriptAsync("navigator.serviceWorker.ready.then(r => { r.active.postMessage('x'); return true; })");
    var warned = await WaitFor(() => messages.FirstOrDefault(m => m.Message == "ping x"));
    Check("serviceWorkers: console.warn arrives at level 2", warned is { Level: ConsoleMessageLevel.Warning, LineNumber: 4 }, warned);
    Check("serviceWorkers: each message arrives once", messages.Count(m => m.Message == "sw says hi 42") == 1, messages.Count);

    var worker = workers.GetWorkerFromVersionId(versionId);
    Check("ServiceWorkerMain: versionId, scope and scriptURL; one object per version",
        worker is not null && !worker.IsDestroyed() && worker.VersionId == versionId && worker.Scope == origin + "/"
            && worker.ScriptUrl == origin + "/sw.js" && ReferenceEquals(worker, workers.GetWorkerFromVersionId(versionId)), worker?.Scope);
    var send = worker is null ? null : await Throws(() => { worker.Send("channel", 1); return Task.CompletedTask; });
    Check("ServiceWorkerMain.send is not supported (drift SW5)", send is NotSupportedException, send?.GetType().Name);
    var taskError = worker is null ? null : await Throws(() => { worker.StartTask().End(); return Task.CompletedTask; });
    Check("ServiceWorkerMain.startTask on a running worker, then end()", worker is not null && taskError is null, taskError?.Message);

    var started = await Throws(async () =>
    {
        var again = await workers.StartWorkerForScopeAsync(origin + "/");
        if (!ReferenceEquals(again, worker)) throw new InvalidOperationException($"another worker: {again.VersionId}");
    });
    Check("serviceWorkers: startWorkerForScope resolves the running worker", started is null, started?.Message);
    var noScope = await Throws(() => workers.StartWorkerForScopeAsync(origin + "/elsewhere/"));
    Check("serviceWorkers: startWorkerForScope rejects a scope with no registration",
        noScope is InvalidOperationException { Message: "Failed to start service worker." }, noScope?.Message);

    // Clearing the registration makes the version redundant, which stops it and destroys its ServiceWorkerMain.
    await swSession.ClearStorageDataAsync(new ClearStorageDataOptions { Origin = origin, Storages = ["serviceworkers"] });
    for (var i = 0; i < 100 && worker is { } alive && !alive.IsDestroyed(); i++) await Task.Delay(100);
    transitions = statuses.Where(e => e.VersionId == versionId).Select(e => e.RunningStatus).ToList();
    Check("serviceWorkers: running-status-changed goes stopping, then stopped once the registration is cleared",
        transitions.TakeLast(2).SequenceEqual([ServiceWorkerRunningStatus.Stopping, ServiceWorkerRunningStatus.Stopped]), string.Join(",", transitions));
    Check("ServiceWorkerMain: destroyed once redundant and stopped; scope and scriptURL empty",
        worker is not null && worker.IsDestroyed() && worker.Scope == "" && worker.ScriptUrl == "" && workers.GetWorkerFromVersionId(versionId) is null);
    Check("serviceWorkers: getAllRunning no longer lists it", !workers.GetAllRunning().ContainsKey(versionId), workers.GetAllRunning().Count);
    var gone = await Throws(() => { workers.GetInfoFromVersionId(versionId); return Task.CompletedTask; });
    Check("serviceWorkers: getInfoFromVersionID throws Electron's error for it", gone is InvalidOperationException { Message: "Could not find service worker with that version_id" }, gone?.Message);
    swWin.Destroy();
    listener.Stop();
}

async Task CookieChangeChecks()
{
    var watched = Session.FromPartition("cookie-events");
    watched.Protocol.Handle("https", request => new Uri(request.Url).AbsolutePath switch
    {
        ['/', 's', 'e', 't', '/', ..] and var path => PageSettingCookie($"{path[5..]}=1; Path=/"),
        ['/', 'u', 'n', 's', 'e', 't', '/', ..] and var path => PageSettingCookie($"{path[7..]}=; Path=/; Max-Age=0"),
        _ => ProtocolResponse.Text(Page, "text/html"),
    });
    static ProtocolResponse PageSettingCookie(string cookie) => new()
    {
        ContentType = "text/html", Body = new MemoryStream(Encoding.UTF8.GetBytes(Page)),
        Headers = new Dictionary<string, string> { ["Set-Cookie"] = cookie },
    };
    var changes = new List<string>();
    EventHandler<CookieChangedEventArgs> onChanged = (_, e) =>
    {
        lock (changes) changes.Add($"{e.Cookie.Name}={e.Cookie.Value}:{e.Cause}:{(e.Removed ? "removed" : "added")}");
    };
    string Take()
    {
        lock (changes)
        {
            var all = string.Join(" ", changes);
            changes.Clear();
            return all;
        }
    }
    async Task<(string Changes, long Ms)> WaitForChange(string expected, int timeoutMs = 4000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var seen = "";
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            seen = (seen + " " + Take()).Trim();
            if (seen == expected) break;
            await Task.Delay(20);
        }
        return (seen, clock.ElapsedMilliseconds);
    }
    watched.Cookies.Changed += onChanged;
    // The first read of the store, which later reads are compared with.
    await Task.Delay(500);
    await watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "ev", Value = "1" });
    var inserted = Take();
    Check("changed: set() of a new cookie raises inserted before set() resolves", inserted == "ev=1:Inserted:added", inserted);
    await watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "ev", Value = "2" });
    var overwritten = Take();
    Check("changed: a new value raises overwrite (removed) then inserted", overwritten == "ev=1:Overwrite:removed ev=2:Inserted:added", overwritten);
    await watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "ev", Value = "2" });
    var same = Take();
    Check("changed: an identical cookie raises overwrite then inserted-no-change-overwrite",
        same == "ev=2:Overwrite:removed ev=2:InsertedNoChangeOverwrite:added", same);
    await watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "ev", Value = "2", ExpirationDate = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
    var expiryOnly = Take();
    Check("changed: a new expiry raises overwrite then inserted-no-value-change-overwrite",
        expiryOnly == "ev=2:Overwrite:removed ev=2:InsertedNoValueChangeOverwrite:added", expiryOnly);
    await watched.Cookies.RemoveAsync(Site, "ev");
    var removed = Take();
    Check("changed: remove() raises explicit (removed)", removed == "ev=2:Explicit:removed", removed);
    await watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "gone", Value = "1" });
    Take();
    await watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "gone", Value = "1", ExpirationDate = 0 });
    var expiredOverwrite = Take();
    Check("changed: set() with a past expiry raises expired-overwrite", expiredOverwrite == "gone=1:ExpiredOverwrite:removed", expiredOverwrite);
    await watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "short", Value = "1", ExpirationDate = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds() / 1000.0 });
    Take();
    var (expired, expiredMs) = await WaitForChange("short=1:Expired:removed", 4000);
    Check("changed: expiry raises expired (within the 1 s poll)", expired == "short=1:Expired:removed", $"{expired} after {expiredMs} ms");

    // Changes made by pages: a response's Set-Cookie and document.cookie.
    var page = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = watched } });
    // Three in a row, each well inside the poll: a poll that happened to land early would not pass all three.
    var responses = new List<string>();
    foreach (var name in new[] { "net1", "net2", "net3" })
    {
        await page.LoadUrl($"{Site}set/{name}");
        var (fromResponse, responseMs) = await WaitForChange($"{name}=1:Inserted:added");
        responses.Add($"{fromResponse} after {responseMs} ms");
        if (fromResponse != $"{name}=1:Inserted:added" || responseMs >= 400) break;
    }
    Check("changed: a response's Set-Cookie raises inserted within 400 ms, ahead of the poll",
        responses.Count == 3 && responses.All(r => r.Contains(":Inserted:added after ") && int.Parse(r.Split(' ')[^2]) < 400), string.Join("; ", responses));
    await page.LoadUrl($"{Site}unset/net1");
    var (unsetByResponse, unsetMs) = await WaitForChange("net1=1:ExpiredOverwrite:removed");
    Check("changed: a response's expired Set-Cookie raises expired-overwrite", unsetByResponse == "net1=1:ExpiredOverwrite:removed", $"{unsetByResponse} after {unsetMs} ms");
    await page.WebContents.ExecuteJavaScriptAsync("document.cookie = 'js=1; path=/'; 1");
    var (fromScript, scriptMs) = await WaitForChange("js=1:Inserted:added", 1500);
    Check("changed: document.cookie raises inserted (within the 1 s poll)", fromScript == "js=1:Inserted:added", $"{fromScript} after {scriptMs} ms");
    await page.WebContents.ExecuteJavaScriptAsync("document.cookie = 'js=; path=/; max-age=0'; 1");
    var (deletedByScript, deletedMs) = await WaitForChange("js=1:ExpiredOverwrite:removed", 1500);
    Check("changed: deleting through document.cookie raises expired-overwrite (within the 1 s poll)", deletedByScript == "js=1:ExpiredOverwrite:removed", $"{deletedByScript} after {deletedMs} ms");
    page.Close();

    watched.Cookies.Changed -= onChanged;
    await watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "unwatched", Value = "1" });
    await Task.Delay(1500);
    var afterRemoval = Take();
    Check("changed: no events once the last handler is removed", afterRemoval == "", afterRemoval);

    // set() rejects with Electron's message, from Chromium's inclusion status.
    var badDomain = await Throws(() => watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "bad", Domain = "elsewhere.test" }));
    Check("set(): another site's domain rejects with Electron's message", badDomain is ArgumentException && badDomain.Message ==
        "Failed to set cookie - The cookie's domain attribute did not match the domain of the URL attempting access.The cookie was set with an invalid Domain attribute.EXCLUDE_DOMAIN_MISMATCH, EXCLUDE_INVALID_DOMAIN, DO_NOT_WARN, NO_EXEMPTION",
        badDomain?.Message);
    var badPrefix = await Throws(() => watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "__Host-x", Path = "/app", Secure = true }));
    Check("set(): a __Host- cookie with a path rejects with Electron's message", badPrefix?.Message ==
        "Failed to set cookie - The cookie was set with an invalid __Host- or __Secure- prefix.EXCLUDE_INVALID_PREFIX, DO_NOT_WARN, NO_EXEMPTION", badPrefix?.Message);
    var insecureNone = await Throws(() => watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "none", SameSite = CookieSameSite.NoRestriction, Secure = false }));
    Check("set(): SameSite=None without Secure rejects with Electron's message", insecureNone?.Message ==
        "Failed to set cookie - The cookie specified SameSite=None, but it was not Secure.EXCLUDE_SAMESITE_NONE_INSECURE, DO_NOT_WARN, NO_EXEMPTION", insecureNone?.Message);
    var secureOnHttp = await Throws(() => watched.Cookies.SetAsync(new CookieDetails("http://oriel.test/") { Name = "s", Secure = true }));
    Check("set(): a Secure cookie for http rejects with Electron's message", secureOnHttp?.Message ==
        "Failed to set cookie - The cookie was Secure, but the URL was not allowed to access Secure cookies.EXCLUDE_SECURE_ONLY, DO_NOT_WARN, NO_EXEMPTION", secureOnHttp?.Message);
    var badUrl = await Throws(() => watched.Cookies.SetAsync(new CookieDetails("not a url") { Name = "u" }));
    Check("set(): an invalid URL rejects with Electron's message", badUrl?.Message ==
        "Failed to set cookie - The cookie was set with an invalid Domain attribute.EXCLUDE_INVALID_DOMAIN, DO_NOT_WARN, NO_EXEMPTION", badUrl?.Message);
    var defaulted = await Throws(() => watched.Cookies.SetAsync(new CookieDetails(Site) { Name = "noneDefault", Value = "1", SameSite = CookieSameSite.NoRestriction }));
    var noneDefault = (await watched.Cookies.GetAsync(new CookieFilter { Name = "noneDefault" })).SingleOrDefault();
    Check("set(): SameSite=None without a Secure choice is made secure, as in Electron", defaulted is null && noneDefault is { Secure: true }, defaulted?.Message ?? noneDefault?.ToString());
}
static async Task<T?> WaitFor<T>(Func<T?> probe) where T : class
{
    for (var i = 0; i < 100; i++)
    {
        if (probe() is { } found) return found;
        await Task.Delay(50);
    }
    return null;
}

// A window made on the UI thread in a brand-new on-disk session, which the UI thread cannot wait for: as Electron's, it
// exists when the constructor returns and takes calls, and a close before the profile loads ends it for good.
async Task UiThreadFreshSessionChecks()
{
    static bool Loading(Session session) => typeof(Session).GetProperty("Initialized", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session) is Task { IsCompleted: false };
    var wanted = new Oriel.Rectangle(100, 120, 520, 340);
    var early = Oriel.Cef.CefThread.InvokeOnUi(() =>
    {
        var session = Session.FromPartition("persist:fresh-ui");
        var loading = Loading(session);
        var window = new BrowserWindow(new BrowserWindowOptions { Show = false, Width = 400, Height = 300, Title = "options title", WebPreferences = new WebPreferences { Session = session } });
        var destroyedAtOnce = window.IsDestroyed;
        var optionsTitle = window.GetTitle();
        window.SetTitle("early title");
        window.SetBounds(wanted);
        return (Window: window, Loading: loading, DestroyedAtOnce: destroyedAtOnce, OptionsTitle: optionsTitle, Title: window.GetTitle(), Bounds: window.GetBounds(),
            Listed: BrowserWindow.GetAllWindows().Contains(window));
    });
    Check("a window made on the UI thread while its new session loads exists at once, as Electron's",
        early.Loading && !early.DestroyedAtOnce && early.Listed && early.OptionsTitle == "options title",
        $"loading={early.Loading} destroyed={early.DestroyedAtOnce} listed={early.Listed} {early.OptionsTitle}");
    Check("SetTitle and SetBounds right after that constructor take effect", early.Title == "early title" && early.Bounds == wanted, $"{early.Title} {early.Bounds}");
    // The profile's load is inside this wait, which the windows made off the UI thread wait for separately.
    var earlyLoad = await Throws(() => early.Window.LoadUrl("data:text/html,<title>late</title>late").WaitAsync(TimeSpan.FromSeconds(20)));
    var laterBounds = early.Window.GetBounds();
    Check("its page joins once the profile loads, and the early bounds stay", earlyLoad is null && early.Window.WebContents.GetUrl().StartsWith("data:text/html", StringComparison.Ordinal)
        && laterBounds == wanted && !early.Window.IsDestroyed, $"{earlyLoad?.GetType().Name} {laterBounds}");
    var content = early.Window.GetContentBounds();
    var pageSize = (await early.Window.WebContents.ExecuteJavaScriptAsync("innerWidth + 'x' + innerHeight"))?.ToString();
    // At a fractional scale the content's DIP size is rounded and the page's viewport is not, so they can differ by one.
    var pageSlack = Screen.GetDisplayMatching(content).ScaleFactor % 1 == 0 ? 0 : 1;
    var pageDims = pageSize?.Split('x') is [var pw, var ph] && int.TryParse(pw, out var pwi) && int.TryParse(ph, out var phi) ? (pwi, phi) : (-99, -99);
    Check("the late page fills the content area (a DIP of slack at a fractional scale)",
        Math.Abs(pageDims.Item1 - content.Width) <= pageSlack && Math.Abs(pageDims.Item2 - content.Height) <= pageSlack,
        $"{pageSize} vs {content.Width}x{content.Height}, slack {pageSlack}");
    early.Window.Destroy();

    foreach (var destroy in new[] { false, true })
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageGone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = 0;
        var (window, loading) = Oriel.Cef.CefThread.InvokeOnUi(() =>
        {
            var session = Session.FromPartition(destroy ? "persist:fresh-ui-destroy" : "persist:fresh-ui-close");
            var loading = Loading(session);
            var window = new BrowserWindow(new BrowserWindowOptions { WebPreferences = new WebPreferences { Session = session } });
            window.Closing += (_, _) => Interlocked.Increment(ref closing);
            window.Closed += (_, _) => closed.TrySetResult();
            window.WebContents.Destroyed += (_, _) => pageGone.TrySetResult();
            if (destroy) window.Destroy();
            else window.Close();
            return (window, loading);
        });
        var closedInTime = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(t => t.IsCompletedSuccessfully);
        var pageGoneInTime = await pageGone.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(t => t.IsCompletedSuccessfully);
        // Long enough for the profile to load and a page to have been made.
        await Task.Delay(2000);
        var name = destroy ? "Destroy" : "Close";
        Check($"{name} before the new session loads raises Closed{(destroy ? " and no Closing" : " after Closing")}, and the window never comes back",
            loading && closedInTime && closing == (destroy ? 0 : 1) && window.IsDestroyed && !window.IsVisible && !BrowserWindow.GetAllWindows().Contains(window),
            $"loading={loading} closed={closedInTime} closing={closing} destroyed={window.IsDestroyed} visible={window.IsVisible}");
        Check($"{name} before the new session loads destroys the page that was never made", pageGoneInTime && window.WebContents.IsDestroyed(), $"{pageGoneInTime}");
    }
}

// Session.DownloadUrl's headers over real HTTP: DevTools reports a request's URL canonical and without its fragment, and
// redirect hops and requests that never come must not cost other downloads their headers.
async Task DownloadUrlHeaderChecks(Session downloads, string downloadDir)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Host, string Path, string? Value)>();
    _ = ServeHeaderDownloads(listener, port, seen);
    var done = 0;
    void OnWillDownload(object? _, WillDownloadEventArgs e)
    {
        e.Item.SetSavePath(Path.Combine(downloadDir, $"headers-{Guid.NewGuid():N}.bin"));
        e.Item.Done += (_, _) => Interlocked.Increment(ref done);
    }
    downloads.WillDownload += OnWillDownload;
    var origin = $"http://127.0.0.1:{port}";
    DownloadUrlOptions Marked(string value) => new() { Headers = new Dictionary<string, string> { ["X-Download"] = value } };
    downloads.DownloadUrl("x-never://nothing", Marked("never"));
    downloads.DownloadUrl(origin + "/frag.bin#part", Marked("frag"));
    downloads.DownloadUrl(origin, Marked("root"));
    downloads.DownloadUrl($"http://b\u00fccher.localhost:{port}/idn.bin", Marked("idn"));
    downloads.DownloadUrl(origin + "/redirect", Marked("redirect"));
    downloads.DownloadUrl(origin + "/cross", Marked("cross"));
    downloads.DownloadUrl(origin + "/one.bin", Marked("one"));
    downloads.DownloadUrl(origin + "/two.bin", Marked("two"));
    // Seven downloads, two of them redirected; the one that never comes may still end as a failed download.
    const int Expected = 7, Requests = 9;
    for (var i = 0; i < 200 && (Volatile.Read(ref done) < Expected || seen.Count < Requests); i++) await Task.Delay(100);
    downloads.WillDownload -= OnWillDownload;
    bool Saw(string path, string value, string? host = null) => seen.Any(r => r.Path == path && r.Value == value && (host is null || r.Host == host));
    var all = string.Join(" ", seen.Select(r => $"{r.Host}{r.Path}={r.Value}"));
    Check("Session.DownloadUrl's headers reach a URL with a fragment", Saw("/frag.bin", "frag"), all);
    Check("Session.DownloadUrl's headers reach a host given without its slash", Saw("/", "root"), all);
    Check("Session.DownloadUrl's headers reach an IDN host", Saw("/idn.bin", "idn", $"xn--bcher-kva.localhost:{port}"), all);
    Check("Session.DownloadUrl's headers stay on a same-origin redirect, as Chromium keeps a download's extra headers",
        Saw("/redirect", "redirect") && Saw("/final.bin", "redirect"), all);
    Check("Session.DownloadUrl's headers stay on a cross-origin redirect, which downloads follow", Saw("/cross", "cross") && Saw("/cross-final.bin", "cross", $"localhost:{port}"), all);
    Check("two concurrent Session.DownloadUrl calls each send their own headers", Saw("/one.bin", "one") && Saw("/two.bin", "two"), all);
    Check("a Session.DownloadUrl whose request never comes costs the others nothing", Volatile.Read(ref done) >= Expected && seen.All(r => r.Value != "never"), $"done={done} {all}");
    listener.Stop();
}

static async Task ServeHeaderDownloads(TcpListener listener, int port, System.Collections.Concurrent.ConcurrentQueue<(string Host, string Path, string? Value)> seen)
{
    while (true)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (Exception) { return; }
        _ = Task.Run(async () =>
        {
            using var _ = client;
            var stream = client.GetStream();
            var head = new StringBuilder();
            var buffer = new byte[4096];
            while (!head.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return;
                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            var lines = head.ToString().Split("\r\n");
            var path = lines[0].Split(' ')[1];
            string? Header(string name) => lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault();
            seen.Enqueue((Header("Host") ?? "", path, Header("X-Download")));
            var response = path switch
            {
                "/redirect" => "HTTP/1.1 302 Found\r\nLocation: /final.bin\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                "/cross" => $"HTTP/1.1 302 Found\r\nLocation: http://localhost:{port}/cross-final.bin\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                _ => "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"d.bin\"\r\nContent-Length: 4\r\nConnection: close\r\n\r\nbody",
            };
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        });
    }
}

// Serves "tls ok" over TLS with the given certificate, to every connection; with requireClient, says which client
// certificate came.
static async Task ServeTls(TcpListener listener, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, bool requireClient = false,
    Action<string>? receivedThumbprint = null, System.Security.Authentication.SslProtocols protocols = System.Security.Authentication.SslProtocols.None)
{
    while (true)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (Exception) { return; }
        _ = Task.Run(async () =>
        {
            using (client)
            {
                try
                {
                    using var tls = new System.Net.Security.SslStream(client.GetStream(), false,
                        // Self-signed client certificates, or none, are what the test sends.
                        (_, _, _, errors) => (errors & ~(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors | System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) == 0);
                    await tls.AuthenticateAsServerAsync(certificate, requireClient, protocols, false);
                    var who = tls.RemoteCertificate is { } remote ? new System.Security.Cryptography.X509Certificates.X509Certificate2(remote).GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) : "none";
                    receivedThumbprint?.Invoke(tls.RemoteCertificate is { } sent ? new System.Security.Cryptography.X509Certificates.X509Certificate2(sent).Thumbprint : "none");
                    await Respond(tls, 200, "OK", "", requireClient ? $"tls ok client={who}" : "tls ok");
                }
                catch (Exception) { }
            }
        });
    }
}

// Answers "logged in" to Basic oriel:secret, else a 401 asking for it.
static async Task ServeBasicAuth(TcpListener listener)
{
    var expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("oriel:secret"));
    while (true)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (Exception) { return; }
        _ = Task.Run(async () =>
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var authorized = false;
                    var requestLine = await reader.ReadLineAsync() ?? "";
                    for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                        if (line.Equals("Authorization: " + expected, StringComparison.OrdinalIgnoreCase)) authorized = true;
                    if (authorized) await Respond(stream, 200, "OK", "", "logged in");
                    else await Respond(stream, 401, "Unauthorized", "WWW-Authenticate: Basic realm=\"oriel\"\r\n", "denied");
                }
                catch (Exception) { }
            }
        });
    }
}

// Answers "ntlm <user>" once a client completes NTLM on its connection, else a 401 offering NTLM. NTLM authenticates
// the connection, not the request, so the connection stays open through the handshake.
static async Task ServeNtlm(TcpListener listener)
{
    while (true)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (Exception) { return; }
        _ = Task.Run(async () =>
        {
            using (client)
            using (var ntlm = new System.Net.Security.NegotiateAuthentication(new System.Net.Security.NegotiateAuthenticationServerOptions { Package = "NTLM" }))
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    while (await reader.ReadLineAsync() is { Length: > 0 })
                    {
                        string? token = null;
                        for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                            if (line.StartsWith("Authorization: NTLM ", StringComparison.OrdinalIgnoreCase)) token = line["Authorization: NTLM ".Length..];
                        async Task Send(int status, string headers, string body)
                        {
                            var bytes = Encoding.ASCII.GetBytes($"<!doctype html><body>{body}</body>");
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: text/html\r\nContent-Length: {bytes.Length}\r\n{headers}\r\n"));
                            await stream.WriteAsync(bytes);
                        }
                        if (token is null)
                        {
                            await Send(401, "WWW-Authenticate: NTLM\r\n", "denied");
                            continue;
                        }
                        var reply = ntlm.GetOutgoingBlob(token, out var status);
                        if (status == System.Net.Security.NegotiateAuthenticationStatusCode.ContinueNeeded)
                            await Send(401, $"WWW-Authenticate: NTLM {reply}\r\n", "challenge");
                        else if (status == System.Net.Security.NegotiateAuthenticationStatusCode.Completed)
                            await Send(200, "", $"ntlm {ntlm.RemoteIdentity.Name}");
                        else
                            await Send(401, "WWW-Authenticate: NTLM\r\n", $"failed {status}");
                    }
                }
                catch (Exception) { }
            }
        });
    }
}

static async Task Respond(Stream stream, int status, string reason, string headers, string body)
{
    if (stream is System.Net.Security.SslStream)
    {
        var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
    }
    var bytes = Encoding.ASCII.GetBytes($"<!doctype html><body>{body}</body>");
    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html\r\nContent-Length: {bytes.Length}\r\n{headers}Connection: close\r\n\r\n");
    await stream.WriteAsync(head);
    await stream.WriteAsync(bytes);
    await stream.FlushAsync();
}

// /file.bin slowly, with Content-Disposition, ETag, Last-Modified and byte ranges; /redirect.bin to it; /drop.bin
// resets the connection halfway the first time; /forbidden.bin is a 403.
static async Task ServeDownloads(TcpListener listener, DownloadServer state)
{
    while (true)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (Exception) { return; }
        _ = Task.Run(async () =>
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var path = (await reader.ReadLineAsync() ?? "").Split(' ') is [_, var target, ..] ? target : "/";
                    string? range = null;
                    for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line["Range:".Length..].Trim();
                    if (range is not null) state.Ranges.Enqueue(range);
                    if (path == "/redirect.bin")
                    {
                        await Respond(stream, 302, "Found", "Location: /file.bin\r\n", "moved");
                        return;
                    }
                    if (path == "/forbidden.bin")
                    {
                        await Respond(stream, 403, "Forbidden", "", "no");
                        return;
                    }
                    var body = DownloadServer.Body;
                    var from = range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) ? long.Parse(range[6..].Split('-')[0], CultureInfo.InvariantCulture) : 0;
                    var head = new StringBuilder(from > 0 ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
                        .Append("Content-Type: application/octet-stream\r\n")
                        .Append($"Content-Disposition: {DownloadServer.Disposition}\r\n")
                        .Append("ETag: \"oriel-v1\"\r\nLast-Modified: Tue, 06 Oct 2026 12:00:00 GMT\r\nAccept-Ranges: bytes\r\n")
                        .Append($"Content-Length: {body.Length - from}\r\n");
                    if (from > 0) head.Append($"Content-Range: bytes {from}-{body.Length - 1}/{body.Length}\r\n");
                    head.Append("Connection: close\r\n\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                    var drop = path == "/drop.bin" && Interlocked.Exchange(ref state.Dropped, 1) == 0;
                    const int Chunk = 64 * 1024;
                    for (var offset = from; offset < body.Length; offset += Chunk)
                    {
                        if (drop && offset >= body.Length / 2)
                        {
                            // An RST, not a FIN: Chromium calls that a network failure, which only the user resumes; a FIN is
                            // a length mismatch it resumes itself. Disposing the TcpClient would shut the stream down (a FIN) first.
                            client.Client.Close(0);
                            return;
                        }
                        await stream.WriteAsync(body.AsMemory((int)offset, (int)Math.Min(Chunk, body.Length - offset)));
                        await stream.FlushAsync();
                        await Task.Delay(40);
                    }
                }
                catch (Exception) { }
            }
        });
    }
}

// Permissions: Electron's request, check and display media handlers, driven by real page requests.
async Task PermissionChecks(bool mediaOnly = false)
{
    // Geolocation: a refusal is error 1; a grant goes on to the (absent) location source, so any other outcome.
    const string Geolocation = "new Promise(r => navigator.geolocation.getCurrentPosition(() => r('ok'), e => r(e.code === 1 ? 'denied' : 'granted'), { timeout: 2000 }))";
    const string Origin = "https://oriel.test/";
    static string Query(string descriptor) => $"navigator.permissions.query({descriptor}).then(s => s.state, e => e.name)";
    async Task<BrowserWindow> Open(Session session, bool show = false)
    {
        session.Protocol.Handle("https", r => ProtocolResponse.Text(r.Url.EndsWith("/frame", StringComparison.Ordinal) ? "<!doctype html><title>frame</title>" : Page, "text/html"));
        var w = new BrowserWindow(new BrowserWindowOptions { Show = show, WebPreferences = new WebPreferences { Session = session } });
        await w.LoadUrl(Site);
        return w;
    }
    static async Task<string?> Run(BrowserWindow w, string code, bool gesture = false)
    {
        try { return (await w.WebContents.ExecuteJavaScriptAsync(code, gesture).WaitAsync(TimeSpan.FromSeconds(15)))?.ToString(); }
        catch (TimeoutException) { return "timeout"; }
    }
    // Made first: a window opened at once in a new on-disk session can fail to get its browser.
    var notifying = Session.FromPartition("persist:perm-notify");
    var silencing = Session.FromPartition("persist:perm-notify-deny");
    // A handler that records what it is asked and answers with allow().
    static Func<WebContents, string, PermissionRequestDetails, Task<bool>> Recorder(List<(WebContents Page, string Permission, PermissionRequestDetails Details)> asked, Func<bool> allow)
        => (page, permission, details) =>
        {
            lock (asked) asked.Add((page, permission, details));
            return Task.FromResult(allow());
        };
    static string Seen(List<(WebContents Page, string Permission, PermissionRequestDetails Details)> asked)
    {
        lock (asked) return string.Join("; ", asked.Select(a => $"{a.Permission} {a.Details.RequestingUrl} main={a.Details.IsMainFrame} [{string.Join(",", a.Details.MediaTypes)}] {a.Details.SecurityOrigin}"));
    }

    // getUserMedia on fake devices, in the MediaChild process. The handler decides, not Chromium's prompt.
    async Task DeviceMediaChecks()
    {
        // An unsigned app never gets macOS's consent, and Chrome style waits for it (drift E33).
        if (OperatingSystem.IsMacOS() && MacMediaConsent.Read() is var (audio, video) && (audio != MacMediaConsent.Authorized || video != MacMediaConsent.Authorized))
        {
            Console.WriteLine($"SKIP  getUserMedia checks: macOS media consent is audio={audio} video={video} ({MacMediaConsent.Authorized} = authorized)");
            return;
        }
        var mediaAsked = new List<(WebContents Page, string Permission, PermissionRequestDetails Details)>();
        var mediaAllow = true;
        var devicesSession = Session.FromPartition("perm-media-devices");
        devicesSession.SetPermissionRequestHandler(Recorder(mediaAsked, () => mediaAllow));
        var mediaWin = await Open(devicesSession);
        // The main run launches this one with --lang=de-DE; LANGUAGE outranks LC_ALL, as in Electron.
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("LANGUAGE") is { Length: > 0 } outranking)
            Console.WriteLine($"SKIP  Linux --lang check: LANGUAGE={outranking} outranks it");
        else if (OperatingSystem.IsLinux())
        {
            var language = await Run(mediaWin, "navigator.language");
            // Chromium's accept list for its locale, so German only if it loaded the de resources (the exact value is A39).
            Check("Linux: --lang picks the app locale, as Electron's LC_ALL", app.GetLocale() == "de" && language?.StartsWith("de") == true,
                $"{app.GetLocale()} {language} LC_ALL={Environment.GetEnvironmentVariable("LC_ALL")}");
        }
        const string Microphone = "navigator.mediaDevices.getUserMedia({ audio: true }).then(s => { s.getTracks().forEach(t => t.stop()); return 'ok'; }, e => e.name)";
        const string CameraAndMicrophone = "navigator.mediaDevices.getUserMedia({ audio: true, video: true }).then(s => { s.getTracks().forEach(t => t.stop()); return 'ok'; }, e => e.name)";
        var heard = await Run(mediaWin, Microphone);
        Check("getUserMedia asks the handler for media: mediaTypes ['audio'], securityOrigin, the frame's URL and isMainFrame", heard != "NotAllowedError"
            && mediaAsked is [(_, "media", { MediaTypes: ["audio"], SecurityOrigin: Origin, RequestingUrl: Site, IsMainFrame: true })], $"{heard} {Seen(mediaAsked)}");
        var labels = await Run(mediaWin, "navigator.mediaDevices.getUserMedia({ audio: true, video: true }).then(s => { const l = s.getTracks().map(t => t.label).join(','); s.getTracks().forEach(t => t.stop()); return l; }, e => e.name)");
        Check("CommandLine.AppendSwitch before Run reaches Chromium: capture uses the fake devices", app.CommandLine.HasSwitch("use-fake-device-for-media-stream")
            && labels?.Contains("fake", StringComparison.OrdinalIgnoreCase) == true, labels);
        lock (mediaAsked) mediaAsked.Clear();
        var both = await Run(mediaWin, CameraAndMicrophone);
        Check("camera and microphone together are one media request, mediaTypes audio then video, as Electron lists them", both != "NotAllowedError"
            && mediaAsked is [(_, "media", { MediaTypes: ["audio", "video"] })], $"{both} {Seen(mediaAsked)}");
        lock (mediaAsked) mediaAsked.Clear();
        mediaAllow = false;
        var refusedMedia = await Run(mediaWin, Microphone);
        var refusedMediaAgain = await Run(mediaWin, Microphone);
        Check("a handler that refuses media fails getUserMedia with NotAllowedError, and is asked on every request", refusedMedia == "NotAllowedError"
            && refusedMediaAgain == "NotAllowedError" && mediaAsked.Count == 2, $"{refusedMedia} {refusedMediaAgain} {Seen(mediaAsked)}");
        mediaAllow = true;
    }

    if (mediaOnly)
    {
        await DeviceMediaChecks();
        return;
    }

    // The getUserMedia checks, from a MediaChild process; its PASS and FAIL lines are this run's. First, while no window of
    // this run has closed: closing the last one would quit the app.
    var childStart = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
    childStart.ArgumentList.Add(MediaChild);
    if (OperatingSystem.IsLinux()) childStart.ArgumentList.Add("--lang=de-DE");
    using (var child = System.Diagnostics.Process.Start(childStart)!)
    {
        var childErrors = child.StandardError.ReadToEndAsync();
        string childOutput;
        // Output to EOF only: WaitForExit can hang inside the CEF process.
        try { childOutput = await child.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(240)); }
        catch (TimeoutException)
        {
            child.Kill(entireProcessTree: true);
            childOutput = "";
        }
        var childLines = childOutput.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var forwarded = 0;
        foreach (var line in childLines.Where(l => l.StartsWith("PASS", StringComparison.Ordinal) || l.StartsWith("FAIL", StringComparison.Ordinal)))
        {
            results.Add(line);
            Console.WriteLine(line);
            forwarded++;
        }
        var skipped = childLines.Where(l => l.StartsWith("SKIP", StringComparison.Ordinal)).ToList();
        foreach (var line in skipped) Console.WriteLine(line);
        Check("the getUserMedia checks ran in their own process, with fake devices", (forwarded > 0 || skipped.Count > 0)
            && childLines.Any(l => l.StartsWith("RESULT: ", StringComparison.Ordinal) && !l.Contains("no result")),
            childLines.LastOrDefault(l => l.StartsWith("RESULT", StringComparison.Ordinal)) ?? (childErrors.IsCompleted ? childErrors.Result.Split('\n').LastOrDefault(l => l.Length > 0) : "no output"));
        // The child's data directory, once its files are let go.
        var childData = Path.Combine(Path.GetTempPath(), $"oriel-session-media-{child.Id}");
        for (var attempt = 0; attempt < 40 && Directory.Exists(childData); attempt++)
        {
            try { Directory.Delete(childData, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(250); }
        }    }

    // With Windows' location access off, its provider fails every request as PERMISSION_DENIED, granted or not
    // (LocationProviderWinrt::OnStatusChanged), so a grant shows only in what the handler was asked.
#pragma warning disable CA1416
    static bool WindowsLocationOff() => OperatingSystem.IsWindows() && new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine }.Any(hive =>
        hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location")?.GetValue("Value") as string == "Deny");
#pragma warning restore CA1416
    var locationOff = WindowsLocationOff();
    var byDefault = await Open(Session.FromPartition("perm-default"));
    var grantedByDefault = await Run(byDefault, Geolocation);
    if (locationOff) Console.WriteLine($"SKIP  with no permission handler, a permission is granted: Windows location access is off, so a grant reads as denied ({grantedByDefault})");
    else Check("with no permission handler, a permission is granted, as Electron", grantedByDefault is "granted" or "ok", grantedByDefault);
    var queriedByDefault = await Run(byDefault, Query("{ name: 'camera' }"));
    Check("with no check handler, navigator.permissions.query is granted, as Electron", queriedByDefault == "granted", queriedByDefault);
    byDefault.Destroy();

    var refusals = new List<(WebContents Page, string Permission, PermissionRequestDetails Details)>();
    var refusing = Session.FromPartition("perm-deny");
    refusing.SetPermissionRequestHandler(Recorder(refusals, () => false));
    var refusingWin = await Open(refusing);
    var denied = await Run(refusingWin, Geolocation);
    Check("a permission handler that refuses denies it, and gets the page, the name, the frame's URL and isMainFrame", denied == "denied"
        && refusals is [(var asker, "geolocation", { RequestingUrl: Site, IsMainFrame: true, MediaTypes: [], SecurityOrigin: null })] && ReferenceEquals(asker, refusingWin.WebContents),
        $"{denied} {Seen(refusals)}");
    var deniedAgain = await Run(refusingWin, Geolocation);
    Check("a refused permission asks the handler again on the next request, as Electron", deniedAgain == "denied" && refusals.Count == 2, $"{deniedAgain} {Seen(refusals)}");
    refusingWin.Destroy();

    var grants = new List<(WebContents Page, string Permission, PermissionRequestDetails Details)>();
    var granting = Session.FromPartition("perm-grant");
    granting.SetPermissionRequestHandler(Recorder(grants, () => true));
    var grantingWin = await Open(granting);
    var granted = await Run(grantingWin, Geolocation);
    var grantedAgain = await Run(grantingWin, Geolocation);
    Check("a permission handler that grants allows it; Chromium then remembers the grant for the origin (drift E29)",
        (locationOff ? granted is not (null or "timeout") && grantedAgain is not (null or "timeout") : granted is "granted" or "ok" && grantedAgain is "granted" or "ok") && grants.Count == 1,
        $"{granted} {grantedAgain} {Seen(grants)}{(locationOff ? " (Windows location access off: outcomes read as denied)" : "")}");
    grantingWin.Destroy();

    // Notifications: Chromium refuses them unasked in an in-memory profile, so these sessions are on disk. A grant from an
    // earlier run would answer unasked unless the session forgot it at start-up.
    var notifyAsked = new List<(WebContents Page, string Permission, PermissionRequestDetails Details)>();
    notifying.SetPermissionRequestHandler(Recorder(notifyAsked, () => true));
    var notifyWin = await Open(notifying);
    var notifyGranted = await Run(notifyWin, "Notification.requestPermission()", gesture: true);
    Check("Notification.requestPermission asks the handler for notifications, also after earlier runs granted it", notifyGranted == "granted"
        && notifyAsked is [(_, "notifications", { RequestingUrl: Site, IsMainFrame: true })], $"{notifyGranted} {Seen(notifyAsked)}");
    notifyWin.Destroy();
    var silenced = new List<(WebContents Page, string Permission, PermissionRequestDetails Details)>();
    silencing.SetPermissionRequestHandler(Recorder(silenced, () => false));
    var silencingWin = await Open(silencing);
    var notifyDenied = await Run(silencingWin, "Notification.requestPermission()", gesture: true);
    var repeated = await Run(silencingWin, "Notification.requestPermission()", gesture: true);
    var askedInDocument = silenced.Count;
    await silencingWin.LoadUrl(Site);
    var notifyDeniedAgain = await Run(silencingWin, "Notification.requestPermission()", gesture: true);
    Check("a refused notification permission is denied, and the next document's request asks again; Chromium ignores a repeat in the same document (drift E29)",
        notifyDenied == "denied" && repeated == "default" && askedInDocument == 1 && notifyDeniedAgain == "denied"
        && silenced.Count == 2 && silenced.All(s => s.Permission == "notifications"), $"{notifyDenied} {repeated} {notifyDeniedAgain} {Seen(silenced)}");
    silencingWin.Destroy();
    var incognitoAsked = new List<(WebContents Page, string Permission, PermissionRequestDetails Details)>();
    var incognito = Session.FromPartition("perm-notify-memory");
    incognito.SetPermissionRequestHandler(Recorder(incognitoAsked, () => true));
    var incognitoWin = await Open(incognito);
    var incognitoNotify = await Run(incognitoWin, "Notification.requestPermission()", gesture: true);
    Check("in an in-memory session Chromium refuses notifications without asking (drift E29)", incognitoNotify == "denied" && incognitoAsked.Count == 0,
        $"{incognitoNotify} {Seen(incognitoAsked)}");
    incognitoWin.Destroy();

    // Chromium refuses clipboard reads in an unfocused document before any permission step (a locked screen, say).
    var clipAsked = new List<(WebContents Page, string Permission, PermissionRequestDetails Details)>();
    var clipAllow = false;
    var clipping = Session.FromPartition("perm-clipboard");
    clipping.SetPermissionRequestHandler(Recorder(clipAsked, () => clipAllow));
    var clipWin = await Open(clipping, show: true);
    clipWin.Focus();
    if (await Run(clipWin, "document.hasFocus()") == "true")
    {
        const string ReadClipboard = "navigator.clipboard.readText().then(() => 'read', e => e.name)";
        var clipDenied = await Run(clipWin, ReadClipboard, gesture: true);
        clipAllow = true;
        var clipGranted = await Run(clipWin, ReadClipboard, gesture: true);
        Check("navigator.clipboard.readText asks the handler for clipboard-read: refused is NotAllowedError, granted reads", clipDenied == "NotAllowedError" && clipGranted == "read"
            && clipAsked.Count == 2 && clipAsked.All(c => c.Permission == "clipboard-read" && c.Details.RequestingUrl == Site), $"{clipDenied} {clipGranted} {Seen(clipAsked)}");
    }
    else Console.WriteLine("SKIP  navigator.clipboard.readText: the document has no focus");
    clipWin.Destroy();

    // Screen capture: the request handler's media first, then the display media handler, as Electron. No fake devices here.
    var mediaAsked = new List<(WebContents Page, string Permission, PermissionRequestDetails Details)>();
    var mediaAllow = true;
    var media = Session.FromPartition("perm-media");
    media.SetPermissionRequestHandler(Recorder(mediaAsked, () => mediaAllow));
    var mediaWin = await Open(media);    const string Screen = "navigator.mediaDevices.getDisplayMedia({ video: true, audio: true }).then(s => { const kinds = s.getTracks().map(t => t.kind).sort().join(','); s.getTracks().forEach(t => t.stop()); return kinds; }, e => e.name)";
    lock (mediaAsked) mediaAsked.Clear();
    var unhandled = await Run(mediaWin, Screen, gesture: true);
    Check("getDisplayMedia asks the request handler for media, without mediaTypes, then is refused with no display media handler", unhandled == "NotAllowedError"
        && mediaAsked is [(_, "media", { MediaTypes: [], SecurityOrigin: Origin, IsMainFrame: true })], $"{unhandled} {Seen(mediaAsked)}");
    var displayRequests = new List<DisplayMediaRequest>();
    DisplayMediaStreams? streams = new() { Video = new DisplayMediaSource("screen:0:0", "Entire screen") };
    media.SetDisplayMediaRequestHandler(request =>
    {
        lock (displayRequests) displayRequests.Add(request);
        return Task.FromResult<DisplayMediaStreams?>(streams);
    });
    // Chromium's X11 desktop capturer dies with SIGILL under the Docker Xvfb on arm64 only; x64 captures there.
    if (Environment.GetEnvironmentVariable("ORIEL_XVFB") == "1"
        && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        Console.WriteLine("SKIP  display media capture checks: Chromium's desktop capturer crashes under arm64 Xvfb");
    // Without Screen Recording consent, or while the screen is locked, macOS never starts the capture and it hangs.
    else if (OperatingSystem.IsMacOS() && (!MacMediaConsent.ScreenCapture() || MacMediaConsent.ScreenLocked()))
        Console.WriteLine($"SKIP  display media capture checks: macOS screen capture consent={MacMediaConsent.ScreenCapture()} locked={MacMediaConsent.ScreenLocked()}");
    else
    {
        var videoOnly = await Run(mediaWin, Screen, gesture: true);
        Check("a display media handler gets the frame, origin and what was requested; its video is captured, and audio left out is not",
            videoOnly == "video" && displayRequests is [{ VideoRequested: true, AudioRequested: true, SecurityOrigin: Origin, UserGesture: false, Frame: { } frame }] && frame.Url == Site,
            $"{videoOnly} {string.Join("; ", displayRequests)}");
        // macOS system audio needs ScreenCaptureKit's own consent, which a test app never gets answered: the capture hangs.
        if (OperatingSystem.IsMacOS())
            Console.WriteLine("SKIP  display media loopback audio: macOS system-audio capture waits on consent");
        else
        {
            streams = new() { Video = new DisplayMediaSource("screen:0:0", "Entire screen"), Audio = "loopback" };
            var withAudio = await Run(mediaWin, Screen, gesture: true);
            // Electron documents loopback for Windows and macOS; on Linux Oriel leaves the audio out (drift E30).
            var loopbackKinds = OperatingSystem.IsLinux() ? "video" : "audio,video";
            Check($"a display media handler's loopback audio gives {loopbackKinds}", withAudio == loopbackKinds, withAudio);
        }

        // A desktopCapturer id as chromeMediaSourceId: CEF parses it as a DesktopMediaID and captures that source. Its device
        // carries no DisplayMediaInformation, so the track has no displaySurface (drift E30).
        // The window source is one of the run's own: the first listed window can be any app's, cloaked or uncapturable.
        const string CaptureTitle = "Oriel capture target";
        var target = new BrowserWindow(new BrowserWindowOptions { Title = CaptureTitle, Width = 320, Height = 240, BackgroundColor = "#20C040" });
        await target.WebContents.LoadUrl("data:text/html,<title>" + Uri.EscapeDataString(CaptureTitle) + "</title><body style='background:%2320C040'>");
        var sources = await DesktopCapturer.GetSourcesAsync(new SourcesOptions { Types = ["screen", "window"], ThumbnailSize = new System.Drawing.Size(0, 0) });
        foreach (var source in new[] { sources.FirstOrDefault(s => s.Id.StartsWith("screen:", StringComparison.Ordinal)), sources.FirstOrDefault(s => s.Name == CaptureTitle) })
        {
            if (source is null) continue;
            streams = new() { Video = source };
            var legacy = await Run(mediaWin, "navigator.mediaDevices.getUserMedia({ video: { mandatory: { chromeMediaSource: 'desktop', chromeMediaSourceId: "
                + System.Text.Json.JsonSerializer.Serialize(source.Id) + " } } }).then(s => new Promise(resolve => { const v = s.getVideoTracks()[0]; const settings = v.getSettings();"
                + " const el = document.createElement('video'); el.muted = true; el.srcObject = s; let done = false;"
                + " const finish = () => { if (done) return; done = true; s.getTracks().forEach(t => t.stop()); resolve(`${settings.deviceId} ${settings.displaySurface ?? 'none'} ${el.videoWidth}x${el.videoHeight}`); };"
                + " el.onloadeddata = finish; setTimeout(finish, 4000); el.play().catch(() => {}); }), e => e.name)", gesture: true);
            var kind = source.Id.StartsWith("screen:", StringComparison.Ordinal) ? "screen" : "window";
            var parts = legacy?.Split(' ') ?? [];
            Check($"getUserMedia with a desktopCapturer {kind} id as chromeMediaSourceId captures that source, with no displaySurface (drift E30)",
                parts is [var deviceId, "none", var size] && deviceId == source.Id && size != "0x0", $"{source.Id}: {legacy}");
        }
        Check("desktopCapturer lists the run's own capture target window", sources.Any(s => s.Name == CaptureTitle), string.Join(" | ", sources.Select(s => s.Name)));
        target.Destroy();
    }
    streams = null;
    var refusedScreen = await Run(mediaWin, Screen, gesture: true);
    streams = new() { Audio = "loopback" };
    var audioOnly = await Run(mediaWin, Screen, gesture: true);
    Check("a display media handler's null, or no video for a video request, refuses the capture", refusedScreen == "NotAllowedError" && audioOnly == "NotAllowedError",
        $"{refusedScreen} {audioOnly}");
    lock (displayRequests) displayRequests.Clear();
    mediaAllow = false;
    var mediaRefused = await Run(mediaWin, Screen, gesture: true);
    Check("a request handler that refuses media refuses the capture before the display media handler", mediaRefused == "NotAllowedError" && displayRequests.Count == 0,
        $"{mediaRefused} {displayRequests.Count}");
    mediaWin.Destroy();

    // Checks: navigator.permissions.query answers from the check handler, which does not decide requests.
    var checks = new List<(WebContents? Page, string Permission, string Origin, PermissionCheckDetails Details)>();
    var checking = Session.FromPartition("perm-check");
    checking.SetPermissionCheckHandler((page, permission, origin, details) =>
    {
        lock (checks) checks.Add((page, permission, origin, details));
        return permission == "notifications";
    });
    var checkWin = await Open(checking);
    string Checked()
    {
        lock (checks) return string.Join("; ", checks.Select(c => $"{c.Permission} {c.Origin} {c.Details}"));
    }
    var geolocationState = await Run(checkWin, Query("{ name: 'geolocation' }"));
    Check("navigator.permissions.query asks the check handler with the page, the name, the origin, the frame's URL, isMainFrame and embeddingOrigin",
        geolocationState == "denied" && checks is [(var checker, "geolocation", Origin, { RequestingUrl: Site, IsMainFrame: true, EmbeddingOrigin: Origin, MediaType: null })]
        && ReferenceEquals(checker, checkWin.WebContents), $"{geolocationState} {Checked()}");
    lock (checks) checks.Clear();
    var notificationsState = await Run(checkWin, Query("{ name: 'notifications' }"));
    Check("a check handler's true is granted", notificationsState == "granted" && checks is [(_, "notifications", _, _)], $"{notificationsState} {Checked()}");
    lock (checks) checks.Clear();
    var cameraState = await Run(checkWin, Query("{ name: 'camera' }"));
    var microphoneState = await Run(checkWin, Query("{ name: 'microphone' }"));
    var sysexState = await Run(checkWin, Query("{ name: 'midi', sysex: true }"));
    Check("camera and microphone checks are media with mediaType video and audio; midi with sysex is midiSysex", cameraState == "denied" && microphoneState == "denied"
        && sysexState == "denied" && checks is [(_, "media", _, { MediaType: "video" }), (_, "media", _, { MediaType: "audio" }), (_, "midiSysex", _, _)],
        $"{cameraState} {microphoneState} {sysexState} {Checked()}");
    lock (checks) checks.Clear();
    var badQuery = await Run(checkWin, Query("{ name: 'teleportation' }"));
    Check("a descriptor Chromium rejects still rejects, without a check", badQuery == "TypeError" && checks.Count == 0, $"{badQuery} {Checked()}");
    var stillGranted = await Run(checkWin, Geolocation);
    Check("the check handler does not decide requests, as Electron's", stillGranted is "granted" or "ok", stillGranted);
    await Run(checkWin, "new Promise(r => { const f = document.createElement('iframe'); f.src = '/frame'; f.onload = () => r('loaded'); document.body.append(f); })");
    lock (checks) checks.Clear();
    var frameState = await Run(checkWin, "document.querySelector('iframe').contentWindow.navigator.permissions.query({ name: 'geolocation' }).then(s => s.state)");
    Check("an iframe's check names that frame: its URL, not the main frame", frameState == "denied"
        && checks is [(_, "geolocation", Origin, { RequestingUrl: Site + "frame", IsMainFrame: false, EmbeddingOrigin: Origin })], $"{frameState} {Checked()}");
    checking.SetPermissionCheckHandler(null);
    var cleared = await Run(checkWin, Query("{ name: 'geolocation' }"));
    Check("SetPermissionCheckHandler(null) goes back to granting every check", cleared == "granted", cleared);
    checkWin.Destroy();
}

// select-client-certificate with the Windows certificate store: two throwaway client certificates in CurrentUser\My
// only, removed with their keys however the checks end. The user's own certificates may be listed too.
async Task WindowsClientCertificateChecks(System.Security.Cryptography.X509Certificates.X509Certificate2 serverCert)
{
    var tag = Guid.NewGuid().ToString("N");
    // Chromium's ClientCertIdentitySorter puts the later expiry first, so A should lead the list and B follow.
    var certA = AddWindowsClientCertificate($"Oriel E25 Test A {tag}", DateTimeOffset.UtcNow.AddYears(20));
    var certB = AddWindowsClientCertificate($"Oriel E25 Test B {tag}", DateTimeOffset.UtcNow.AddYears(19));
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { RemoveWindowsClientCertificate(certA); RemoveWindowsClientCertificate(certB); };
    Console.WriteLine($"E25 added {certA.Thumbprint} and {certB.Thumbprint}");
    try
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = ServeTls(listener, serverCert, requireClient: true, received.Enqueue);
        static string Fingerprint(System.Security.Cryptography.X509Certificates.X509Certificate2 c) => "sha256/" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(c.RawData));
        static string Thumbprint(Certificate c) => System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(Convert.FromBase64String(string.Concat(
            c.Data.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal))))).Thumbprint;
        async Task<(string? Body, string? Sent, List<string> Order, SelectClientCertificateEventArgs? Args)> Visit(Action<SelectClientCertificateEventArgs>? decide)
        {
            received.Clear();
            var order = new List<string>();
            SelectClientCertificateEventArgs? seenArgs = null;
            var w = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "win-mutual-" + Guid.NewGuid() } });
            // Trusts the self-signed server certificate for this server only.
            w.WebContents.CertificateError += (_, e) => { e.Cancel = true; e.Callback(e.Url.StartsWith($"https://127.0.0.1:{port}/", StringComparison.Ordinal)); };
            w.WebContents.SelectClientCertificate += (_, e) => { order.Add("page"); seenArgs = e; decide?.Invoke(e); };
            EventHandler<SelectClientCertificateEventArgs> onApp = (sender, e) => order.Add(ReferenceEquals(sender, app) && ReferenceEquals(e, seenArgs) ? "app" : "app?");
            app.SelectClientCertificate += onApp;
            var failed = await Throws(() => w.LoadUrl($"https://127.0.0.1:{port}/").WaitAsync(TimeSpan.FromSeconds(20)));
            var body = failed is null ? (await w.WebContents.ExecuteJavaScriptAsync("document.body.innerText"))?.ToString() : failed.Message;
            app.SelectClientCertificate -= onApp;
            w.Destroy();
            return (body, received.LastOrDefault(), order, seenArgs);
        }

        var first = await Visit(null);
        var list = first.Args?.CertificateList ?? [];
        var a = list.FirstOrDefault(c => c.Fingerprint == Fingerprint(certA));
        var b = list.FirstOrDefault(c => c.Fingerprint == Fingerprint(certB));
        Check("Windows: select-client-certificate lists the store's certificates, with host:port, page then app", first.Args?.Url == $"127.0.0.1:{port}"
            && a is not null && b is not null && first.Order.SequenceEqual(["page", "app"]),
            $"{first.Args?.Url} [{string.Join(",", first.Order)}] {string.Join(" | ", list.Select(c => c.SubjectName))}");
        Check("Windows: a listed certificate carries Electron's fields", a is { IssuerName: var issuer, SubjectName: var subject, Data: var pem, IssuerCert: null }
            && subject == $"Oriel E25 Test A {tag}" && issuer == subject && a.SerialNumber == certA.SerialNumber && pem.StartsWith("-----BEGIN CERTIFICATE-----\n", StringComparison.Ordinal)
            && Thumbprint(a) == certA.Thumbprint && a.ValidExpiry == new DateTimeOffset(certA.NotAfter.ToUniversalTime()).ToUnixTimeSeconds(),
            $"{a?.SubjectName} {a?.IssuerName} {a?.SerialNumber}");
        Check("Windows: the later-expiring certificate leads, as Chromium sorts them", list.Count > 1 && ReferenceEquals(list[0], a), list.FirstOrDefault()?.SubjectName);
        Check("Windows: with no choice, the server gets the first listed certificate, as Electron's", list.Count > 0 && first.Sent == Thumbprint(list[0])
            && first.Body == $"tls ok client={list[0].Subject.CommonName}", $"{first.Sent} {first.Body}");

        var chosen = await Visit(e => { e.Cancel = true; e.Callback(e.CertificateList.First(c => c.Fingerprint == Fingerprint(certB))); });
        Check("Windows: preventDefault and callback(B) send B, not the first", chosen.Sent == certB.Thumbprint && chosen.Body == $"tls ok client=Oriel E25 Test B {tag}",
            $"{chosen.Sent} {chosen.Body}");

        var none = await Visit(e => { e.Cancel = true; _ = Task.Delay(200).ContinueWith(_ => e.Callback()); });
        Check("Windows: callback() with none sends no certificate", none.Sent == "none" && none.Body == "tls ok client=none", $"{none.Sent} {none.Body}");
        listener.Stop();
    }
    finally
    {
        var goneA = RemoveWindowsClientCertificate(certA);
        var goneB = RemoveWindowsClientCertificate(certB);
        Console.WriteLine($"E25 removed {certA.Thumbprint}={goneA} {certB.Thumbprint}={goneB}");
        Check("Windows: the test client certificates are removed from CurrentUser\\My with their keys", goneA && goneB);
    }
}

// A self-signed client-auth certificate whose key persists, added to CurrentUser\My and nowhere else.
static System.Security.Cryptography.X509Certificates.X509Certificate2 AddWindowsClientCertificate(string subject, DateTimeOffset notAfter)
{
    using var key = System.Security.Cryptography.RSA.Create(2048);
    var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={subject}", key,
        System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.2")], false));
    using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter);
    var persisted = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
        made.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, "pw"), "pw",
        System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.UserKeySet | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.PersistKeySet);
    using var store = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My,
        System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
    store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
    store.Add(persisted);
    return persisted;
}

// Removes the certificate from CurrentUser\My and deletes its persisted key; true when it is no longer in the store.
static bool RemoveWindowsClientCertificate(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
{
    try
    {
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
        foreach (var found in store.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, certificate.Thumbprint, false))
            store.Remove(found);
        if (certificate.HasPrivateKey)
        {
            try
            {
                switch (System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(certificate))
                {
                    case System.Security.Cryptography.RSACng cng: cng.Key.Delete(); break;
                    case System.Security.Cryptography.RSACryptoServiceProvider csp: csp.PersistKeyInCsp = false; csp.Clear(); break;
                }
            }
            // Already deleted by an earlier call.
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        store.Close();
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
        return store.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"E25 removal of {certificate.Thumbprint} failed: {e}");
        return false;
    }
}

// Two self-signed client certificates, imported into a fresh NSS store in the home directory.
static bool InstallClientCertificates()
{
    var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");
    if (Directory.Exists(db)) Directory.Delete(db, recursive: true);
    Directory.CreateDirectory(db);
    bool Run(string tool, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }
    if (!Run("/usr/bin/certutil", $"-N -d sql:{db} --empty-password")) return false;
    foreach (var name in new[] { "oriel-client-1", "oriel-client-2" })
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={name}", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.2")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pfx = Path.Combine(db, name + ".p12");
        File.WriteAllBytes(pfx, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, "pw"));
        if (!Run("/usr/bin/pk12util", $"-i {pfx} -d sql:{db} -W pw")) return false;
    }
    return true;
}

// Answers one proxied request and reports its request line.
static async Task<string> ServeOneProxyRequest(TcpListener listener) => await AnswerRequest(await listener.AcceptTcpClientAsync());

static async Task<string> AnswerRequest(TcpClient connection)
{
    using var client = connection;
    var stream = client.GetStream();
    var reader = new StreamReader(stream, Encoding.ASCII);
    var first = await reader.ReadLineAsync() ?? "";
    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
    var body = Encoding.ASCII.GetBytes(Page);
    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(head);
    await stream.WriteAsync(body);
    return first;
}

// WebGL2, WebGPU and video decode on a real GPU; skipped where Chromium renders in software (llvmpipe, SwiftShader,
// Microsoft's Basic Render Driver).
async Task GpuHardwareChecks()
{
    System.Text.Json.Nodes.JsonObject info;
    // Patched Basic is collected in the browser process, which marks no device active; only Complete knows it.
    try { info = await app.GetGpuInfoAsync(Oriel.Cef.CefRuntime.IsPatched ? GpuInfoType.Complete : GpuInfoType.Basic); }
    catch (InvalidOperationException e) when (Oriel.Cef.CefRuntime.IsPatched)
    {
        Console.WriteLine($"SKIP  GPU hardware checks: Chromium refused GPU access [{e.Message}]");
        return;
    }
    var devices = (info["gpuDevice"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>().ToList();
    static int VendorOf(System.Text.Json.Nodes.JsonObject d) => d["vendorId"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var id) ? id : 0;
    static string VendorName(int id) => id switch { 0x10DE => "nvidia", 0x8086 => "intel", 0x1002 => "amd", 0x13B5 => "arm", 0x5143 => "qualcomm", 0x106B => "apple", _ => "" };
    var active = devices.FirstOrDefault(d => d["active"]?.GetValue<bool>() == true);
    var activeString = active?["deviceString"]?.ToString() ?? "";
    if (OperatingSystem.IsWindows())
    {
        // Windows names each display adapter's PCI vendor, so the GPU rows can be checked against the machine itself.
        var pci = WindowsDisplayAdapters.PciVendorIds();
        var listed = devices.Select(VendorOf).ToHashSet();
        Check("Windows: GetGpuInfoAsync lists every PCI display adapter by vendor id, and one of them is active",
            pci.Count > 0 && pci.All(listed.Contains) && active is not null && pci.Contains(VendorOf(active)),
            $"pci {string.Join(",", pci.Select(v => $"0x{v:X4}"))} vs listed {string.Join(",", listed.Select(v => $"0x{v:X4}"))}, active {activeString}");
    }
    if (active is null || VendorName(VendorOf(active)) == "" || activeString.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || activeString.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"SKIP  GPU hardware checks: Chromium's active GPU is software or unknown [{activeString}]");
        return;
    }
    var activeVendor = VendorName(VendorOf(active));
    var page = Path.Combine(Path.GetTempPath(), $"oriel-gpu-{Environment.ProcessId}.html");
    File.WriteAllText(page, "<!doctype html><title>gpu</title>");
    var w = new BrowserWindow(new BrowserWindowOptions { Show = false });
    try
    {
        // file: is a secure context, which navigator.gpu needs.
        await w.WebContents.LoadFile(page);
        var renderer = (await w.WebContents.ExecuteJavaScriptAsync(
            "(() => { const gl = document.createElement('canvas').getContext('webgl2'); if (!gl) return 'none';" +
            " const ext = gl.getExtension('WEBGL_debug_renderer_info'); return ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : 'no debug info'; })()"))?.ToString() ?? "";
        Check("a WebGL2 context renders on Chromium's active GPU, not in software", renderer.Contains(activeVendor, StringComparison.OrdinalIgnoreCase)
            && !renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase), $"{renderer} vs active {activeVendor} {activeString}");
        async Task<string> Adapter(string power) => (await w.WebContents.ExecuteJavaScriptAsync(
            $"(async () => {{ if (!navigator.gpu) return 'no navigator.gpu'; const a = await navigator.gpu.requestAdapter({{ powerPreference: '{power}' }});" +
            " if (!a) return 'no adapter'; const i = a.info || await a.requestAdapterInfo(); return `${i.vendor}|${i.architecture}|${i.description}|fallback=${!!(i.isFallbackAdapter ?? a.isFallbackAdapter)}`; })()"))?.ToString() ?? "";
        var lowPower = await Adapter("low-power");
        Check("WebGPU gives a hardware adapter from one of the machine's GPUs", !lowPower.Contains("fallback=true", StringComparison.Ordinal)
            && devices.Select(d => VendorName(VendorOf(d))).Where(v => v.Length > 0).Any(v => lowPower.StartsWith(v + "|", StringComparison.OrdinalIgnoreCase)), lowPower);
        Console.WriteLine($"INFO  WebGPU high-performance adapter: {await Adapter("high-performance")}");
        foreach (var (codec, type) in new[] { ("VP9", "video/webm; codecs=\"vp09.00.40.08\""), ("AV1", "video/webm; codecs=\"av01.0.08M.08\""), ("H.264", "video/mp4; codecs=\"avc1.640028\"") })
        {
            var decode = (await w.WebContents.ExecuteJavaScriptAsync(
                $"navigator.mediaCapabilities.decodingInfo({{ type: 'media-source', video: {{ contentType: '{type.Replace("\"", "\\\"")}', width: 3840, height: 2160, bitrate: 20000000, framerate: 30 }} }})" +
                ".then(r => `supported=${r.supported} smooth=${r.smooth} powerEfficient=${r.powerEfficient}`)"))?.ToString();
            Console.WriteLine($"INFO  decodingInfo {codec} 2160p30: {decode}");
        }
    }
    finally
    {
        w.Destroy();
        File.Delete(page);
    }
}

// Session members added by the docs/parity/session.md audit; written for a macOS, Windows and Linux run.
async Task SessionAuditChecks()
{
    var persistent = Session.FromPartition("persist:audit");
    var memory = Session.FromPartition("audit-memory");
    Check("StoragePath is GetStoragePath, null in memory", persistent.StoragePath == persistent.GetStoragePath() && persistent.StoragePath is not null
        && memory.StoragePath is null, persistent.StoragePath);
    persistent.SpellCheckerEnabled = false;
    var off = persistent.SpellCheckerEnabled == false && !persistent.IsSpellCheckerEnabled();
    persistent.SpellCheckerEnabled = true;
    Check("SpellCheckerEnabled reads and writes IsSpellCheckerEnabled / SetSpellCheckerEnabled", off && persistent.IsSpellCheckerEnabled());

    // Throttling is the network service's, so the server must be a real socket, not a protocol handler.
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
    var serving = Task.Run(async () =>
    {
        // Chromium may open a socket it never sends on (a preconnect), so each connection is answered on its own.
        try { while (true) { var client = await listener.AcceptTcpClientAsync(); _ = AnswerRequest(client).ContinueWith(_ => { }); } }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException) { }
    });
    memory.EnableNetworkEmulation(new NetworkEmulationOptions { Offline = true });
    var emulatedWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = memory } });
    var offline = await Throws(() => emulatedWin.LoadUrl(url).WaitAsync(TimeSpan.FromSeconds(10)));
    Check("EnableNetworkEmulation(offline) reaches a page made after it: loads fail with ERR_INTERNET_DISCONNECTED",
        offline is LoadFailedException { ErrorCode: -106 }, offline?.Message);
    var otherWin = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = Session.FromPartition("audit-other") } });
    var other = await Throws(() => otherWin.LoadUrl(url).WaitAsync(TimeSpan.FromSeconds(10)));
    Check("another session is not emulated", other is null, $"{other?.GetType().Name} {other?.Message}");
    otherWin.Destroy();
    memory.DisableNetworkEmulation();
    var again = await Throws(() => emulatedWin.LoadUrl(url).WaitAsync(TimeSpan.FromSeconds(10)));
    Check("DisableNetworkEmulation lets the open page load again", again is null, $"{again?.GetType().Name} {again?.Message}");
    memory.EnableNetworkEmulation(new NetworkEmulationOptions { Latency = 1500 });
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var slow = await Throws(() => emulatedWin.LoadUrl(url + "slow").WaitAsync(TimeSpan.FromSeconds(15)));
    var slowMs = timer.ElapsedMilliseconds;
    memory.DisableNetworkEmulation();
    timer.Restart();
    var fast = await Throws(() => emulatedWin.LoadUrl(url + "fast").WaitAsync(TimeSpan.FromSeconds(10)));
    Check("EnableNetworkEmulation(latency) slows an open page's load, and disabling restores it",
        slow is null && fast is null && slowMs >= 1400 && timer.ElapsedMilliseconds < 1400, $"{slowMs} ms, then {timer.ElapsedMilliseconds} ms");
    emulatedWin.Destroy();
    listener.Stop();
    await serving.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });

    var badVersion = await Throws(() => { persistent.SetSslConfig(new SslConfig { MinVersion = "tls1.1" }); return Task.CompletedTask; });
    var setSsl = await Throws(() =>
    {
        persistent.SetSslConfig(new SslConfig { MinVersion = "tls1.3", DisabledCipherSuites = [0x0004, 0xC002] });
        persistent.SetSslConfig(new SslConfig());
        return Task.CompletedTask;
    });
    Check("SetSslConfig refuses tls1.1 and Chromium takes the TLS version and cipher suite preferences", badVersion is ArgumentException && setSsl is null,
        $"{badVersion?.Message} | {setSsl?.Message}");

    // A server that speaks only TLS 1.2 shows the minimum taking effect in the handshake, not just in the preference.
    using var tls12Key = System.Security.Cryptography.RSA.Create(2048);
    using var tls12SelfSigned = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=oriel-tls12.test", tls12Key,
        System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    using var tls12Cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
        tls12SelfSigned.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12), null);
    using var tls12Listener = new TcpListener(IPAddress.Loopback, 0);
    tls12Listener.Start();
    var tls12Url = $"https://127.0.0.1:{((IPEndPoint)tls12Listener.LocalEndpoint).Port}/";
    _ = ServeTls(tls12Listener, tls12Cert, protocols: System.Security.Authentication.SslProtocols.Tls12);
    var tlsSession = Session.FromPartition("audit-tls12");
    async Task<Exception?> VisitTls12()
    {
        var w = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = tlsSession } });
        w.WebContents.CertificateError += (_, e) => { e.Cancel = true; e.Callback(e.Url.StartsWith(tls12Url, StringComparison.Ordinal)); };
        var failed = await Throws(() => w.LoadUrl(tls12Url + Guid.NewGuid().ToString("N")).WaitAsync(TimeSpan.FromSeconds(15)));
        w.Destroy();
        return failed;
    }
    var tls12Default = await VisitTls12();
    tlsSession.SetSslConfig(new SslConfig { MinVersion = "tls1.3" });
    // The network service hears the change after a hop.
    await Task.Delay(500);
    var tls12Refused = await VisitTls12();
    tlsSession.SetSslConfig(new SslConfig());
    await Task.Delay(500);
    var tls12Again = await VisitTls12();
    tls12Listener.Stop();
    Check("SetSslConfig(MinVersion tls1.3) refuses a TLS 1.2-only server that loads by default, and resetting it loads again",
        tls12Default is null && tls12Refused is LoadFailedException { ErrorCode: -113 or -107 } && tls12Again is null,
        $"{tls12Default?.Message ?? "loaded"} | {tls12Refused?.Message ?? "loaded"} | {tls12Again?.Message ?? "loaded"}");
    var ntlm = await Throws(() =>
    {
        persistent.AllowNtlmCredentialsForDomains("*.oriel.test,intranet");
        persistent.AllowNtlmCredentialsForDomains("");
        return Task.CompletedTask;
    });
    Check("AllowNtlmCredentialsForDomains: Chromium takes the auth.server_allowlist preference", ntlm is null, ntlm?.Message);

    // SSPI answers NTLM with the signed-in user's own credentials, so the allow list decides who gets them unasked.
    if (OperatingSystem.IsWindows())
    {
        using var ntlmListener = new TcpListener(IPAddress.Loopback, 0);
        ntlmListener.Start();
        var ntlmUrl = $"http://127.0.0.1:{((IPEndPoint)ntlmListener.LocalEndpoint).Port}/";
        _ = ServeNtlm(ntlmListener);
        var ntlmSession = Session.FromPartition("audit-ntlm");
        async Task<(string? Body, int Logins)> VisitNtlm()
        {
            var logins = 0;
            var w = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = ntlmSession } });
            w.WebContents.Login += (_, _) => Interlocked.Increment(ref logins);
            var failed = await Throws(() => w.LoadUrl(ntlmUrl + Guid.NewGuid().ToString("N")).WaitAsync(TimeSpan.FromSeconds(15)));
            var body = failed is null ? (await w.WebContents.ExecuteJavaScriptAsync("document.body.innerText"))?.ToString() : failed.Message;
            w.Destroy();
            return (body, logins);
        }
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        ntlmSession.AllowNtlmCredentialsForDomains("*.oriel.test");
        await Task.Delay(500);
        var unlisted = await VisitNtlm();
        ntlmSession.AllowNtlmCredentialsForDomains("127.0.0.1");
        await Task.Delay(500);
        var listed = await VisitNtlm();
        ntlmSession.AllowNtlmCredentialsForDomains("");
        ntlmListener.Stop();
        Check("Windows: AllowNtlmCredentialsForDomains sends a listed server the user's NTLM credentials, with no login event",
            listed.Body == $"ntlm {me}" && listed.Logins == 0, $"{listed.Body} logins={listed.Logins} vs {me}");
        Check("Windows: a server off the list gets no NTLM credentials unasked: login is raised instead",
            unlisted.Body != $"ntlm {me}" && unlisted.Logins > 0, $"{unlisted.Body} logins={unlisted.Logins}");
    }
}

// app members added by the docs/parity/app.md audit; written for a macOS, Windows and Linux run.
// Electron's webPreferences.sandbox: false, which Oriel decides per session (drift W13, patch 0010).
async Task SandboxSessionChecks()
{
    var mismatch = await Throws(() => { Session.FromPartition("", new SessionOptions { Sandbox = false }); return Task.CompletedTask; });
    Check("an unsandboxed default session is refused", mismatch is ArgumentException, mismatch?.Message);
    if (app.IsSandboxed && !Oriel.Cef.CefRuntime.IsPatched)
    {
        var stock = await Throws(() => { Session.FromPartition("unsandboxed-stock", new SessionOptions { Sandbox = false }); return Task.CompletedTask; });
        Check("stock CEF: a sandboxed app refuses an unsandboxed session", stock is PlatformNotSupportedException, stock?.Message);
        return;
    }

    var open = Session.FromPartition("unsandboxed", new SessionOptions { Sandbox = false });
    var shut = Session.FromPartition("sandboxed-split");
    var other = await Throws(() => { Session.FromPartition("unsandboxed", new SessionOptions { Sandbox = true }); return Task.CompletedTask; });
    Check("asking an existing session for another Sandbox throws, and no options take it as it is",
        other is InvalidOperationException && Session.FromPartition("unsandboxed") == open && !open.Sandbox && shut.Sandbox, other?.Message);
    var onShut = await Throws(() => { new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = shut, Sandbox = false } }).Destroy(); return Task.CompletedTask; });
    var onOpen = await Throws(() => { new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Partition = "unsandboxed", Sandbox = true } }).Destroy(); return Task.CompletedTask; });
    Check("a window's Sandbox that contradicts its session throws", onShut is ArgumentException && onOpen is ArgumentException, $"{onShut?.Message} | {onOpen?.Message}");
    if (!app.IsSandboxed)
    {
        Console.WriteLine("SKIP  per-session sandbox split: the app's children are not sandboxed, so every renderer already runs without it");
        return;
    }

    var page = Path.Combine(Path.GetTempPath(), "oriel-sandbox-split.html");
    File.WriteAllText(page, "<!doctype html><p>split</p>");
    var disk = Session.FromPartition("persist:unsandboxed-disk", new SessionOptions { Sandbox = false });
    // Interleaved, so a spare renderer warmed for one session is offered to the other's next window.
    var windows = new List<(string Name, bool Open, BrowserWindow Window)>();
    foreach (var (name, session, isOpen) in new[] { ("A1", open, true), ("B1", shut, false), ("A2", open, true), ("B2", shut, false), ("C1", disk, true) })
    {
        var window = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = session, Sandbox = isOpen ? false : null } });
        await window.WebContents.LoadFile(page);
        windows.Add((name, isOpen, window));
    }
    var pids = windows.Select(w => (w.Name, w.Open, Pid: w.Window.WebContents.GetOSProcessId())).ToList();
    var openPids = pids.Where(p => p.Open).Select(p => p.Pid).ToHashSet();
    Check("the sessions' renderers are separate processes, the same site in both",
        pids.All(p => p.Pid > 0) && pids.Where(p => !p.Open).All(p => !openPids.Contains(p.Pid)), string.Join(",", pids.Select(p => $"{p.Name}:{p.Pid}")));
#pragma warning disable CA1416
    if (OperatingSystem.IsWindows())
    {
        var metrics = app.GetAppMetrics();
        var seen = pids.Select(p => (p.Name, p.Open, p.Pid, Token: TokenProbe.IntegrityName(p.Pid), Metric: metrics.FirstOrDefault(m => m.Pid == p.Pid))).ToList();
        var detail = string.Join(",", seen.Select(s => $"{s.Name}:{s.Pid}:{s.Token}/{s.Metric?.IntegrityLevel}/{s.Metric?.Sandboxed}"));
        Check("patched (0010): at once, the unsandboxed sessions' renderers run at medium integrity and the sandboxed one's at untrusted, as getAppMetrics says",
            seen.All(s => s.Metric is { Type: "Tab" } m && m.IntegrityLevel == s.Token
                && (s.Open ? s.Token == "medium" && m.Sandboxed == false : s.Token == "untrusted" && m.Sandboxed == true)), detail);
    }
#pragma warning restore CA1416
    if (OperatingSystem.IsLinux())
    {
        // A sandboxed renderer adds Chromium's seccomp-bpf filter to whatever the browser runs under; a container's
        // runtime puts every process under a filter of its own, so mode 2 alone proves nothing.
        string Status(int pid, string key) => File.ReadLines($"/proc/{pid}/status").FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal))?.Split('\t')[^1] ?? "?";
        string Args(int pid) => File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
        var browserFilters = int.Parse(Status(Environment.ProcessId, "Seccomp_filters"), CultureInfo.InvariantCulture);
        var browserPid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var seen = pids.Select(p => (p.Name, p.Open, p.Pid, Filters: int.Parse(Status(p.Pid, "Seccomp_filters"), CultureInfo.InvariantCulture),
            NoSandbox: Args(p.Pid).Contains("--no-sandbox"), Parent: Status(p.Pid, "PPid"))).ToList();
        // Patch 0011: only a renderer launched outside the zygote, by the browser itself, escapes the zygote's sandbox.
        Check("patched (0010, 0011): at once, the unsandboxed sessions' renderers are the browser's children with --no-sandbox and no seccomp filter beyond the browser's, the sandboxed one's are the zygote's under one more",
            seen.All(s => s.Open ? s.Filters == browserFilters && s.NoSandbox && s.Parent == browserPid : s.Filters > browserFilters && !s.NoSandbox && s.Parent != browserPid),
            $"browser {browserPid} filters={browserFilters}: " + string.Join(",", seen.Select(s => $"{s.Name}:{s.Pid}:filters={s.Filters}/no-sandbox={s.NoSandbox}/parent={s.Parent}")));
    }
    foreach (var (_, _, window) in windows) window.Destroy();
}

async Task AppAuditChecks()
{
    Check("WillFinishLaunching comes before Ready (macOS: from AppKit's launch), and WhenReady after the Ready handlers",
        await Task.WhenAny(whenReady, Task.Delay(5000)) == whenReady
        && string.Join(",", launchOrder) == "will-finish-launching,ready,whenReady",
        string.Join(",", launchOrder));
    Check("RequestSingleInstanceLock was granted with no other run, and HasSingleInstanceLock says so", gotSingleInstanceLock && app.HasSingleInstanceLock());
    app.ReleaseSingleInstanceLock();
    Check("ReleaseSingleInstanceLock drops it, and asking again while running grants it without notifying itself",
        !app.HasSingleInstanceLock() && app.RequestSingleInstanceLock() && app.HasSingleInstanceLock());

    // A second launch, from another working directory, hands this run its command line, directory and additional data.
    var seconds = new System.Collections.Concurrent.ConcurrentQueue<SecondInstanceEventArgs>();
    EventHandler<SecondInstanceEventArgs> onSecond = (_, e) => seconds.Enqueue(e);
    app.SecondInstance += onSecond;
    var secondDirectory = Directory.CreateTempSubdirectory("oriel-second-").FullName;
    try
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = secondDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(SecondInstanceChild);
        using var second = System.Diagnostics.Process.Start(start)!;
        string secondOutput;
        try { secondOutput = (await second.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(60))).Trim(); }
        catch (TimeoutException)
        {
            second.Kill(entireProcessTree: true);
            secondOutput = "timeout";
        }
        for (var waited = 0; waited < 100 && seconds.IsEmpty; waited++) await Task.Delay(100);
        var heard = seconds.TryPeek(out var arrived) ? arrived : null;
        // CEF's bootstrap.exe moves the browser process into its own folder (drift A38), so on Windows the launch's
        // directory is the app's; elsewhere it is the one the launch started in.
        var (handOver, secondCwd) = secondOutput.Split('|', 2) is [var verdict, var cwd] ? (verdict, cwd) : (secondOutput, "");
        static bool SamePath(string? a, string? b) => string.Equals(Path.TrimEndingDirectorySeparator(a ?? ""), Path.TrimEndingDirectorySeparator(b ?? ""),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        Check("a second launch's RequestSingleInstanceLock answers false, and SecondInstance gets its command line, working directory and additional data",
            handOver == "HANDED-OVER" && seconds.Count == 1 && heard is { } h && h.CommandLine.Contains(SecondInstanceChild, StringComparison.Ordinal)
            && !h.CommandLine.Contains("oriel-second-instance-data", StringComparison.Ordinal)
            && SamePath(h.WorkingDirectory, secondCwd) && (OperatingSystem.IsWindows() || SamePath(secondCwd, secondDirectory))
            && h.AdditionalData?["child"]?.GetValue<string>() == "ok ✓",
            $"{secondOutput} | {seconds.Count} | {heard?.CommandLine} | {heard?.WorkingDirectory} (started in {secondDirectory}) | {heard?.AdditionalData?.ToJsonString()}");
    }
    finally
    {
        app.SecondInstance -= onSecond;
        Directory.Delete(secondDirectory);
    }

    Check("GetPath(cache), (userCache) and (userDesktop) are Chromium's, userCache made under cache",
        app.GetPath("userCache") == Path.Combine(app.GetPath("cache"), app.Name) && Directory.Exists(app.GetPath("userCache"))
        && app.GetPath("userDesktop") == app.GetPath("desktop")
        && app.GetPath("cache") == (OperatingSystem.IsMacOS() ? Path.Combine(app.GetPath("home"), "Library", "Caches")
            : OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(app.GetPath("home"), ".cache")),
        app.GetPath("cache"));
    if (OperatingSystem.IsMacOS())
        Check("GetPath(assets) fails on macOS, as Electron's", (await Throws(() => Task.FromResult(app.GetPath("assets"))))?.Message.StartsWith("Failed to get 'assets' path") == true);
    var logsBefore = app.GetPath("logs");
    var newLogs = Path.Combine(Path.GetTempPath(), "oriel-logs-" + Guid.NewGuid().ToString("N"), "nested");
    app.SetAppLogsPath(newLogs);
    // PathService::Override resolves symlinks: macOS's /var is /private/var.
    Check("SetAppLogsPath makes the directory and GetPath(logs) answers it resolved", Directory.Exists(newLogs)
        && app.GetPath("logs").EndsWith(newLogs, StringComparison.Ordinal), app.GetPath("logs"));
    app.SetPath("logs", logsBefore);
    Directory.Delete(Path.GetDirectoryName(newLogs)!, recursive: true);
    Check("SetAppLogsPath refuses a relative path", (await Throws(() => { app.SetAppLogsPath("relative"); return Task.CompletedTask; }))?.Message.StartsWith("Path must be absolute") == true);

    Check("GetAppPath is the app's directory", OrielApp.GetAppPath() == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) && Directory.Exists(OrielApp.GetAppPath()));
    Check("IsPackaged is true for the app's own executable", OrielApp.IsPackaged == !Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("dotnet", StringComparison.OrdinalIgnoreCase));
    var name = app.Name;
    var userAgent = app.UserAgentFallback;
    // Read just before, so Chromium's PathService cache holds it.
    var userDataBefore = app.GetPath("userData");
    app.Name = "Renamed App";
    Check("Setting Name renames the default user agent and keeps a userData already read", app.UserAgentFallback.Contains(" RenamedApp/", StringComparison.Ordinal)
        && app.GetPath("userData") == userDataBefore, app.UserAgentFallback);
    app.Name = name;
    Check("and the user agent comes back with the name", app.UserAgentFallback == userAgent);

    var locale = app.GetLocale();
    Check("GetLocale is the locale Chromium resolved: en-US from CEF's --lang on macOS and Windows", OperatingSystem.IsLinux() ? locale.Length > 0 : locale == "en-US", locale);
    // Patched reads GpuDataManager, which is off where Chromium fell back to software; the 0013 checks cover it.
    Check("IsHardwareAccelerationEnabled is on without --disable-gpu (stock CEF), and DisableHardwareAcceleration throws once ready",
        (Oriel.Cef.CefRuntime.IsPatched || app.IsHardwareAccelerationEnabled() == !app.CommandLine.HasSwitch("disable-gpu"))
        &&(await Throws(() => { app.DisableHardwareAcceleration(); return Task.CompletedTask; }))?.Message == "app.disableHardwareAcceleration() can only be called before app is ready");

    // Before the first Complete request, whose DirectX and video reports are what raise GpuInfoUpdate.
    var gpuUpdates = 0;
    EventHandler onGpuUpdate = (_, _) => Interlocked.Increment(ref gpuUpdates);
    app.GpuInfoUpdate += onGpuUpdate;
    // First: once one chrome://gpu has asked for Windows' DirectX rows, later pages report them at once.
    System.Text.Json.Nodes.JsonObject? completeGpuInfo = null;
    try { completeGpuInfo = await app.GetGpuInfoAsync(GpuInfoType.Complete); }
    catch (InvalidOperationException refused) when (Oriel.Cef.CefRuntime.IsPatched)
    {
        // Electron's getGPUInfo rejects once GpuDataManager has no GPU access, as in a container with no GPU.
        var compositing = app.GetGpuFeatureStatus().GetValueOrDefault("gpu_compositing");
        Check("patched (0013): with Chromium in software, GetGpuInfoAsync is refused with Electron's message and IsHardwareAccelerationEnabled is false",
            compositing != "enabled" && refused.Message.StartsWith("GPU access not allowed. Reason: ", StringComparison.Ordinal) && !app.IsHardwareAccelerationEnabled(),
            $"{refused.Message} | gpu_compositing={compositing}");
        Console.WriteLine("SKIP  GetGpuInfoAsync and GpuInfoUpdate checks: Chromium has no GPU access");
    }
    if (completeGpuInfo is not null)
    {
        var dx12 = completeGpuInfo["auxAttributes"]?["dx12FeatureLevel"] is System.Text.Json.Nodes.JsonValue dx12Value && dx12Value.TryGetValue<string>(out var level) ? level : null;
        Check("GetGpuInfoAsync(Complete) waits for the later report: Windows' D3D12 feature level is filled in",
            OperatingSystem.IsWindows() ? dx12 is { Length: > 0 } && dx12 != "Not supported" : dx12 is null, dx12);
        var gpuStatus = await app.GetGpuFeatureStatusAsync();
        Check("GetGpuFeatureStatusAsync lists chrome://gpu's features", gpuStatus.ContainsKey("webgl") && gpuStatus.ContainsKey("gpu_compositing"), string.Join(",", gpuStatus.Select(p => $"{p.Key}={p.Value}")));
        var gpuInfo = await app.GetGpuInfoAsync(GpuInfoType.Basic);
        // Patched Basic is collected afresh in the browser process, as Electron's, which knows no active device; the
        // GPU process marks it, so Complete has it either way.
        Check("GetGpuInfoAsync answers auxAttributes and gpuDevice, the active device first (Complete; Basic too on stock CEF)",
            gpuInfo["auxAttributes"] is System.Text.Json.Nodes.JsonObject && completeGpuInfo["gpuDevice"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } completeDevices
            && completeDevices[0]!["active"]!.GetValue<bool>()
            && (Oriel.Cef.CefRuntime.IsPatched || gpuInfo["gpuDevice"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } devices && devices[0]!["active"]!.GetValue<bool>()),
            gpuInfo.ToJsonString());
        if (!Oriel.Cef.CefRuntime.IsPatched)
        {
            Check("stock CEF: GetGpuInfoAsync(Basic) carries the GPU process's initialization time",
                gpuInfo["auxAttributes"]?["initializationTime"] is System.Text.Json.Nodes.JsonValue initTime && initTime.TryGetValue<double>(out var initMs) && initMs > 0,
                gpuInfo["auxAttributes"]?["initializationTime"]?.ToJsonString());
            Check("stock CEF: GetGpuFeatureStatus (synchronous) needs patch 0013", (await Throws(() => Task.FromResult(app.GetGpuFeatureStatus()))) is PlatformNotSupportedException);
            Check("stock CEF: GpuInfoUpdate is never raised", gpuUpdates == 0, gpuUpdates);
        }
        else
        {
            // Patch 0013: Chromium's GpuDataManager itself, as Electron reads it.
            var aux = completeGpuInfo["auxAttributes"] as System.Text.Json.Nodes.JsonObject;
            Check("patched (0013): GetGpuInfoAsync(Complete) enumerates GPUInfo's fields the chrome://gpu page lacks",
                aux is not null && new[] { "targetCpuBits", "isAsan", "canSupportThreadedTextureMailbox" }.All(aux.ContainsKey)
                && (!OperatingSystem.IsWindows() || aux.ContainsKey("supportsDx12")), aux is null ? "none" : string.Join(",", aux.Select(p => p.Key)));
            var again = System.Diagnostics.Stopwatch.StartNew();
            var second = await app.GetGpuInfoAsync(GpuInfoType.Complete).WaitAsync(TimeSpan.FromSeconds(15));
            Check("patched (0013): a second Complete request answers too, without waiting out the timeout", second["gpuDevice"] is System.Text.Json.Nodes.JsonArray { Count: > 0 }
                && again.Elapsed < TimeSpan.FromSeconds(9), again.Elapsed);
            Check("patched (0013): Basic is collected afresh with every device's vendor and device ids",
                gpuInfo["gpuDevice"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } basicDevices && basicDevices.All(d => d?["vendorId"] is not null && d["deviceId"] is not null), gpuInfo.ToJsonString());
            var syncStatus = app.GetGpuFeatureStatus();
            Check("patched (0013): GetGpuFeatureStatus answers synchronously, the same as chrome://gpu's",
                syncStatus.ContainsKey("webgl") && syncStatus.OrderBy(p => p.Key).SequenceEqual(gpuStatus.OrderBy(p => p.Key)), string.Join(",", syncStatus.Select(p => $"{p.Key}={p.Value}")));
            Check("patched (0013): IsHardwareAccelerationEnabled is GpuDataManager's, on for a hardware GPU", app.IsHardwareAccelerationEnabled() == !app.CommandLine.HasSwitch("disable-gpu"));
            Check("patched (0013): GpuInfoUpdate is raised when the GPU info changes", gpuUpdates > 0, gpuUpdates);
        }
    }
    app.GpuInfoUpdate -= onGpuUpdate;
    Check("DisableDomainBlockingFor3DAPIs throws Electron's error once ready",
        (await Throws(() => { app.DisableDomainBlockingFor3DAPIs(); return Task.CompletedTask; }))?.Message == "app.disableDomainBlockingFor3DAPIs() can only be called before app is ready");

    var a11yEvents = new List<bool>();
    app.AccessibilitySupportChanged += (_, on) => a11yEvents.Add(on);
    var a11yPage = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await a11yPage.WebContents.LoadUrl("data:text/html,<button>press</button>");
    app.SetAccessibilitySupportEnabled(true);
    Check("SetAccessibilitySupportEnabled(true) turns it on and says so", app.IsAccessibilitySupportEnabled() && app.AccessibilitySupportEnabled && a11yEvents.SequenceEqual([true]));
    app.AccessibilitySupportEnabled = false;
    Check("and false turns it off", !app.IsAccessibilitySupportEnabled() && a11yEvents.SequenceEqual([true, false]));
    var unknownFeature = await Throws(() => { app.SetAccessibilitySupportFeatures(["webContents", "bogus"]); return Task.CompletedTask; });
    Check("SetAccessibilitySupportFeatures rejects an unknown feature with Electron's message",
        unknownFeature?.Message.StartsWith("Unknown accessibility feature: bogus") == true, unknownFeature?.Message);
    if (Oriel.Cef.CefRuntime.IsPatched)
    {
        // Patch 0009: the process-wide mode, read back from Chromium.
        app.SetAccessibilitySupportEnabled(true);
        var complete = app.GetAccessibilitySupportFeatures();
        Check("patched (0009): GetAccessibilitySupportFeatures reads the complete mode back",
            new[] { "nativeAPIs", "webContents", "inlineTextBoxes", "extendedProperties" }.All(complete.Contains), string.Join(",", complete));
        app.SetAccessibilitySupportFeatures(["webContents", "screenReader"]);
        var partial = app.GetAccessibilitySupportFeatures();
        Check("patched (0009): SetAccessibilitySupportFeatures replaces the app's mode, and complete mode is off",
            partial.Contains("webContents") && partial.Contains("screenReader") && !app.IsAccessibilitySupportEnabled() && a11yEvents[^1] == false,
            string.Join(",", partial));
        app.SetAccessibilitySupportEnabled(true);
        Check("patched (0009): enabling keeps the features' mode, as Electron's scoper", !app.GetAccessibilitySupportFeatures().Contains("inlineTextBoxes"));
        app.SetAccessibilitySupportFeatures([]);
        Check("patched (0009): no features turns the app's mode off", !app.GetAccessibilitySupportFeatures().Contains("screenReader"));
    }
    else
    {
        Check("stock CEF: SetAccessibilitySupportFeatures throws PlatformNotSupportedException (A28)",
            await Throws(() => { app.SetAccessibilitySupportFeatures(["webContents"]); return Task.CompletedTask; }) is PlatformNotSupportedException);
        app.SetAccessibilitySupportEnabled(true);
        Check("stock CEF: GetAccessibilitySupportFeatures answers the complete mode Oriel asked for",
            app.GetAccessibilitySupportFeatures().SequenceEqual(["nativeAPIs", "webContents", "inlineTextBoxes", "extendedProperties"]));
        app.SetAccessibilitySupportEnabled(false);
    }
    a11yPage.Destroy();

    if (Oriel.Cef.CefRuntime.IsPatched)
    {
        // Patch 0007: Electron's spec crashes the GPU process with chrome://gpucrash.
        var gone = new TaskCompletionSource<ChildProcessGoneDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.ChildProcessGone += (_, details) => gone.TrySetResult(details);
        var gpuCrashPage = new BrowserWindow(new BrowserWindowOptions { Show = false });
        _ = gpuCrashPage.WebContents.LoadUrl("chrome://gpucrash");
        var goneDetails = await gone.Task.WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
        Check("patched (0007): ChildProcessGone reports the GPU process crashing", goneDetails is { Type: "GPU", Reason: "crashed" or "abnormal-exit" },
            goneDetails?.ToString());
        gpuCrashPage.Destroy();
    }

    var resolverError = await Throws(() => { app.ConfigureHostResolver(new HostResolverOptions { SecureDnsServers = ["http://plain"] }); return Task.CompletedTask; });
    app.ConfigureHostResolver(new HostResolverOptions { SecureDnsMode = SecureDnsMode.Off });
    var resolved = await Oriel.Session.DefaultSession.ResolveHostAsync("localhost");
    Check("ConfigureHostResolver refuses a non-HTTPS template and keeps resolving after a change", resolverError?.Message.StartsWith("not a valid DoH template: http://plain") == true
        && resolved.Endpoints.Count > 0, resolverError?.Message);
    app.ConfigureHostResolver(new HostResolverOptions { SecureDnsMode = SecureDnsMode.Automatic });

    var goneEvents = new List<string>();
    var crashPage = new BrowserWindow(new BrowserWindowOptions { Show = false });
    await crashPage.WebContents.LoadUrl("about:blank");
    void OnAppGone(object? _, AppRenderProcessGoneEventArgs e) { if (e.WebContents == crashPage.WebContents) lock (goneEvents) goneEvents.Add("app:" + e.Details.Reason); }
    app.RenderProcessGone += OnAppGone;
    crashPage.WebContents.RenderProcessGone += (_, e) => { lock (goneEvents) goneEvents.Add("page:" + e.Reason); };
    crashPage.WebContents.ForcefullyCrashRenderer();
    var bothGone = await Task.Run(() => SpinWait.SpinUntil(() => { lock (goneEvents) return goneEvents.Count >= 2; }, TimeSpan.FromSeconds(10)));
    app.RenderProcessGone -= OnAppGone;
    Check("app RenderProcessGone comes before the page's, with the page and its details", bothGone && goneEvents[0].StartsWith("app:", StringComparison.Ordinal)
        && goneEvents[1].StartsWith("page:", StringComparison.Ordinal) && goneEvents[0][4..] == goneEvents[1][5..], string.Join(",", goneEvents));
    crashPage.Destroy();

    if (OperatingSystem.IsMacOS())
    {
        var https = await app.GetApplicationInfoForProtocol("https://example.com/");
        Check("GetApplicationInfoForProtocol(https) names the default browser, its bundle and a 32-point icon on macOS",
            https.Path.EndsWith(".app", StringComparison.Ordinal) && https.Name == app.GetApplicationNameForProtocol("https://example.com/")
            && https.Icon.GetSize() == (32, 32), $"{https.Name} {https.Path} {https.Icon.GetSize()}");
        Check("IsActive, IsInApplicationsFolder and IsSecureKeyboardEntryEnabled read without changing anything on macOS",
            !app.IsSecureKeyboardEntryEnabled() && app.IsInApplicationsFolder() == AppContext.BaseDirectory.Split('/').Contains("Applications"), app.IsActive());
        Check("the app delegate is Oriel's, and the login item reads ServiceManagement's status on macOS",
            Oriel.Cef.Platform.MacApp.DelegateClassName() is "OrielAppDelegate" or "OrielApplicationDelegate"
            && app.GetLoginItemSettings().Status is "not-registered" or "enabled" or "requires-approval" or "not-found",
            Oriel.Cef.Platform.MacApp.DelegateClassName());
        Check("IsEmojiPanelSupported is true on macOS", app.IsEmojiPanelSupported());
    }
    if (OperatingSystem.IsLinux())
    {
        Check("IsDefaultProtocolClient is false for a scheme no desktop file handles on Linux", !app.IsDefaultProtocolClient("oriel-none-" + Environment.ProcessId));
        Check("IsEmojiPanelSupported is false on Linux, and login items read as off", !app.IsEmojiPanelSupported() && !app.GetLoginItemSettings().OpenAtLogin);
        await LinuxDesktopIntegrationChecks();
    }
}

// The GIO protocol client against a desktop file installed for the run, and the GtkAboutDialog read back from GTK.
async Task LinuxDesktopIntegrationChecks()
{
    var applications = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome ? dataHome
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "applications");
    Directory.CreateDirectory(applications);
    var desktopName = $"oriel-sessiontest-{Environment.ProcessId}.desktop";
    var desktopFile = Path.Combine(applications, desktopName);
    var iconFile = Path.Combine(Path.GetTempPath(), $"oriel-protocol-icon-{Environment.ProcessId}.png");
    var green = new byte[48 * 48 * 4];
    for (var i = 0; i < green.Length; i += 4) (green[i], green[i + 1], green[i + 2], green[i + 3]) = (0, 200, 0, 255);
    File.WriteAllBytes(iconFile, NativeImage.CreateFromBitmap(green, new NativeImageBitmapOptions { Width = 48, Height = 48 }).ToPng());
    var scheme = $"oriel-proto-{Environment.ProcessId}";
    File.WriteAllText(desktopFile, $"[Desktop Entry]\nType=Application\nName=Oriel Protocol Probe\nExec={Environment.ProcessPath} %u\nIcon={iconFile}\nMimeType=x-scheme-handler/{scheme};\n");
    var previousDesktop = OperatingSystem.IsLinux() ? Oriel.Cef.Platform.LinuxLauncherEntry.DesktopName() : null;
    try
    {
        app.SetDesktopName(desktopName);
        // GIO hears of a new desktop file through a file monitor on the UI thread's main loop.
        var set = false;
        for (var waited = 0; waited < 50 && !(set = app.SetAsDefaultProtocolClient(scheme)); waited++) await Task.Delay(100);
        var url = scheme + "://open";
        var info = await Throws(() => app.GetApplicationInfoForProtocol(url));
        var got = set ? await app.GetApplicationInfoForProtocol(url).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null) : null;
        Check("Linux: SetAsDefaultProtocolClient makes the installed desktop file (SetDesktopName) the scheme's GIO default, and IsDefaultProtocolClient says so",
            set && app.IsDefaultProtocolClient(scheme), $"set={set} default={app.IsDefaultProtocolClient(scheme)}");
        Check("Linux: GetApplicationNameForProtocol and GetApplicationInfoForProtocol read the desktop file's Name and Exec",
            app.GetApplicationNameForProtocol(url) == "Oriel Protocol Probe" && got is { Name: "Oriel Protocol Probe" } && got.Path == Environment.ProcessPath,
            $"{app.GetApplicationNameForProtocol(url)} | {got?.Name} {got?.Path} | {info?.Message}");
        var (iw, ih) = got?.Icon.GetSize() ?? (0, 0);
        var pixels = got?.Icon.ToBitmap() ?? [];
        var mid = ((ih / 2) * iw + iw / 2) * 4;
        Check("Linux: its icon is the desktop file's, through GTK's icon theme at 32 pixels",
            (iw, ih) == (32, 32) && pixels.Length > mid + 3 && pixels[mid + 1] > 150 && pixels[mid] < 60 && pixels[mid + 2] < 60,
            pixels.Length > mid + 3 ? $"{iw}x{ih} {pixels[mid]},{pixels[mid + 1]},{pixels[mid + 2]}" : $"{iw}x{ih}");
    }
    finally
    {
        if (previousDesktop is not null) app.SetDesktopName(previousDesktop);
        File.Delete(desktopFile);
        File.Delete(iconFile);
    }

    var logo = Path.Combine(Path.GetTempPath(), $"oriel-about-logo-{Environment.ProcessId}.png");
    File.WriteAllBytes(logo, NativeImage.CreateFromBitmap(green, new NativeImageBitmapOptions { Width = 48, Height = 48 }).ToPng());
    app.SetAboutPanelOptions(new AboutPanelOptions { ApplicationName = "Oriel About", ApplicationVersion = "9.9", Copyright = "(c) Oriel", Website = "https://oriel.test/", IconPath = logo, Authors = ["Ada", "Grace"], Credits = "unused on Linux" });
    app.ShowAboutPanel();
    LinuxGtkAbout.Fields? about = null;
    for (var waited = 0; waited < 50 && (about = Oriel.Cef.CefThread.InvokeOnUi(LinuxGtkAbout.Read)) is null; waited++) await Task.Delay(100);
    Check("Linux: ShowAboutPanel shows a GtkAboutDialog of the options: name, version, copyright, website, authors, and the icon as a 64-pixel logo",
        about is { Name: "Oriel About", Version: "9.9", Copyright: "(c) Oriel", Website: "https://oriel.test/", Authors: "Ada,Grace", LogoSize: 64 }, about);
    Oriel.Cef.CefThread.InvokeOnUi(() => { LinuxGtkAbout.Respond(); return true; });
    var gone = false;
    for (var waited = 0; waited < 50 && !(gone = Oriel.Cef.CefThread.InvokeOnUi(LinuxGtkAbout.Read) is null); waited++) await Task.Delay(100);
    Check("Linux: any response destroys the about dialog", gone);
    app.SetAboutPanelOptions(new AboutPanelOptions());
    File.Delete(logo);
}

// Fresh, so the run shows CEF creating the persistent partition's directory.
var staleDisk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oriel.SessionTest", "Partition disk");
// The media child has its own data directory and must leave the main run's profiles alone.
if (!mediaChild)
{
    if (Directory.Exists(staleDisk)) Directory.Delete(staleDisk, recursive: true);
    foreach (var name in new[] { "fresh", "fresh-object", "cache", "fresh-ui", "fresh-ui-close", "fresh-ui-destroy" })
    {
        var freshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oriel.SessionTest", "Partition " + name);
        if (Directory.Exists(freshDir)) Directory.Delete(freshDir, recursive: true);
    }
}
// The webRequest checks fetch this scheme from an https page.
Protocol.RegisterSchemesAsPrivileged(new PrivilegedScheme(CustomScheme, new SchemePrivileges { Standard = true, Secure = true, SupportFetchApi = true, CorsEnabled = true }));

app.Ready += (_, _) => _ = Task.Run(async () =>
{
    try
    {
        await (mediaChild ? PermissionChecks(mediaOnly: true) : RunAsync());
        // Last: a window opened before the session checks above stalls them.
        if (!mediaChild && OperatingSystem.IsLinux()) await NoPortalThemeAsync();
    }
    catch (Exception e) { Check("run completed", false, e); }
    failures = results.Count(r => r.StartsWith("FAIL"));
    app.Quit();
});

// The download and service-worker sections took a full run past 90 s on macOS.
_ = Task.Delay(TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("ORIEL_TEST_TIMEOUT") ?? "180"))).ContinueWith(_ =>
{
    Console.WriteLine("FAIL  run did not finish in time");
    Environment.Exit(-1);
}, TaskScheduler.Default);

app.Run();
Console.WriteLine(failures < 0 ? "RESULT: no result" : $"RESULT: {failures} failure(s)");
return failures;

// A body that arrives in chunks over time, so a download can be paused midway.
sealed class DownloadServer
{
    public const string Disposition = "attachment; filename=\"named.bin\"";
    public static readonly byte[] Body = [.. Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251))];
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Ranges = new();
    public int Dropped;
}

sealed class SlowStream(int chunks, int chunkSize) : Stream
{
    private long _remaining = (long)chunks * chunkSize;

    // About 50 ms per chunk, however the reader sizes its buffer.
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(Math.Min(count, chunkSize), _remaining);
        if (n == 0) return 0;
        Thread.Sleep(Math.Max(1, 50 * n / chunkSize));
        Array.Fill(buffer, (byte)'s', offset, n);
        _remaining -= n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Lowers this process's soft open-files limit, so a test can watch SetFdLimit raise it.
static class FdLimit
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current, Maximum;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getrlimit")] private static extern int GetRLimit(int resource, ref RLimit limit);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setrlimit")] private static extern int SetRLimit(int resource, ref RLimit limit);

    public static bool LowerSoft(ulong soft)
    {
        var limit = new RLimit();
        var resource = OperatingSystem.IsMacOS() ? 8 : 7;
        if (GetRLimit(resource, ref limit) != 0) return false;
        limit.Current = soft;
        return SetRLimit(resource, ref limit) == 0;
    }
}

// A process's token integrity level, read through SecurityIdentifier rather than the SID sub-authority walk Oriel does.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class TokenProbe
{
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("advapi32")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [System.Runtime.InteropServices.DllImport("advapi32")] private static extern bool GetTokenInformation(IntPtr token, int information, IntPtr buffer, int length, out int returned);

    public static string? IntegrityName(int pid)
    {
        var process = OpenProcess(0x1000, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, 8, out var token)) return null;
            try
            {
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out var length);
                var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, 25, buffer, length, out _)) return null;
                    var sid = new System.Security.Principal.SecurityIdentifier(System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer));
                    // S-1-16-<level>: 0 untrusted, 4096 low, 8192 medium, 12288 high; others are system or protected.
                    return sid.Value.Split('-')[^1] switch { "0" => "untrusted", "4096" => "low", "8192" => "medium", "12288" => "high", _ => "unknown" };
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}

// macOS's camera and microphone consent for this app: AVCaptureDevice authorizationStatusForMediaType:.
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
static class MacMediaConsent
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // AVAuthorizationStatusAuthorized.
    public const long Authorized = 3;

    [System.Runtime.InteropServices.DllImport(ObjC)] private static extern nint objc_getClass([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string name);
    [System.Runtime.InteropServices.DllImport(ObjC)] private static extern nint sel_registerName([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string name);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendString(nint receiver, nint selector, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string value);
    [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendObject(nint receiver, nint selector, nint value);

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)]
    private static extern bool CGPreflightScreenCaptureAccess();

    /// <summary>Whether macOS lets this app record the screen, without asking.</summary>
    public static bool ScreenCapture() => CGPreflightScreenCaptureAccess();

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern nint CGSessionCopyCurrentDictionary();
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)] private static extern bool CFBooleanGetValue(nint boolean);
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] private static extern void CFRelease(nint value);

    /// <summary>Whether the login session's screen is locked (CGSSessionScreenIsLocked, absent when unlocked).</summary>
    public static bool ScreenLocked()
    {
        var session = CGSessionCopyCurrentDictionary();
        if (session == 0) return false;
        var key = CFStringCreateWithCString(0, "CGSSessionScreenIsLocked", 0x08000100);
        var value = CFDictionaryGetValue(session, key);
        var locked = value != 0 && CFBooleanGetValue(value);
        CFRelease(key);
        CFRelease(session);
        return locked;
    }

    /// <summary>The audio and video statuses (0 not determined, 1 restricted, 2 denied, 3 authorized); -1 when unreadable.</summary>
    public static (long Audio, long Video) Read()
    {
        // Chromium links AVFoundation already; loading it again is a no-op that makes sure the class exists.
        System.Runtime.InteropServices.NativeLibrary.TryLoad("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", out _);
        var device = objc_getClass("AVCaptureDevice");
        if (device == 0) return (-1, -1);
        var status = sel_registerName("authorizationStatusForMediaType:");
        var nsString = objc_getClass("NSString");
        var fromUtf8 = sel_registerName("stringWithUTF8String:");
        // AVMediaTypeAudio and AVMediaTypeVideo are the strings "soun" and "vide".
        long Of(string mediaType) => SendObject(device, status, SendString(nsString, fromUtf8, mediaType));
        return (Of("soun"), Of("vide"));
    }
}