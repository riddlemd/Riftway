using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Oriel;

// End-to-end CrashReporter check. Without arguments this is the driver: it serves a local crash server and runs
// this exe again per scenario (Start is once per process and CEF reads its cfg at launch), each child crashing a
// renderer or the browser. The exit code is the number of failures.
return args.Length > 0 ? Child.Run(args) : Driver.Run();

/// <summary>One scenario in its own process; reports facts as "R key=value" lines on stdout.</summary>
internal static class Child
{
    private const string ProductName = "OrielCrashTest";

    public static int Run(string[] args)
    {
        var (mode, port, appName) = (args[0], int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args[2]);
        // A priming launch only writes this mode's cfg and reports which handler its own process runs (CR15).
        var prime = mode.StartsWith("prime-", StringComparison.Ordinal);
        if (prime) mode = mode["prime-".Length..];
        var app = OrielApp.Create(appName);
        var url = $"http://127.0.0.1:{port}/submit";

        try { CrashReporter.Start(new CrashReporterStartOptions { ProductName = ProductName }); }
        catch (ArgumentException e) { Report("invalidStart", e.Message); }
        Report("uploadBeforeStart", CrashReporter.GetUploadToServer());

        CrashReporter.AddExtraParameter("removed_key", "gone");
        CrashReporter.Start(new CrashReporterStartOptions
        {
            SubmitURL = url,
            ProductName = ProductName,
            CompanyName = "Oriel Co",
            UploadToServer = mode != "local",
            RateLimit = mode == "ratelimit",
            Extra = new Dictionary<string, string> { ["extra_key"] = "extra-value" },
            GlobalExtra = new Dictionary<string, string> { ["global_key"] = "global-value", ["scenario"] = mode },
        });
        Report("crashDumps", app.GetPath("crashDumps"));
        Report("userData", app.GetPath("userData"));
        var cfg = File.ReadAllText(CfgPath);
        CrashReporter.Start(new CrashReporterStartOptions { SubmitURL = url, ProductName = "Ignored" });
        Report("secondStartIgnored", File.ReadAllText(CfgPath) == cfg);
        CrashReporter.AddExtraParameter("added_key", "added");
        CrashReporter.AddExtraParameter(new string('k', 40), "too long");
        CrashReporter.RemoveExtraParameter("removed_key");
        Report("params", JsonSerializer.Serialize(CrashReporter.GetParameters().OrderBy(p => p.Key, StringComparer.Ordinal)));
        if (prime)
        {
            Report("handler", HandlerCommandLine(appName) ?? "");
            return 0;
        }

        app.Ready += (_, _) => Task.Run(async () =>
        {
            var code = 0;
            BrowserWindow? window = null;
            try { window = await DriveAsync(mode, appName); }
            catch (Exception e) { Report("error", e.GetType().Name + ": " + e.Message.ReplaceLineEndings(" ")); code = 1; }
            // CEF's shutdown waits for open browsers, so the window goes first.
            window?.Destroy();
            await Task.Delay(1000);
            app.Exit(code);
        });
        return app.Run();
    }

