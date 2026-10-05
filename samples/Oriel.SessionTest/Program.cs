using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Oriel;
using Cookie = Oriel.Cookie;

// End-to-end Session check: partitions, cookies, per-session protocol and proxy, driven from a worker thread.
// The exit code is the number of failures (-1 when the run never finishes).
// Linux: two client certificates in the user's NSS store, which Chromium opens at start-up.
var clientCertificates = OperatingSystem.IsLinux() && File.Exists("/usr/bin/certutil") && File.Exists("/usr/bin/pk12util") ? InstallClientCertificates() : false;
var app = OrielApp.Create("Oriel.SessionTest");
var failures = -1;
var results = new List<string>();

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

static string Names(IEnumerable<Cookie> cookies) => string.Join(",", cookies.Select(c => c.Name).Order());

const string Site = "https://oriel.test/";
const string Page = "<!doctype html><title>session</title><p>session test</p>";

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
    Check("other sessions keep the app's", defaultSession.GetUserAgent() == app.UserAgentFallback && memory.GetUserAgent() == app.UserAgentFallback);
    later.Destroy();
    agentWin.Destroy();

    // Permissions: granted unless the session's handler says otherwise, as Electron.
    // Geolocation: a refusal is error 1; a grant goes on to the (absent) location source, so any other outcome.
    async Task<string?> AskLocation(string partition, Func<WebContents, string, PermissionRequestDetails, Task<bool>>? handler)
    {
        var session = Session.FromPartition(partition);
        session.Protocol.Handle("https", _ => ProtocolResponse.Text(Page, "text/html"));
        session.SetPermissionRequestHandler(handler);
        var w = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Session = session } });
        await w.LoadUrl(Site);
        var answer = (await w.WebContents.ExecuteJavaScriptAsync(
            "new Promise(r => navigator.geolocation.getCurrentPosition(() => r('ok'), e => r(e.code === 1 ? 'denied' : 'granted'), { timeout: 2000 }))")
            .WaitAsync(TimeSpan.FromSeconds(10)))?.ToString();
        w.Destroy();
        return answer;
    }
    var byDefault = await AskLocation("perm-default", null);
    Check("with no permission handler, a permission is granted, as Electron", byDefault is "granted" or "ok", byDefault);
    var asked = new List<string>();
    var denied = await AskLocation("perm-deny", (page, permission, details) =>
    {
        asked.Add($"{permission}|{details.RequestingUrl}|{page is not null}");
        return Task.FromResult(false);
    });
    Check("a permission handler that refuses denies it, and is told what was asked", denied == "denied"
        && asked.Count == 1 && asked[0].StartsWith("geolocation|https://oriel.test", StringComparison.Ordinal) && asked[0].EndsWith("|True"), $"{denied} {string.Join(";", asked)}");
    var granted = await AskLocation("perm-grant", (_, _, _) => Task.FromResult(true));
    Check("a permission handler that grants allows it", granted is "granted" or "ok", granted);

    // Downloads: Session.WillDownload and DownloadItem, as Electron.
    var downloads = Session.FromPartition("downloads");
    var report = Encoding.UTF8.GetBytes("report body " + new string('x', 4000));
    const int SlowChunks = 40, SlowChunk = 64 * 1024;
    downloads.Protocol.Handle("https", request =>
    {
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

    var linked = await Download(() => _ = downloadWin.WebContents.ExecuteJavaScriptAsync(
        "const a = document.createElement('a'); a.href = '/report.txt'; a.download = ''; document.body.append(a); a.click(); 1", userGesture: true),
        e => e.Item.SetSavePath(Path.Combine(downloadDir, "linked.txt")));
    Check("a download link raises WillDownload", linked.Item?.GetUrl() == Site + "report.txt" && linked.State == DownloadState.Completed, $"{linked.Item?.GetUrl()} {linked.State}");
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
    downloadWin.Destroy();
    Directory.Delete(downloadDir, recursive: true);

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
    requestWin.WebContents.DidFailLoad += (_, e) => failedLoads.Add(e.ErrorCode);
    var blockedLoad = await Throws(() => requestWin.LoadUrl(Site + "blocked").WaitAsync(TimeSpan.FromSeconds(10)));
    Check("a navigation a listener cancels fails LoadUrl and DidFailLoad with ERR_BLOCKED_BY_CLIENT, as Electron's",
        blockedLoad is LoadFailedException { ErrorCode: -20 } && failedLoads.SequenceEqual([-20]), $"{blockedLoad?.Message} [{string.Join(",", failedLoads)}]");
    var unfiltered = await Fetch("/unfiltered");
    Check("onBeforeRequest follows its filter", unfiltered == Page && Saw("before", "unfiltered") is null && await WaitFor(() => Saw("completed", "unfiltered")) is not null);

    var followed = await Fetch("/server-redirect");
    var redirect = await WaitFor(() => Saw("redirect", "server-redirect"));
    Check("onBeforeRedirect reports a server redirect", followed == "data" && redirect is { StatusCode: 302, RedirectUrl: Site + "data" },
        $"{followed} {redirect?.StatusCode} {redirect?.RedirectUrl}");

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
    var spellWin = new BrowserWindow(new BrowserWindowOptions { Width = 500, Height = 200, WebPreferences = new WebPreferences { Session = spelling } });
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
    // Linux and Windows download the dictionary first; text typed before it arrives stays unchecked.
    ContextMenuParams? misspelled = null;
    for (var i = 0; i < 15 && misspelled?.MisspelledWord is not { Length: > 0 }; i++)
    {
        await TypeMisspelling();
        misspelled = await RightClickFirstWord();
    }
    Check("a misspelled word reaches the context menu with suggestions", misspelled is { MisspelledWord: "helo" } && misspelled.DictionarySuggestions.Contains("hello"),
        $"{misspelled?.MisspelledWord} [{string.Join(",", misspelled?.DictionarySuggestions ?? [])}] menu={misspelled is not null} editable={misspelled?.IsEditable} spellcheck={misspelled?.SpellcheckEnabled} value={await spellWin.WebContents.ExecuteJavaScriptAsync("t.value")}");
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
    await navWin.LoadUrl(Site + "start");
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
    await ContentTracing.StartRecordingAsync("blink");
    var twice = await Throws(() => ContentTracing.StartRecordingAsync("blink"));
    File.Delete(await ContentTracing.StopRecordingAsync());
    var unstarted = await Throws(() => ContentTracing.StopRecordingAsync());
    Check("starting twice, or stopping what was not started, fails with the reason",
        twice is InvalidOperationException && twice.Message.Contains("already started") && unstarted is InvalidOperationException && unstarted.Message.Contains("has not started"),
        $"{twice?.Message} / {unstarted?.Message}");
    Check("trace options CEF does not have throw NotSupportedException",
        await Throws(() => ContentTracing.StartRecordingAsync(new TraceConfig { RecordingMode = "record-continuously" })) is NotSupportedException
        && await Throws(() => ContentTracing.StartRecordingAsync(new TraceConfig { IncludedCategories = ["a"], ExcludedCategories = ["b"] })) is NotSupportedException
        && await Throws(() => ContentTracing.StartRecordingAsync("blink", "record-until-full")) is NotSupportedException
        && await Throws(() => ContentTracing.GetCategoriesAsync()) is NotSupportedException);
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
    if (OperatingSystem.IsLinux())
    {
        var userDirs = Path.Combine(app.GetPath("appData"), "user-dirs.dirs");
        File.WriteAllText(userDirs, "XDG_DOWNLOAD_DIR=\"$HOME/Fetched\"\n");
        var downloadsDir = app.GetPath("downloads");
        File.Delete(userDirs);
        Check("GetPath(downloads) follows the XDG user directories on Linux", downloadsDir == Path.Combine(app.GetPath("home"), "Fetched")
            && app.GetPath("downloads") == Path.Combine(app.GetPath("home"), "Downloads"), downloadsDir);
    }
    var unknownPath = await Throws(() => Task.FromResult(app.GetPath("nope")));
    var missingDir = await Throws(() => { app.SetPath("logs", Path.Combine(Path.GetTempPath(), "oriel-no-such-dir-" + Guid.NewGuid())); return Task.CompletedTask; });
    var relativeDir = await Throws(() => { app.SetPath("logs", "relative"); return Task.CompletedTask; });
    Check("GetPath and SetPath refuse what Electron refuses", unknownPath?.Message.StartsWith("Failed to get 'nope' path") == true
        && missingDir is ArgumentException && relativeDir is ArgumentException, $"{unknownPath?.Message} | {missingDir?.Message} | {relativeDir?.Message}");
    var movedTo = Directory.CreateTempSubdirectory("oriel-userdata-").FullName;
    var originalUserData = app.GetPath("userData");
    app.SetPath("userData", movedTo);
    Check("SetPath overrides GetPath, and sessionData follows a moved userData", app.GetPath("userData") == movedTo && app.GetPath("sessionData") == movedTo);
    app.SetPath("userData", originalUserData);
    Directory.Delete(movedTo);

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
    proxyWin.Close();
    listener.Stop();
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

// Serves "tls ok" over TLS with the given certificate, to every connection; with requireClient, says which client
// certificate came.
static async Task ServeTls(TcpListener listener, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, bool requireClient = false)
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
                    await tls.AuthenticateAsServerAsync(certificate, requireClient, false);
                    var who = tls.RemoteCertificate is { } remote ? new System.Security.Cryptography.X509Certificates.X509Certificate2(remote).GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) : "none";
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
static async Task<string> ServeOneProxyRequest(TcpListener listener)
{
    using var client = await listener.AcceptTcpClientAsync();
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

// Fresh, so the run shows CEF creating the persistent partition's directory.
var staleDisk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oriel.SessionTest", "Partition disk");
if (Directory.Exists(staleDisk)) Directory.Delete(staleDisk, recursive: true);

app.Ready += (_, _) => _ = Task.Run(async () =>
{
    try { await RunAsync(); }
    catch (Exception e) { Check("run completed", false, e); }
    failures = results.Count(r => r.StartsWith("FAIL"));
    app.Quit();
});

_ = Task.Delay(TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("ORIEL_TEST_TIMEOUT") ?? "90"))).ContinueWith(_ =>
{
    Console.WriteLine("FAIL  run did not finish in time");
    Environment.Exit(-1);
}, TaskScheduler.Default);

app.Run();
Console.WriteLine(failures < 0 ? "RESULT: no result" : $"RESULT: {failures} failure(s)");
return failures;

// A body that arrives in chunks over time, so a download can be paused midway.
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
