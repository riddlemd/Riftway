using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel;

// End-to-end IPC check: the page runs its assertions and reports them; the exit code is the
// number of failures (or -1 when no report arrives), so CI can run it under Xvfb.
var app = OrielApp.Create("Oriel.IpcTest");
if (args.Contains("--enable-sandbox")) app.EnableSandbox();
var failures = -1;
BrowserWindow? window = null;

IpcMain.Handle("ping", e => JsonValue.Create("pong:" + e.Args[0].GetString()));
IpcMain.Handle("add", e => JsonValue.Create(e.Args[0].GetInt32() + e.Args[1].GetInt32()));
IpcMain.Handle("fail", _ => throw new InvalidOperationException("boom"));
IpcMain.On("subscribe", e => e.Sender.Send("tick", 42));
IpcMain.Handle("report", e =>
{
    // Counted locally and published only once the whole report has been read: a malformed
    // report must leave `failures` at -1 so the fallback below still ends the run.
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
    return null;
});

app.Ready += (_, _) =>
{
    var web = Path.Combine(AppContext.BaseDirectory, "web");
    window = new BrowserWindow(new BrowserWindowOptions
    {
        Title = "Oriel IPC test",
        WebPreferences = new WebPreferences { Preload = Path.Combine(web, "preload.js") },
    });
    window.LoadUrl(new Uri(Path.Combine(web, "index.html")).AbsoluteUri);

    // A page that never reports (broken preload, crash) must still end the run.
    _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
    {
        if (failures < 0)
        {
            Console.WriteLine("FAIL  no report from the page within 30s");
            window.Close();
        }
    }, TaskScheduler.Default);
};

app.Run();
Console.WriteLine(failures < 0 ? "RESULT: no report" : $"RESULT: {failures} failure(s)");
return failures;