    private static async Task<BrowserWindow?> DriveAsync(string mode, string appName)
    {
        Report("uploadToServer", CrashReporter.GetUploadToServer());
        Report("handler", HandlerCommandLine(appName) ?? "");
        if (mode == "disable")
        {
            CrashReporter.SetUploadToServer(false);
            Report("uploadAfterDisable", CrashReporter.GetUploadToServer());
        }
        if (mode == "list")
        {
            ReportList();
            return null;
        }

        var window = new BrowserWindow(new BrowserWindowOptions { Show = false });
        if (mode == "browser")
        {
            await Task.Delay(500);
            _ = window.WebContents.LoadUrl("chrome://inducebrowsercrashforrealz");
            await Task.Delay(15000);
            Report("browserSurvived", true);
            return window;
        }

        var crashes = mode == "renderer" ? 2 : 1;
        for (var i = 1; i <= crashes; i++)
        {
            // A crashed page does not take a second debug URL, so each crash gets a fresh window, opened before the
            // old one goes: closing the last window quits the app.
            if (i > 1)
            {
                var crashed = window;
                window = new BrowserWindow(new BrowserWindowOptions { Show = false });
                crashed.Destroy();
            }
            _ = window.WebContents.LoadUrl("chrome://crash");
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline && !Settled(mode, i)) await Task.Delay(1000);
            Report($"settled{i}", Settled(mode, i));
        }
        // Give the handler's upload thread its turn before reading the outcome.
        if (mode == "disable") await Task.Delay(5000);
        if (mode == "local")
        {
            CrashReporter.SetUploadToServer(true);
            Report("uploadAfterSetTrue", CrashReporter.GetUploadToServer());
            CrashReporter.SetUploadToServer(false);
            Report("uploadAfterSetFalse", CrashReporter.GetUploadToServer());
        }
        ReportList();
        return window;
    }

    // A crash counts once the handler has finished with it: uploaded, or kept locally when uploads are off.
    private static bool Settled(string mode, int count)
    {
        var reports = CrashReporter.GetUploadedReports();
        return mode is "local" or "disable" ? reports.Count >= count : reports.Count(r => r.Id.Length > 0) >= count;
    }

    private static void ReportList()
    {
        var reports = CrashReporter.GetUploadedReports();
        Report("reports", JsonSerializer.Serialize(reports.Select(r => new { r.Id, Date = r.Date.ToUnixTimeSeconds() })));
        var last = CrashReporter.GetLastCrashReport();
        Report("last", last is null ? "null" : last.Id);
    }

    private static void Report(string key, object value)
        => Console.WriteLine($"R {key}={(value is bool b ? (b ? "true" : "false") : value)}");

    // CEF's GetCrashConfigPath: beside the exe, or on macOS in the outermost app bundle's Resources.
    internal static string CfgPath => Path.Combine(AppRoot is { } bundle && OperatingSystem.IsMacOS()
        ? Path.Combine(bundle, "Contents", "Resources") : AppContext.BaseDirectory, "crash_reporter.cfg");

    // The .app on macOS; elsewhere the exe's directory. Every process this app starts runs from under it.
    internal static string? AppRoot
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return AppContext.BaseDirectory;
            var path = AppContext.BaseDirectory;
            var app = path.IndexOf(".app/", StringComparison.Ordinal);
            return app < 0 ? null : path[..(app + 4)];
        }
    }

    // Windows: the subprocess exe (ExternalHandler), or in the bootstrap layout, which has no subprocess exe, the
    // app's own exe. macOS: CHILD_PROCESS_EXE, the "<app> Helper" app. Linux: CHILD_PROCESS_EXE, Oriel.Subprocess.
    internal static string HandlerName()
        => OperatingSystem.IsWindows() && Oriel.Cef.Platform.BootstrapHost.IsHosted ? Path.GetFileNameWithoutExtension(Environment.ProcessPath)!
            : OperatingSystem.IsMacOS() ? Path.GetFileName(Environment.ProcessPath) + " Helper" : "Oriel.Subprocess";

    private static string? HandlerCommandLine(string appName)
        => Native.CommandLines().Select(p => p.Line).FirstOrDefault(line => line.Contains("--type=crashpad-handler", StringComparison.Ordinal)
            && line.Contains(appName, StringComparison.Ordinal));
}

/// <summary>Runs the scenarios against a local crash server and checks what arrived.</summary>
internal static class Driver
{
    private static int s_failures;
    private static readonly string s_runId = $"{Environment.ProcessId}-{DateTime.UtcNow:HHmmss}";
    private static readonly List<string> s_appNames = [];

