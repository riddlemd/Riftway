using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Oriel;

// End-to-end protocol check: the page is served from app://, runs its checks against the handler
// below and reports them; the exit code is the number of failures (or -1 when no report arrives).
var app = OrielApp.Create("Oriel.ProtocolTest");
if (args.Contains("--enable-sandbox")) app.EnableSandbox();
var failures = -1;
BrowserWindow? window = null;
var web = Path.Combine(AppContext.BaseDirectory, "web");

Protocol.RegisterSchemesAsPrivileged(new PrivilegedScheme("app", new SchemePrivileges
{
    Standard = true,
    Secure = true,
    SupportFetchApi = true,
    CorsEnabled = true,
}));

var rangeBody = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
var bigBody = Enumerable.Range(0, 5 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();

// loadURL checks: what each /nav?step=… request carried, and what navigator.userAgent said there.
var navRequests = new ConcurrentDictionary<string, ConcurrentQueue<ProtocolRequest>>();
var navPages = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
string? initialHeader = null;
TaskCompletionSource<string> NavPage(string step) => navPages.GetOrAdd(step, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

Protocol.Handle("app", request =>
{
    var path = new Uri(request.Url).AbsolutePath;
    return path switch
    {
        "/echo" => ProtocolResponse.Text(
            new JsonObject
            {
                ["method"] = request.Method,
                ["header"] = request.Headers.GetValueOrDefault("X-Test"),
                ["body"] = request.BodyText,
                ["url"] = request.Url,
            }.ToJsonString(),
            "application/json"),
        "/nav" => RecordNav(request),
        "/index.html" when initialHeader is null => RecordInitial(request),
        "/range.bin" => ProtocolResponse.Bytes(rangeBody),
        "/big" => ProtocolResponse.Bytes(bigBody),
        "/throws" => throw new InvalidOperationException("handler failure"),
        _ => ProtocolResponse.File(Path.Combine(web, path.TrimStart('/'))),
    };
});

ProtocolResponse RecordInitial(ProtocolRequest request)
{
    initialHeader = request.Headers.GetValueOrDefault("X-Initial") ?? "";
    return ProtocolResponse.File(Path.Combine(web, "index.html"));
}

ProtocolResponse RecordNav(ProtocolRequest request)
{
    navRequests.GetOrAdd(new Uri(request.Url).Query, _ => new()).Enqueue(request);
    return ProtocolResponse.File(Path.Combine(web, "nav.html"));
}

// Each finished check, as it happens: shows where a run that never reports got stuck.
IpcMain.On("progress", e => Console.Error.WriteLine($"[progress] {e.Args[0].GetString()}"));

IpcMain.On("navigated", e => NavPage(e.Args[0].GetString()!).TrySetResult(e.Args[1].GetString()!));

IpcMain.Handle("report", e =>
{
    // Published only once fully read, so a malformed report still ends in the fallback below.
    var results = new List<(string Name, bool Pass, string Detail)>();
    foreach (var result in e.Args[0].EnumerateArray())
        results.Add((result.GetProperty("name").GetString()!, result.GetProperty("pass").GetBoolean(), result.GetProperty("detail").GetString()!));
    _ = RunLoadUrlChecks(results).ContinueWith(done =>
    {
        if (done.Exception is { } error) results.Add(("loadURL checks ran", false, error.InnerException?.Message ?? error.Message));
        foreach (var (name, pass, detail) in results) Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}  [{detail}]");
        failures = results.Count(r => !r.Pass);
        window?.Close();
    }, TaskScheduler.Default);
    return (JsonNode?)null;
});

// Navigates the window itself, so it runs after the page's own checks have reported.
async Task RunLoadUrlChecks(List<(string Name, bool Pass, string Detail)> results)
{
    void Check(string name, bool pass, string? detail)
    {
        results.Add((name, pass, detail ?? "null"));
        Console.Error.WriteLine($"[progress] {name}");
    }

    async Task<(ProtocolRequest Request, string PageUserAgent)> Load(string url, LoadUrlOptions? options = null)
    {
        var step = new Uri(url).Query;
        _ = window!.LoadUrl(url, options);
        var userAgent = await NavPage(step).Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (navRequests[step].Last(), userAgent);
    }

    static string? Header(ProtocolRequest r, string name) => r.Headers.GetValueOrDefault(name);
    static string? Referrer(ProtocolRequest r) => r.Referrer ?? Header(r, "Referer");

    Check("loadURL options on a load queued before the page was ready", initialHeader == "queued", initialHeader);

    var upload = Path.Combine(Path.GetTempPath(), $"oriel-upload-{Environment.ProcessId}.txt");
    await File.WriteAllTextAsync(upload, "file part");

    var (withOptions, pageAgent) = await Load("app://test/nav?step=options", new LoadUrlOptions
    {
        ExtraHeaders = "X-One: 1\nX-Two:  two ",
        HttpReferrer = new Referrer("https://example.com/from/page?x=1", ReferrerPolicy.Origin),
        UserAgent = "OrielTest/1.0",
        PostData = [new UploadRawData("a=1&b=2"u8.ToArray()), new UploadFile(upload)],
    });
    Check("loadURL extraHeaders arrive", Header(withOptions, "X-One") == "1" && Header(withOptions, "X-Two") == "two",
        $"X-One={Header(withOptions, "X-One")} X-Two={Header(withOptions, "X-Two")}");
    Check("loadURL httpReferrer arrives, trimmed by its policy", Referrer(withOptions) == "https://example.com/", Referrer(withOptions));
    Check("loadURL postData: POST with raw bytes and file", withOptions.Method == "POST" && withOptions.BodyText == "a=1&b=2"
        && withOptions.UploadFiles.SequenceEqual([upload]),
        $"{withOptions.Method} body={withOptions.BodyText} files={string.Join(",", withOptions.UploadFiles)}");
    Check("loadURL userAgent: request header", Header(withOptions, "User-Agent") == "OrielTest/1.0", Header(withOptions, "User-Agent"));
    Check("loadURL userAgent: navigator.userAgent", pageAgent == "OrielTest/1.0", pageAgent);
    Check("loadURL userAgent: getUserAgent", window!.WebContents.GetUserAgent() == "OrielTest/1.0", window.WebContents.GetUserAgent());

    var (plain, plainAgent) = await Load("app://test/nav?step=plain");
    Check("plain loadURL after options: no extra headers, referrer or body",
        Header(plain, "X-One") is null && Header(plain, "X-Two") is null && Referrer(plain) is null && plain.Method == "GET" && plain.Body is not { Length: > 0 },
        $"{plain.Method} X-One={Header(plain, "X-One")} referrer={Referrer(plain)} body={plain.BodyText}");
    // Electron's userAgent option is setUserAgent: it outlives the one navigation.
    Check("plain loadURL after options: user agent stays", Header(plain, "User-Agent") == "OrielTest/1.0" && plainAgent == "OrielTest/1.0",
        $"header={Header(plain, "User-Agent")} navigator={plainAgent}");

    // The first request may already be under way when the second load cancels it.
    _ = window.LoadUrl("app://test/nav?step=superseded", new LoadUrlOptions { ExtraHeaders = "X-One: stale" });
    var (superseded, _) = await Load("app://test/nav?step=superseded");
    Check("loadURL superseded by a plain loadURL of that URL: the plain request has no options", Header(superseded, "X-One") is null,
        $"requests={navRequests["?step=superseded"].Count} X-One={Header(superseded, "X-One")}");

    // A fragment-only load stays in the document and makes no request, so its options stay pending.
    await Load("app://test/nav?step=fragment");
    _ = window.LoadUrl("app://test/nav?step=fragment#a", new LoadUrlOptions { ExtraHeaders = "X-One: fragment" });
    await NavPage("#fetched").Task.WaitAsync(TimeSpan.FromSeconds(10));
    var fetched = navRequests["?step=fragment"].Last();
    Check("pending loadURL options skip the page's own fetch of that URL", Header(fetched, "X-One") is null, Header(fetched, "X-One"));
    navPages.TryRemove("?step=fragment", out _);
    var (afterFragment, _) = await Load("app://test/nav?step=fragment");
    Check("plain loadURL clears options a fragment-only loadURL left pending",
        Header(afterFragment, "X-One") is null && navRequests["?step=fragment"].Count == 3,
        $"requests={navRequests["?step=fragment"].Count} X-One={Header(afterFragment, "X-One")}");

    // nav.html navigates itself once to the same URL for this step.
    await Load("app://test/nav?step=again", new LoadUrlOptions { ExtraHeaders = "X-One: once" });
    var again = navRequests["?step=again"].ToArray();
    Check("loadURL options apply once: the page's own reload of that URL sends none",
        again.Length == 2 && Header(again[0], "X-One") == "once" && Header(again[1], "X-One") is null,
        $"requests={again.Length} X-One={string.Join(",", again.Select(r => Header(r, "X-One") ?? "-"))}");

    // A javascript: URL makes no request, so its options stay pending while the page navigates elsewhere.
    _ = window.LoadUrl("javascript:location.href='app://test/nav?step=after-js'", new LoadUrlOptions { ExtraHeaders = "X-One: js" });
    await NavPage("?step=after-js").Task.WaitAsync(TimeSpan.FromSeconds(10));
    var afterJs = navRequests["?step=after-js"].Last();
    Check("loadURL options never reach another URL's navigation", Header(afterJs, "X-One") is null, Header(afterJs, "X-One"));

    window.WebContents.SetUserAgent("OrielTest/2.0");
    Check("setUserAgent then getUserAgent", window.WebContents.GetUserAgent() == "OrielTest/2.0", window.WebContents.GetUserAgent());
    // Another origin: a new renderer, which must still get the override.
    var (other, otherAgent) = await Load("app://other/nav?step=set");
    Check("setUserAgent: header and navigator on another origin", Header(other, "User-Agent") == "OrielTest/2.0" && otherAgent == "OrielTest/2.0",
        $"header={Header(other, "User-Agent")} navigator={otherAgent}");

    window.WebContents.SetUserAgent("");
    var restored = window.WebContents.GetUserAgent();
    var (reset, resetAgent) = await Load("app://test/nav?step=reset");
    Check("setUserAgent(\"\") restores the default", restored.Contains("Chrome/") && !restored.Contains("OrielTest")
        && Header(reset, "User-Agent") == restored && resetAgent == restored,
        $"get={restored} header={Header(reset, "User-Agent")} navigator={resetAgent}");

    File.Delete(upload);
}

app.Ready += (_, _) =>
{
    window = new BrowserWindow(new BrowserWindowOptions
    {
        Title = "Oriel protocol test",
        WebPreferences = new WebPreferences { Preload = Path.Combine(web, "preload.js") },
    });
    // Right after construction, before any navigation.
    window.WebContents.SetUserAgent("OrielTest/0.9");
    // Queued until the preload is registered: the options must survive the wait.
    window.LoadUrl("app://test/index.html", new LoadUrlOptions { ExtraHeaders = "X-Initial: queued" });

    _ = Task.Delay(TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("ORIEL_TEST_TIMEOUT") ?? "60"))).ContinueWith(_ =>
    {
        if (failures < 0)
        {
            Console.WriteLine("FAIL  no report from the page in time");
            window.Close();
        }
    }, TaskScheduler.Default);
};

app.Run();
Console.WriteLine(failures < 0 ? "RESULT: no report" : $"RESULT: {failures} failure(s)");
return failures;
