using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Oriel;

// End-to-end utilityProcess / parentPort check, driven from C#: forks Oriel.UtilityTest.Child (published beside this
// app) and checks each behaviour against Electron's. Exit code = failures (-1 when the run never finishes).
//
// Windows: dotnet publish samples/Oriel.UtilityTest -r win-x64 -c Release --self-contained -o <dir>, add the CEF
//          runtime and Oriel.Subprocess (eng/layout-windows.ps1), run <dir>/Oriel.UtilityTest.exe.
// macOS:   dotnet publish src/Oriel.Subprocess -r osx-arm64 -c Release -o artifacts/subprocess
//          dotnet publish samples/Oriel.UtilityTest -r osx-arm64 -c Release --self-contained -o artifacts/utility
//          eng/bundle-macos.sh artifacts/utility Oriel.UtilityTest artifacts/subprocess/Oriel.Subprocess artifacts
//          artifacts/Oriel.UtilityTest.app/Contents/MacOS/Oriel.UtilityTest
// Linux:   in the linux-dev container (see README; Xvfb on DISPLAY=:99 as run-linux.sh starts it):
//          src/Oriel.Subprocess.Native/build.sh out/subprocess/Oriel.Subprocess linuxarm64
//          dotnet publish samples/Oriel.UtilityTest -r linux-arm64 -c Release --self-contained -o out/utility
//          eng/layout-linux.sh out/utility out/subprocess/Oriel.Subprocess linuxarm64
//          out/utility/Oriel.UtilityTest   (x64 hosts: linux-x64 / linux64)
// --linger pauses 60 s before the end: kill the app then, and its utility processes must end on their own.
var app = OrielApp.Create("Oriel.UtilityTest");
var failures = 0;
var finished = false;
var step = TimeSpan.FromSeconds(10);
var childDll = Path.Combine(AppContext.BaseDirectory, "Oriel.UtilityTest.Child.dll");
BrowserWindow? window = null;
var pageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int? quitChildPid = null;

void Check(string name, bool pass, object? detail = null)
{
    if (!pass) failures++;
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}  [{detail}]");
}

// A check whose body throws fails, and the run goes on.
async Task Step(string name, Func<Task> body)
{
    try { await body(); }
    catch (Exception e) { Check(name, false, $"threw {e.GetType().Name}: {e.Message}"); }
}

async Task<T?> Within<T>(Task<T> task, TimeSpan? timeout = null)
{
    try { return await task.WaitAsync(timeout ?? step); }
    catch (TimeoutException) { return default; }
}

async Task<bool> Completes(Task task, TimeSpan? timeout = null)
{
    try { await task.WaitAsync(timeout ?? step); return true; }
    catch (TimeoutException) { return false; }
}

// Forked and subscribed in one UI-thread task, as Electron's fork() and on() run in one JS turn: from a worker thread
// the UI thread could raise spawn before the listener is added. setup runs in the same task.
Watched Fork(IReadOnlyList<string>? args = null, UtilityProcessOptions? options = null, string? module = null, Action<Watched>? setup = null)
    => Oriel.Cef.CefThread.InvokeOnUi(() =>
    {
        var watched = new Watched(UtilityProcess.Fork(module ?? childDll, args, options));
        setup?.Invoke(watched);
        return watched;
    });

// Before ready, as Electron's: forking throws.
try
{
    UtilityProcess.Fork(childDll);
    Check("fork before ready throws", false, "no exception");
}
catch (InvalidOperationException e)
{
    Check("fork before ready throws", e.Message == "utilityProcess cannot be created before app is ready.", e.Message);
}

try
{
    ParentPort.PostMessage("x");
    Check("parentPort outside a utility process throws", false, "no exception");
}
catch (InvalidOperationException e)
{
    Check("parentPort outside a utility process throws", true, e.Message);
}

IpcMain.On("page-ready", _ => pageReady.TrySetResult());