    private static void Check(string name, bool pass, object? detail = null)
    {
        if (!pass) s_failures++;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}  [{detail}]");
    }

    public static int Run()
    {
        using var server = new UploadServer();
        var cfgPath = Child.CfgPath;
        try
        {
            Scenario("renderer", () => Renderer(server, cfgPath));
            Scenario("local", () => Local(server));
            Scenario("disable", () => Disable(server));
            Scenario("ratelimit", () => RateLimit(server));
            Scenario("browser", () => Browser(server));
        }
        finally
        {
            Cleanup(cfgPath);
        }
        Console.WriteLine($"{s_failures} failure(s)");
        return s_failures;
    }

    private static void Scenario(string name, Action run)
    {
        try { run(); }
        catch (Exception e) { Check($"{name}: scenario ran to the end", false, e.ToString().ReplaceLineEndings(" ")); }
    }

    private static void Renderer(UploadServer server, string cfgPath)
    {
        var (exit, facts) = RunChild("renderer", server.Port, out var appName);
        Check("renderer: child exited cleanly", exit == 0, exit);
        Check("start without submitURL throws Electron's error",
            Fact(facts, "invalidStart") == "submitURL must be specified when uploadToServer is true", Fact(facts, "invalidStart"));
        Check("getUploadToServer is false before start", Fact(facts, "uploadBeforeStart") == "false");
        Check("a second start is ignored", Fact(facts, "secondStartIgnored") == "true");
        Check("getParameters: extra and added keys, removed and over-long keys gone",
            Fact(facts, "params") == """[{"Key":"added_key","Value":"added"},{"Key":"extra_key","Value":"extra-value"}]""",
            Fact(facts, "params"));
        Check("getUploadToServer is true after start", Fact(facts, "uploadToServer") == "true");
        var handler = Fact(facts, "handler") ?? "";
        Check("the handler is the subprocess exe with this app's database",
            handler.Contains(HandlerExe, StringComparison.Ordinal) && handler.Contains(DatabaseArgument(appName, facts), StringComparison.Ordinal),
            $"{handler} (want {HandlerExe}, {DatabaseArgument(appName, facts)})");
        Check("rateLimit false: the handler does not rate limit", handler.Contains("--no-rate-limit", StringComparison.Ordinal), handler);
        Check("database pruning is Crashpad's default (128 MB, 365 days)",
            handler.Contains("--max-db-size=128", StringComparison.Ordinal) && handler.Contains("--max-db-age=365", StringComparison.Ordinal));

        var uploads = server.WaitFor("renderer", 2, TimeSpan.FromSeconds(10));
        Check("rateLimit false: both renderer crashes were uploaded", uploads.Count == 2, uploads.Count);
        var upload = uploads.FirstOrDefault();
        Check("upload carries a minidump as upload_file_minidump",
            upload?.Minidump is { Length: > 4 } dump && Encoding.ASCII.GetString(dump, 0, 4) == "MDMP", upload?.Minidump?.Length);
        Check("compress true: the upload is gzipped", upload?.Gzip == true);
        Check("_productName is productName", Field(upload, "_productName") == "OrielCrashTest", Field(upload, "_productName"));
        Check("_version is the app version", Field(upload, "_version") == "1.2.3", Field(upload, "_version"));
        Check("companyName becomes _companyName", Field(upload, "_companyName") == "Oriel Co", Field(upload, "_companyName"));
        Check("globalExtra reaches renderer reports", Field(upload, "global_key") == "global-value", Field(upload, "global_key"));
        Check("extra is not inherited by the renderer", upload is not null && Field(upload, "extra_key") is null, Field(upload, "extra_key"));
        Check("process_type is renderer", Field(upload, "process_type") == "renderer", Field(upload, "process_type"));
        Check($"platform is {Platform}", Field(upload, "platform") == Platform, Field(upload, "platform"));
        Check("CEF's product annotation is productName", Field(upload, "product") == "OrielCrashTest", Field(upload, "product"));
        Check("guid is sent", Field(upload, "guid") is { Length: > 0 }, Field(upload, "guid"));
        if (upload is not null) Console.WriteLine("      fields: " + string.Join(", ", upload.Fields.Select(f => $"{f.Key}={Shorten(f.Value)}")));

        var reports = Reports(facts);
        var ids = uploads.Select(u => u.ResponseId).ToHashSet();
        Check("getUploadedReports lists both uploads by the server's ids",
            reports.Count == 2 && reports.All(r => ids.Contains(r.Id)), Fact(facts, "reports"));
        Check("report dates are upload times", reports.Count > 0 && reports.All(r => Math.Abs(r.Date - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 600));
        Check("getLastCrashReport is the latest upload", uploads.Count > 0 && Fact(facts, "last") == uploads[^1].ResponseId, Fact(facts, "last"));
        var cfg = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : "";
        Check("a key added after start is declared for the next launch", cfg.Contains("\nadded_key=large\n", StringComparison.Ordinal));
    }

    private static void Local(UploadServer server)
    {
        var (exit, facts) = RunChild("local", server.Port, out _);
        Check("local: child exited cleanly", exit == 0, exit);
        Check("uploadToServer false: getUploadToServer is false", Fact(facts, "uploadToServer") == "false");
        Check("setUploadToServer(true) is read back", Fact(facts, "uploadAfterSetTrue") == "true");
        Check("setUploadToServer(false) is read back", Fact(facts, "uploadAfterSetFalse") == "false");
        var reports = Reports(facts);
        Check("uploadToServer false: the report stays in the database, not uploaded",
            reports.Count == 1 && reports[0] is { Id: "", Date: 0 }, Fact(facts, "reports"));
        Check("getLastCrashReport returns the local report (epoch, no id), as Electron's sort does", Fact(facts, "last") == "");
        var uploads = server.WaitFor("local", 1, TimeSpan.FromSeconds(5));
        Check("uploadToServer false: nothing reached the server", uploads.Count == 0, uploads.Count);
    }

    private static void Disable(UploadServer server)
    {
        var (exit, facts) = RunChild("disable", server.Port, out _);
        Check("disable: child exited cleanly", exit == 0, exit);
        Check("setUploadToServer(false) after start is read back", Fact(facts, "uploadAfterDisable") == "false");
        var reports = Reports(facts);
        Check("setUploadToServer(false): the crash is kept, not uploaded",
            reports.Count == 1 && reports[0] is { Id: "", Date: 0 }, Fact(facts, "reports"));
        var uploads = server.WaitFor("disable", 1, TimeSpan.FromSeconds(5));
        Check("setUploadToServer(false): nothing reached the server", uploads.Count == 0, uploads.Count);
    }

    private static void RateLimit(UploadServer server)
    {
        var (exit, facts) = RunChild("ratelimit", server.Port, out _);
        Check("ratelimit: child exited cleanly", exit == 0, exit);
        var handler = Fact(facts, "handler") ?? "";
        Check("rateLimit true: the handler limits uploads per day",
            handler.Contains("--max-uploads=24", StringComparison.Ordinal) && !handler.Contains("--no-rate-limit", StringComparison.Ordinal), handler);
        var uploads = server.WaitFor("ratelimit", 1, TimeSpan.FromSeconds(10));
        Check("rateLimit true: the first crash is still uploaded", uploads.Count == 1, uploads.Count);
    }

    private static void Browser(UploadServer server)
    {
        var (exit, facts) = RunChild("browser", server.Port, out var appName);
        Check("browser: the main process crashed", exit != 0 && Fact(facts, "browserSurvived") is null, $"exit={exit}");
        var uploads = server.WaitFor("browser", 1, TimeSpan.FromSeconds(30));
        var upload = uploads.FirstOrDefault();
        Check("browser crash was uploaded", upload is not null, uploads.Count);
        Check("browser report: process_type is browser", Field(upload, "process_type") == "browser", Field(upload, "process_type"));
        Check("browser report carries extra", Field(upload, "extra_key") == "extra-value", Field(upload, "extra_key"));
        Check("browser report carries globalExtra", Field(upload, "global_key") == "global-value", Field(upload, "global_key"));
        Check("browser report carries _productName", Field(upload, "_productName") == "OrielCrashTest", Field(upload, "_productName"));

        var (listExit, list) = RunChild("list", server.Port, out _, appName);
        Check("list: child exited cleanly", listExit == 0, listExit);
        var reports = Reports(list);
        Check("a later launch lists the browser crash", upload is not null && reports.Any(r => r.Id == upload.ResponseId), Fact(list, "reports"));
    }

    // Electron's platform crash key (shell/common/crash_keys.cc SetPlatformCrashKey).
    private static string Platform => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";

    private static string HandlerExe => OperatingSystem.IsWindows() ? Child.HandlerName() + ".exe"
        : OperatingSystem.IsMacOS() ? $"/{Child.HandlerName()}.app/Contents/MacOS/{Child.HandlerName()} " : "/Oriel.Subprocess ";

    // Windows: CEF's AppName folder (CR3). macOS and Linux: Electron's app.getPath('crashDumps').
    private static string DatabaseArgument(string appName, Dictionary<string, string> facts)
        => OperatingSystem.IsWindows() ? $@"\{appName}\User Data\Crashpad" : "--database=" + Fact(facts, "crashDumps");

    // Electron starts Crashpad in crashReporter.start; in the bootstrap layout CEF's bootstrap.exe starts it from the cfg
    // beside the exe before .NET runs, so a scenario's settings reach its own process only from the next launch (CR15).
    private static bool StartAppliesNextLaunch => OperatingSystem.IsWindows() && Oriel.Cef.Platform.BootstrapHost.IsHosted;

    private static (int Exit, Dictionary<string, string> Facts) RunChild(string mode, int port, out string appName, string? reuse = null)
    {
        appName = reuse ?? $"Oriel.CrashTest.{mode}.{s_runId}";
        if (reuse is null) s_appNames.Add(appName);
        if (reuse is null && StartAppliesNextLaunch)
        {
            var (primeExit, primed) = Launch("prime-" + mode, port, appName);
            var primeHandler = Fact(primed, "handler") ?? "";
            Check($"{mode}: bootstrap layout: the launch that calls start runs no handler for its own database (CR15)",
                primeExit == 0 && !primeHandler.Contains($@"\{appName}\User Data\Crashpad", StringComparison.Ordinal), $"exit={primeExit} {primeHandler}");
            Remember(primed);
        }
        var launched = Launch(mode, port, appName);
        Remember(launched.Facts);
        return launched;
    }

    private static readonly HashSet<string> s_userData = [];

    private static void Remember(Dictionary<string, string> facts)
    {
        if (Fact(facts, "userData") is { Length: > 0 } userData) s_userData.Add(userData);
    }

    private static (int Exit, Dictionary<string, string> Facts) Launch(string mode, int port, string appName)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, [mode, port.ToString(System.Globalization.CultureInfo.InvariantCulture), appName])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var facts = new Dictionary<string, string>();
        // Crashpad logs a missing attachments folder every time the database is listed; it is not a failure.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } && !e.Data.Contains("filesystem_win.cc", StringComparison.Ordinal)) Console.WriteLine($"   [{mode} err] {e.Data}");
        };
        process.BeginErrorReadLine();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            Console.WriteLine($"   [{mode}] timed out (killed)");
        }
        process.WaitForExit();
        foreach (var line in stdout.Result.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length > 0) Console.WriteLine($"   [{mode}] {Shorten(text, 300)}");
            if (!text.StartsWith("R ", StringComparison.Ordinal)) continue;
            var equals = text.IndexOf('=', StringComparison.Ordinal);
            if (equals > 2) facts[text[2..equals]] = text[(equals + 1)..];
        }
        return (process.ExitCode, facts);
    }

    private static string? Fact(Dictionary<string, string> facts, string key) => facts.TryGetValue(key, out var value) ? value : null;

    private static string? Field(Upload? upload, string key) => upload is not null && upload.Fields.TryGetValue(key, out var value) ? value : null;

    private static List<(string Id, long Date)> Reports(Dictionary<string, string> facts)
    {
        if (Fact(facts, "reports") is not { } json) return [];
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(e => (e.GetProperty("Id").GetString()!, e.GetProperty("Date").GetInt64())).ToList();
    }

    private static string Shorten(string value, int max = 60) => value.Length <= max ? value : value[..max] + "...";

    private static void Cleanup(string cfgPath)
    {
        // Crashpad handlers outlive their clients briefly; they hold the databases open.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && OurSubprocesses().Count > 0) Thread.Sleep(500);
        foreach (var process in OurSubprocesses())
        {
            using (process) { try { process.Kill(); process.WaitForExit(5000); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        }
        var leftovers = new List<string>();
        var directories = s_appNames.SelectMany(name => new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }
            .Select(root => Path.Combine(Environment.GetFolderPath(root), name))).Concat(s_userData).Distinct();
        {
            foreach (var directory in directories)
            {
                for (var attempt = 0; attempt < 10 && Directory.Exists(directory); attempt++)
                {
                    try { Directory.Delete(directory, recursive: true); }
                    catch (IOException) { Thread.Sleep(500); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(500); }
                }
                if (Directory.Exists(directory)) leftovers.Add(directory);
            }
        }
        File.Delete(cfgPath);
        Check("cleanup: no crash databases, profiles or handlers remain", leftovers.Count == 0 && OurSubprocesses().Count == 0,
            string.Join("; ", leftovers));
    }

    private static List<Process> OurSubprocesses()
        => !OperatingSystem.IsWindows()
            ? Native.CommandLines().Where(p => p.Pid != Environment.ProcessId && Child.AppRoot is { } root && p.Line.StartsWith(root, StringComparison.Ordinal))
                .Select(p => { try { return Process.GetProcessById(p.Pid); } catch (ArgumentException) { return null; } }).OfType<Process>().ToList()
            : Process.GetProcessesByName(Child.HandlerName())
            .Where(p => p.Id != Environment.ProcessId && (Child.HandlerName() == "Oriel.Subprocess" || Native.CommandLine(p)?.Contains("--type=crashpad-handler", StringComparison.Ordinal) == true))
            .Where(p => { try { return p.MainModule?.FileName?.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) == true; } catch (System.ComponentModel.Win32Exception) { return false; } })
            .ToList();
}

