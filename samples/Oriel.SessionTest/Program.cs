using System.Net;
using System.Net.Sockets;
using System.Text;
using Oriel;
using Cookie = Oriel.Cookie;

// End-to-end Session check: partitions, cookies, per-session protocol and proxy, driven from a worker thread.
// The exit code is the number of failures (-1 when the run never finishes).
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
    Check("a cookie the page sets lands in its session",
        (await memory.Cookies.GetAsync(new CookieFilter { Name = "fromPage" })).Count == 1
        && (await defaultSession.Cookies.GetAsync(new CookieFilter { Name = "fromPage" })).Count == 0);

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
