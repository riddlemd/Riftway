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
        "/range.bin" => ProtocolResponse.Bytes(rangeBody),
        "/big" => ProtocolResponse.Bytes(bigBody),
        "/throws" => throw new InvalidOperationException("handler failure"),
        _ => ProtocolResponse.File(Path.Combine(web, path.TrimStart('/'))),
    };
});

// Each finished check, as it happens: shows where a run that never reports got stuck.
IpcMain.On("progress", e => Console.Error.WriteLine($"[progress] {e.Args[0].GetString()}"));

IpcMain.Handle("report", e =>
{
    // Published only once fully read, so a malformed report still ends in the fallback below.
    var count = 0;
    var lines = new List<string>();
    foreach (var result in e.Args[0].EnumerateArray())
    {
        var pass = result.GetProperty("pass").GetBoolean();
        if (!pass) count++;
        lines.Add($"{(pass ? "PASS" : "FAIL")}  {result.GetProperty("name").GetString()}  [{result.GetProperty("detail").GetString()}]");
    }
    lines.ForEach(Console.WriteLine);
    failures = count;
    window?.Close();
    return (JsonNode?)null;
});

app.Ready += (_, _) =>
{
    window = new BrowserWindow(new BrowserWindowOptions
    {
        Title = "Oriel protocol test",
        WebPreferences = new WebPreferences { Preload = Path.Combine(web, "preload.js") },
    });
    window.LoadUrl("app://test/index.html");

    _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ =>
    {
        if (failures < 0)
        {
            Console.WriteLine("FAIL  no report from the page within 60s");
            window.Close();
        }
    }, TaskScheduler.Default);
};

app.Run();
Console.WriteLine(failures < 0 ? "RESULT: no report" : $"RESULT: {failures} failure(s)");
return failures;