/// <summary>One POST the crash server received.</summary>
internal sealed record Upload(Dictionary<string, string> Fields, byte[]? Minidump, bool Gzip, string ResponseId);

/// <summary>A minimal HTTP/1.1 server on loopback that accepts Crashpad's multipart uploads and answers a report id.</summary>
internal sealed class UploadServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<Upload> _uploads = new();
    private int _count;

    public UploadServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The uploads from <paramref name="scenario"/>, once <paramref name="count"/> have arrived or the wait ends.</summary>
    public List<Upload> WaitFor(string scenario, int count, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        List<Upload> matching;
        while ((matching = _uploads.Where(u => u.Fields.GetValueOrDefault("scenario") == scenario).ToList()).Count < count
               && DateTime.UtcNow < deadline) Thread.Sleep(250);
        return matching;
    }

    public void Dispose() => _listener.Stop();

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var headers = ReadHeaders(stream);
                var body = headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase)
                    ? ReadChunked(stream)
                    : ReadExactly(stream, int.Parse(headers.GetValueOrDefault("content-length", "0"), System.Globalization.CultureInfo.InvariantCulture));
                var gzip = headers.GetValueOrDefault("content-encoding", "").Contains("gzip", StringComparison.OrdinalIgnoreCase);
                if (gzip)
                {
                    using var unzip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
                    using var plain = new MemoryStream();
                    unzip.CopyTo(plain);
                    body = plain.ToArray();
                }
                var id = $"oriel-report-{Interlocked.Increment(ref _count)}";
                var (fields, minidump) = ParseMultipart(body, headers.GetValueOrDefault("content-type", ""));
                _uploads.Enqueue(new Upload(fields, minidump, gzip, id));
                if (Environment.GetEnvironmentVariable("ORIEL_CRASH_DUMPDIR") is { } dumps && minidump is not null) File.WriteAllBytes(Path.Combine(dumps, id + ".dmp"), minidump);
                Console.WriteLine($"   [server] {id}: {minidump?.Length ?? 0}-byte minidump, gzip={gzip}, fields: "
                    + string.Join(", ", fields.Select(f => $"{f.Key}={(f.Value.Length > 40 ? f.Value[..40] + "..." : f.Value)}")));
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {id.Length}\r\nConnection: close\r\n\r\n{id}");
                stream.Write(response);
            }
            catch (Exception e) when (e is IOException or FormatException or InvalidDataException)
            {
                Console.WriteLine($"   [server] bad request: {e.Message}");
            }
        }
    }

    private static Dictionary<string, string> ReadHeaders(NetworkStream stream)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 4 || bytes[^4] != '\r' || bytes[^3] != '\n' || bytes[^2] != '\r' || bytes[^1] != '\n')
        {
            var b = stream.ReadByte();
            if (b < 0) throw new IOException("connection closed in headers");
            bytes.Add((byte)b);
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (headers.GetValueOrDefault("expect", "").Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            stream.Write("HTTP/1.1 100 Continue\r\n\r\n"u8);
        return headers;
    }

    private static byte[] ReadExactly(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static byte[] ReadChunked(NetworkStream stream)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = ReadLine(stream);
            var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
            if (size == 0)
            {
                ReadLine(stream);
                return body.ToArray();
            }
            body.Write(ReadExactly(stream, size));
            ReadLine(stream);
        }
    }

    private static string ReadLine(NetworkStream stream)
    {
        var line = new StringBuilder();
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n') if (b != '\r') line.Append((char)b);
        return line.ToString();
    }

    private static (Dictionary<string, string> Fields, byte[]? Minidump) ParseMultipart(byte[] body, string contentType)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? minidump = null;
        var marker = contentType.IndexOf("boundary=", StringComparison.Ordinal);
        if (marker < 0) return (fields, null);
        var boundary = Encoding.ASCII.GetBytes("--" + contentType[(marker + 9)..].Trim('"'));
        var position = IndexOf(body, boundary, 0);
        while (position >= 0)
        {
            var start = position + boundary.Length + 2;
            var next = IndexOf(body, boundary, start);
            if (next < 0) break;
            var headerEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), start);
            if (headerEnd < 0 || headerEnd > next) break;
            var partHeaders = Encoding.UTF8.GetString(body, start, headerEnd - start);
            var content = body.AsSpan(headerEnd + 4, next - 2 - (headerEnd + 4)).ToArray();
            var nameAt = partHeaders.IndexOf("name=\"", StringComparison.Ordinal);
            if (nameAt >= 0)
            {
                var name = partHeaders[(nameAt + 6)..partHeaders.IndexOf('"', nameAt + 6)];
                if (name == "upload_file_minidump") minidump = content;
                else fields[name] = Encoding.UTF8.GetString(content);
            }
            position = next;
        }
        return (fields, minidump);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        var index = haystack.AsSpan(from).IndexOf(needle);
        return index < 0 ? -1 : from + index;
    }
}