async Task Drive()
{
    await Step("argument validation", async () =>
    {
        await Task.CompletedTask;
        try { UtilityProcess.Fork(""); Check("empty modulePath throws", false); }
        catch (ArgumentException e) { Check("empty modulePath throws", e.Message.StartsWith("Missing UtilityProcess entry script.", StringComparison.Ordinal), e.Message); }
        try { UtilityProcess.Fork(childDll, new UtilityProcessOptions { Stdio = "bogus" }); Check("bad stdio throws", false); }
        catch (ArgumentException e) { Check("bad stdio throws", e.Message == "stdio must be of the following values: inherit, pipe, ignore", e.Message); }
        try { UtilityProcess.Fork(childDll, new UtilityProcessOptions { Stdio = new[] { "pipe", "pipe", "pipe" } }); Check("stdin other than ignore throws", false); }
        catch (ArgumentException e) { Check("stdin other than ignore throws", e.Message == "stdin value other than ignore is not supported.", e.Message); }
    });

    Watched? echo = null;
    await Step("spawn", async () =>
    {
        int? pidBefore = null;
        var killBefore = true;
        echo = Fork(["late"], setup: w =>
        {
            pidBefore = w.Process.Pid;
            killBefore = w.Process.Kill();
            // Sent before the child is even running: queued until its listener is added.
            w.Process.PostMessage(new JsonObject { ["cmd"] = "echo", ["v"] = "early" });
        });
        Check("pid is null right after fork", pidBefore is null, pidBefore);
        Check("kill before spawn returns false", !killBefore);
        Check("spawn is raised", await Completes(echo.Spawned.Task));
        Check("pid is set at spawn", echo.PidAtSpawn is > 0, echo.PidAtSpawn);
        Check("stdout and stderr are null by default (inherit)", echo.Process.Stdout is null && echo.Process.Stderr is null);
    });

    await Step("message round trip", async () =>
    {
        var reply = await Within(echo!.Next(m => m?["echo"] is not null));
        Check("message sent before spawn arrives", reply?["echo"]?.GetValue<string>() == "early", reply?.ToJsonString());
        echo.Process.PostMessage(new JsonObject { ["cmd"] = "echo", ["v"] = new JsonObject { ["n"] = 42, ["s"] = "é\"\n" } });
        reply = await Within(echo.Next(m => m?["echo"] is not null));
        Check("message round trip keeps JSON", reply?["echo"]?["n"]?.GetValue<int>() == 42 && reply?["echo"]?["s"]?.GetValue<string>() == "é\"\n", reply?.ToJsonString());
    });

    await Step("port transfer", async () =>
    {
        var channel = new MessageChannelMain();
        var got = Channel.CreateUnbounded<string?>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Port1.Message += (_, e) => got.Writer.TryWrite(e.Data?.GetValue<string>());
        channel.Port1.Start();
        echo!.Process.PostMessage(new JsonObject { ["cmd"] = "port" }, [channel.Port2]);
        Check("child gets the port", await Within(echo.Next(m => m?.ToJsonString() == "\"port-ready\"")) is not null);
        try
        {
            echo.Process.PostMessage(new JsonObject { ["cmd"] = "port" }, [channel.Port2]);
            Check("a moved port cannot be sent again", false, "no exception");
        }
        catch (InvalidOperationException e) { Check("a moved port cannot be sent again", true, e.Message); }
        channel.Port1.PostMessage("x");
        var echoed = await Within(got.Reader.ReadAsync().AsTask());
        Check("port entangled main <-> child", echoed == "child:x", echoed);
        channel.Port1.Close();
        Check("closing the main end raises close in the child", await Within(echo.Next(m => m?.ToJsonString() == "\"port-closed\"")) is not null);

        var second = new MessageChannelMain();
        second.Port1.Closed += (_, _) => closed.TrySetResult();
        second.Port1.Start();
        echo.Process.PostMessage(new JsonObject { ["cmd"] = "close-port" }, [second.Port2]);
        Check("closing the child end raises close in main", await Completes(closed.Task));
    });

    await Step("port from the child", async () =>
    {
        var via = new MessageChannelMain();
        var arrived = new TaskCompletionSource<MessagePortEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        via.Port1.Message += (_, e) => arrived.TrySetResult(e);
        via.Port1.Start();
        echo!.Process.PostMessage(new JsonObject { ["cmd"] = "make-channel" }, [via.Port2]);
        var e = await Within(arrived.Task);
        Check("a port made in the child arrives in main", e is { Ports.Count: 1 } && e.Data?.GetValue<string>() == "here", e?.Data?.ToJsonString());
        if (e is not { Ports.Count: 1 }) return;
        var made = e.Ports[0];
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        made.Message += (_, m) => answer.TrySetResult(m.Data?.GetValue<string>());
        made.Start();
        made.PostMessage("ping");
        var said = await Within(answer.Task);
        Check("the child's port is entangled with its other end there", said == "child-made:ping", said);
    });

    await Step("child <-> page", async () =>
    {
        Check("page preload ready", await Completes(pageReady.Task, TimeSpan.FromSeconds(30)));
        var channel = new MessageChannelMain();
        window!.WebContents.PostMessage("child-port", null, [channel.Port1]);
        echo!.Process.PostMessage(new JsonObject { ["cmd"] = "page" }, [channel.Port2]);
        var reply = await Within(echo.Next(m => m?["pageSaid"] is not null));
        Check("child and page talk over a port", reply?["pageSaid"]?.GetValue<string>() == "page:hello-page", reply?.ToJsonString());
    });

    await Step("dead process closes its ports", async () =>
    {
        var holder = Fork();
        var channel = new MessageChannelMain();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Port1.Closed += (_, _) => closed.TrySetResult();
        channel.Port1.Start();
        holder.Process.PostMessage(new JsonObject { ["cmd"] = "port" }, [channel.Port2]);
        await Within(holder.Next(m => m?.ToJsonString() == "\"port-ready\""));
        Check("kill returns true", holder.Process.Kill());
        Check("a killed child's port closes in main", await Completes(closed.Task));
        var code = await Within(holder.Exited.Task);
        Check("kill gives exit code 0", holder.Exited.Task.IsCompleted && code == 0, code);
        Check("pid is null in the exit handler", holder.Exited.Task.IsCompleted && holder.PidAtExit is null, holder.PidAtExit);
        Check("kill after exit returns false", !holder.Process.Kill());
        Check("no error event for a kill", holder.Error is null);
        echo!.Process.PostMessage(new JsonObject { ["cmd"] = "echo", ["v"] = "still" });
        Check("other children carry on", (await Within(echo.Next(m => m?["echo"] is not null)))?["echo"]?.GetValue<string>() == "still");
    });

    await Step("exit code", async () =>
    {
        var quitter = Fork();
        await Completes(quitter.Spawned.Task);
        quitter.Process.PostMessage(new JsonObject { ["cmd"] = "exit", ["code"] = 7 });
        var code = await Within(quitter.Exited.Task);
        Check("Environment.Exit(7) gives exit code 7", quitter.Exited.Task.IsCompleted && code == 7, code);
        // As Electron's: an exited child's postMessage returns before it moves (neuters) any port.
        var unsent = new MessageChannelMain();
        quitter.Process.PostMessage(new JsonObject { ["cmd"] = "echo" }, [unsent.Port2]);
        try
        {
            quitter.Process.PostMessage(new JsonObject { ["cmd"] = "echo" }, [unsent.Port2]);
            Check("postMessage after exit moves no port", true);
        }
        catch (InvalidOperationException e) { Check("postMessage after exit moves no port", false, e.Message); }
    });

    await Step("messages before exit", async () =>
    {
        var flooder = Fork();
        await Completes(flooder.Spawned.Task);
        flooder.Process.PostMessage(new JsonObject { ["cmd"] = "flood" });
        var code = await Within(flooder.Exited.Task, TimeSpan.FromSeconds(20));
        Check("messages posted just before exiting all arrive before exit", flooder.Exited.Task.IsCompleted && code == 3 && flooder.MessagesAtExit == 301,
            $"code {code}, {flooder.MessagesAtExit} of 301 before exit");
    });

    await Step("crash", async () =>
    {
        var crasher = Fork();
        await Completes(crasher.Spawned.Task);
        crasher.Process.PostMessage(new JsonObject { ["cmd"] = "throw" });
        var code = await Within(crasher.Exited.Task, TimeSpan.FromSeconds(20));
        var expected = OperatingSystem.IsWindows() ? unchecked((int)0xE0434352) : 128 + 6;
        Check("unhandled exception raises error FatalError", crasher.Error is { Type: "FatalError" } err && err.Report.Contains("child exploded", StringComparison.Ordinal)
            && err.Location.Length > 0, crasher.Error?.Location);
        Check("error comes before exit", crasher.Order.SequenceEqual(["spawn", "error", "exit"]), string.Join(",", crasher.Order));
        Check("crash exit code is the runtime's", crasher.Exited.Task.IsCompleted && code == expected, code);

        var big = Fork();
        await Completes(big.Spawned.Task);
        big.Process.PostMessage(new JsonObject { ["cmd"] = "throw-big" });
        await Within(big.Exited.Task, TimeSpan.FromSeconds(20));
        Check("a large error report still comes before exit", big.Order.SequenceEqual(["spawn", "error", "exit"]) && big.Error?.Report.Length > 8_000_000,
            $"{string.Join(",", big.Order)}, report {big.Error?.Report.Length}");
    });

    await Step("env, cwd, args", async () =>
    {
        Environment.SetEnvironmentVariable("UT_PARENT_VAR", "inherited");
        var cwd = Directory.CreateTempSubdirectory("oriel-ut-").FullName;
        var info = Fork(["info", "a b", "c\"d", ""], new UtilityProcessOptions { Cwd = cwd, Env = new Dictionary<string, string> { ["UT_VAR"] = "v1" } });
        var report = await Within(info.Next(m => m?["args"] is not null));
        var args = report?["args"]?.AsArray().Select(a => a?.GetValue<string>()).ToArray() ?? [];
        Check("args reach the child", args.SequenceEqual(["info", "a b", "c\"d", ""]), report?["args"]?.ToJsonString());
        Check("env reaches the child", report?["var"]?.GetValue<string>() == "v1", report?["var"]);
        Check("a given env replaces the parent's", report?["parentVar"] is null, report?["parentVar"]);
        Check("the child clears Oriel's channel variables", report?["pipeVarCleared"]?.GetValue<bool>() == true);
        var childCwd = report?["cwd"]?.GetValue<string>()?.TrimEnd(Path.DirectorySeparatorChar);
        // macOS reports /private/var for /var.
        Check("cwd reaches the child", childCwd is not null && (string.Equals(childCwd, cwd.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || childCwd == "/private" + cwd.TrimEnd('/')), $"{childCwd} vs {cwd}");
        info.Process.Kill();

        var inherit = Fork(["info"]);
        report = await Within(inherit.Next(m => m?["args"] is not null));
        Check("no env inherits the parent's", report?["parentVar"]?.GetValue<string>() == "inherited", report?["parentVar"]);
        inherit.Process.Kill();
        await Within(inherit.Exited.Task);
        await Within(info.Exited.Task);
    });

    await Step("stdio", async () =>
    {
        var piped = Fork(["print"], new UtilityProcessOptions { Stdio = "pipe" });
        var stdout = piped.Process.Stdout;
        var stderr = piped.Process.Stderr;
        Check("stdio pipe gives streams at fork", stdout is not null && stderr is not null);
        await Within(piped.Next(m => m?.ToJsonString() == "\"printed\""));
        var outLine = stdout is null ? null : await Within(new StreamReader(stdout).ReadLineAsync());
        var errLine = stderr is null ? null : await Within(new StreamReader(stderr).ReadLineAsync());
        Check("stdout is piped", outLine == "out-line", outLine);
        Check("stderr is piped", errLine == "err-line", errLine);
        piped.Process.Kill();
        await Within(piped.Exited.Task);
        Check("streams are still set in the exit handler", piped.StdoutAtExit is not null);
        Check("streams are null after exit", piped.Process.Stdout is null && piped.Process.Stderr is null);

        var ignored = Fork(["print"], new UtilityProcessOptions { Stdio = "ignore" });
        Check("stdio ignore gives no streams", ignored.Process.Stdout is null && ignored.Process.Stderr is null);
        Check("an ignoring child still runs", await Within(ignored.Next(m => m?.ToJsonString() == "\"printed\"")) is not null);
        ignored.Process.Kill();

        var mixed = Fork(["print"], new UtilityProcessOptions { Stdio = new[] { "ignore", "pipe", "inherit" } });
        Check("array form pipes only stdout", mixed.Process.Stdout is not null && mixed.Process.Stderr is null);
        await Within(mixed.Next(m => m?.ToJsonString() == "\"printed\""));
        mixed.Process.Kill();
        await Within(ignored.Exited.Task);
        await Within(mixed.Exited.Task);
    });

    await Step("spawn failure", async () =>
    {
        var missing = Fork(module: Path.Combine(AppContext.BaseDirectory, "no-such-module.dll"));
        var code = await Within(missing.Exited.Task);
        Check("a module that cannot start exits with code 1", missing.Exited.Task.IsCompleted && code == 1, code);
        Check("and raises no spawn", !missing.Spawned.Task.IsCompleted);
        Check("and pid stays null", missing.Process.Pid is null);
    });

    await Step("quit", async () =>
    {
        var spinner = Fork(["spin"]);
        var hello = await Within(spinner.Next(m => m?["pid"] is not null));
        quitChildPid = hello?["pid"]?.GetValue<int>();
        Check("pid matches the child's own", quitChildPid is not null && quitChildPid == spinner.Process.Pid, $"{quitChildPid} vs {spinner.Process.Pid}");
    });

    // Manual probe: kill this process during the pause; its children must end on their own.
    if (args.Contains("--linger")) await Task.Delay(TimeSpan.FromSeconds(60));
    echo?.Process.Kill();
    finished = true;
    window?.Close();
}

app.Ready += (_, _) =>
{
    var web = Path.Combine(AppContext.BaseDirectory, "web");
    window = new BrowserWindow(new BrowserWindowOptions
    {
        Title = "Oriel utility process test",
        WebPreferences = new WebPreferences { Preload = Path.Combine(web, "preload.js") },
    });
    window.LoadUrl(new Uri(Path.Combine(web, "index.html")).AbsoluteUri);
    _ = Task.Run(Drive);
    _ = Task.Delay(TimeSpan.FromSeconds(180)).ContinueWith(_ =>
    {
        if (finished) return;
        Console.WriteLine("FAIL  the run did not finish within 180s");
        failures++;
        window.Close();
    }, TaskScheduler.Default);
};

app.Run();

// Electron kills utility processes when the app quits.
if (quitChildPid is { } pid)
{
    bool gone;
    try { gone = Process.GetProcessById(pid).WaitForExit(5000); }
    catch (ArgumentException) { gone = true; }
    Check("a utility process ends when the app quits", gone, pid);
}
else Check("a utility process ends when the app quits", false, "no child pid");

if (!finished) failures = failures == 0 ? -1 : failures;
Console.WriteLine($"RESULT: {failures} failure(s)");
return failures;

/// <summary>A forked child with its events recorded.</summary>
sealed class Watched
{
    private readonly Channel<JsonNode?> _messages = Channel.CreateUnbounded<JsonNode?>();

    public Watched(UtilityProcess process)
    {
        Process = process;
        process.Spawn += (_, _) =>
        {
            Order.Add("spawn");
            PidAtSpawn = process.Pid;
            Spawned.TrySetResult();
        };
        process.Message += (_, m) =>
        {
            Messages++;
            _messages.Writer.TryWrite(m);
        };
        process.Error += (_, e) =>
        {
            Order.Add("error");
            Error = e;
        };
        process.Exit += (_, e) =>
        {
            Order.Add("exit");
            PidAtExit = process.Pid;
            MessagesAtExit = Messages;
            StdoutAtExit = process.Stdout;
            Exited.TrySetResult(e.Code);
        };
    }

    public UtilityProcess Process { get; }
    public TaskCompletionSource Spawned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Order { get; } = [];
    public int? PidAtSpawn { get; private set; }
    public int? PidAtExit { get; private set; }
    public int Messages { get; private set; }
    public int MessagesAtExit { get; private set; }
    public Stream? StdoutAtExit { get; private set; }
    public UtilityProcessErrorEventArgs? Error { get; private set; }

    /// <summary>The next message matching <paramref name="match"/>, others dropped; null after 10 s.</summary>
    public async Task<JsonNode?> Next(Func<JsonNode?, bool> match)
    {
        // Cancelled rather than abandoned, so a late message is not swallowed by a read nobody awaits.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var message = await _messages.Reader.ReadAsync(timeout.Token);
                if (Matches(match, message)) return message;
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    // A message of another shape (a string where an object is indexed) is just not a match.
    private static bool Matches(Func<JsonNode?, bool> match, JsonNode? message)
    {
        try { return match(message); }
        catch (InvalidOperationException) { return false; }
    }
}