/// <summary>Reads other processes' command lines: ProcessCommandLineInformation on Windows, /proc on Linux, ps on macOS.</summary>
internal static partial class Native
{
    public static List<(int Pid, string Line)> CommandLines()
    {
        var lines = new List<(int, string)>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var process in Process.GetProcesses())
                using (process) if (CommandLine(process) is { } line) lines.Add((process.Id, line));
        }
        else if (OperatingSystem.IsLinux())
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
                try { lines.Add((pid, File.ReadAllText(Path.Combine(directory, "cmdline")).TrimEnd('\0').Replace('\0', ' '))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        else
        {
            using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-axww", "-o", "pid=,command="]) { RedirectStandardOutput = true })!;
            foreach (var raw in ps.StandardOutput.ReadToEnd().Split('\n'))
            {
                var text = raw.TrimStart();
                var space = text.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0 && int.TryParse(text[..space], out var pid)) lines.Add((pid, text[(space + 1)..]));
            }
            ps.WaitForExit();
        }
        return lines;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, [Out] byte[] buffer, int length, out int returned);

    public static string? CommandLine(Process process)
    {
        try
        {
            var buffer = new byte[65536];
            if (NtQueryInformationProcess(process.Handle, 60, buffer, buffer.Length, out _) != 0) return null;
            // A UNICODE_STRING (Length, MaximumLength, padding, Buffer) followed by the text itself.
            var length = BitConverter.ToUInt16(buffer, 0);
            return Encoding.Unicode.GetString(buffer, IntPtr.Size * 2, length);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
