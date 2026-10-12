using System.Diagnostics;
using System.Text.Json.Nodes;
using Oriel;

/// <summary><c>name</c> and <c>windowStatePersistence</c>: each restart is a child process of this exe with its own
/// userData, so the state crosses a real restart through <c>Local State</c>. Clamping onto a smaller display is left to
/// the unit tests: Xvfb has one fixed display.</summary>
static class WindowStatePersistenceChecks
{
    internal const string Mode = "persist";
    internal delegate void CheckFn(string name, bool pass, object? detail = null);

    private const int DefaultWidth = 400, DefaultHeight = 300;
    private static readonly Rectangle Moved = new(60, 70, 520, 410);

    internal static async Task RunAsync(CheckFn check, TimeSpan step)
    {
        await NameAsync(check, step);
        if (!OperatingSystem.IsLinux()) return;

        // Bounds, saved 200 ms after the move.
        var dir = NewUserData();
        var saved = await Child(dir, "save-now", "on");
        var entry = Entry(dir, "persisted");
        var savedBounds = BoundsOf(saved);
        check("Persistence: Local State keeps windowStates.<name> with Electron's eleven keys",
            entry is not null && entry.Select(p => p.Key).Order().SequenceEqual(
                ["bottom", "fullscreen", "kiosk", "left", "maximized", "right", "top", "workAreaBottom", "workAreaLeft", "workAreaRight", "workAreaTop"]),
            entry?.ToJsonString());
        var workArea = Screen.GetPrimaryDisplay().WorkArea;
        check("Persistence: the entry has the window's edges and its display's work area",
            savedBounds is { } b && entry is not null && Int(entry, "left") == b.X && Int(entry, "top") == b.Y
            && Int(entry, "right") == b.X + b.Width && Int(entry, "bottom") == b.Y + b.Height
            && Int(entry, "workAreaLeft") == workArea.X && Int(entry, "workAreaTop") == workArea.Y
            && Int(entry, "workAreaRight") == workArea.X + workArea.Width && Int(entry, "workAreaBottom") == workArea.Y + workArea.Height
            && entry["maximized"]?.GetValue<bool>() == false,
            $"{saved} {entry?.ToJsonString()} wa={workArea}");
        var loaded = await Child(dir, "load", "on");
        check("Persistence: the bounds come back on the next run, and persisted-state-restored is raised",
            savedBounds is not null && BoundsOf(loaded) == savedBounds && loaded.Contains("restored=True") && loaded.Contains("max=False"), $"{saved} -> {loaded}");

        // Maximized, over the normal bounds saved before it; destroyed within the 200 ms, so only the save on close keeps it.
        dir = NewUserData();
        saved = await Child(dir, "save-max", "on");
        entry = Entry(dir, "persisted");
        savedBounds = BoundsOf(saved);
        check("Persistence: a maximized window saves maximized and keeps its normal bounds",
            entry?["maximized"]?.GetValue<bool>() == true && savedBounds is { } n && Int(entry, "left") == n.X && Int(entry, "right") == n.X + n.Width,
            $"{saved} {entry?.ToJsonString()}");
        loaded = await Child(dir, "load", "on");
        // X11Window maximizes again once mapped and takes the restore bounds from where the WM put the window; Xfwm4
        // sometimes has moved it by then, so only the size is sure to survive there (as in Electron, which shares that code).
        var positionKept = Environment.GetEnvironmentVariable("ORIEL_WM") != "xfwm4";
        check("Persistence: the window comes back maximized over its saved normal bounds",
            loaded.Contains("max=True") && NormalOf(loaded) is { } normal && savedBounds is { } s
            && normal.Width == s.Width && normal.Height == s.Height && (!positionKept || (normal.X == s.X && normal.Y == s.Y)),
            $"{saved} -> {loaded}");
        // Every run saves again on creation, so each load below starts from a fresh maximized save.
        DeleteUserData(dir);
        dir = NewUserData();
        await Child(dir, "save-max", "on");
        loaded = await Child(dir, "load", "bounds");
        check("Persistence: displayMode false restores the bounds but not maximized",
            loaded.Contains("max=False") && BoundsOf(loaded) == savedBounds, $"{saved} -> {loaded}");
        DeleteUserData(dir);
        dir = NewUserData();
        await Child(dir, "save-max", "on");
        loaded = await Child(dir, "load", "mode");
        check("Persistence: bounds false restores maximized but not the bounds",
            loaded.Contains("max=True") && NormalOf(loaded) is { Width: DefaultWidth, Height: DefaultHeight }, $"{saved} -> {loaded}");

        // clearPersistedState.
        var cleared = await Child(dir, "clear", "on");
        check("Persistence: ClearPersistedState removes the entry", cleared.Contains("CLEARED") && Entry(dir, "persisted") is null && Entries(dir) is not null,
            $"{cleared} {Entries(dir)?.ToJsonString()}");
        loaded = await Child(dir, "load", "on");
        check("Persistence: after ClearPersistedState the window opens at its own bounds, nothing restored",
            BoundsOf(loaded) is { Width: DefaultWidth, Height: DefaultHeight } && loaded.Contains("restored=False") && loaded.Contains("max=False"), loaded);
        DeleteUserData(dir);

        // No name, or persistence off: nothing saved.
        dir = NewUserData();
        saved = await Child(dir, "save-now", "noname");
        var off = await Child(dir, "save-now", "off");
        check("Persistence: nothing is saved without a name, or with persistence off; the nameless one warns, as Electron logs",
            saved.Contains("BOUNDS") && saved.Contains("WARNED") && !off.Contains("WARNED") && File.Exists(Path.Combine(dir, "Local State")) && Entries(dir) is null or { Count: 0 }, $"{saved} {Entries(dir)?.ToJsonString()}");
        DeleteUserData(dir);
    }

    private static async Task NameAsync(CheckFn check, TimeSpan step)
    {
        var first = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: named", Name = "named", Show = false });
        string? error = null;
        try { _ = new BrowserWindow(new BrowserWindowOptions { Name = "named", Show = false }); }
        catch (ArgumentException e) { error = e.Message; }
        check("Name: a name in use throws Electron's error",
            error == "Window name 'named' is already in use. Window names must be unique.", error);
        using (var closed = new ManualResetEventSlim())
        {
            first.Closed += (_, _) => closed.Set();
            first.Destroy();
            closed.Wait(step);
        }
        BrowserWindow? again = null;
        error = null;
        try { again = new BrowserWindow(new BrowserWindowOptions { Name = "named", Show = false }); }
        catch (ArgumentException e) { error = e.Message; }
        check("Name: free again once the window is destroyed", again is not null && again.Name == "named", error);

        // A window refused for its other options must not keep the name it would have taken.
        string? pageError = null, iconError = null;
        try { _ = new BrowserWindow(new BrowserWindowOptions { Name = "refused-page", Show = false, WebContents = again?.WebContents }); }
        catch (NotSupportedException e) { pageError = e.Message; }
        check("Name: a window refused for a page it cannot adopt leaves its name free",
            pageError is not null && NameIsFree("refused-page"), pageError);
        try { _ = new BrowserWindow(new BrowserWindowOptions { Name = "refused-icon", Show = false, Icon = Path.Combine(Path.GetTempPath(), "oriel-no-such-icon.png") }); }
        catch (IOException e) { iconError = e.Message; }
        check("Name: a window refused for a missing icon file leaves its name free",
            iconError is not null && NameIsFree("refused-icon"), iconError);
        again?.Destroy();

        // Electron keeps restore_display_mode_ without a name, and with it skips the fullscreen option.
        var full = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: no-name persistence", WindowStatePersistence = true, FullScreen = true, Width = 300, Height = 200 });
        await Task.Delay(1500);
        check("Persistence: on without a name, the fullscreen option is still ignored, as Electron's", !full.IsFullScreen, full.IsFullScreen);
        full.Destroy();
        await Task.Delay(300);
    }

    // The child prints one result line; the last line it writes is what the checks read.
    private static async Task<string> Child(string userData, string action, string spec)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--oriel-process-child", Mode, userData, action, spec }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var errors = child.StandardError.ReadToEndAsync();
        var output = child.StandardOutput.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            return "TIMEOUT";
        }
        var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var warned = (await errors).Contains("Window state persistence enabled but no window name provided.", StringComparison.Ordinal) ? " WARNED" : "";
        return (lines.LastOrDefault(l => l.StartsWith("BOUNDS", StringComparison.Ordinal) || l.StartsWith("CLEARED", StringComparison.Ordinal)) is { } result ? result + warned : null) ?? $"exit={child.ExitCode} {string.Join('|', lines)} {string.Join('|', (await errors).Split('\n').Where(l => l.Contains("Oriel") || l.Contains("FATAL") || l.Contains("Check failed") || l.Contains("Exception") || l.Contains("   at ")).Take(12))}";
    }

    internal static int ChildMain(string[] args)
    {
        var (userData, action, spec) = (args[0], args[1], args[2]);
        var app = OrielApp.Create("Oriel.WindowTest");
        app.SetPath("userData", userData);
        app.WindowAllClosed += (_, _) => app.Quit();
        app.Ready += (_, _) =>
        {
            if (action == "clear")
            {
                // Off Ready: a quit asked inside it comes before the message loop runs, and is lost.
                _ = Task.Run(() =>
                {
                    BrowserWindow.ClearPersistedState("persisted");
                    Console.WriteLine("CLEARED");
                    app.Quit();
                });
                return;
            }
            var options = new BrowserWindowOptions { Title = "Oriel window test: persisted", Width = DefaultWidth, Height = DefaultHeight };
            options = spec switch
            {
                "on" => options with { Name = "persisted", WindowStatePersistence = true },
                "bounds" => options with { Name = "persisted", WindowStatePersistence = new WindowStatePersistence { DisplayMode = false } },
                "mode" => options with { Name = "persisted", WindowStatePersistence = new WindowStatePersistence { Bounds = false } },
                "noname" => options with { WindowStatePersistence = true },
                _ => options with { Name = "persisted" },
            };
            var win = new BrowserWindow(options);
            var restored = false;
            win.PersistedStateRestored += (_, _) => restored = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                if (action == "load")
                    Console.WriteLine($"BOUNDS {Format(win.GetBounds())} normal={Format(win.GetNormalBounds())} max={win.IsMaximized} restored={restored}");
                else
                {
                    win.SetBounds(Moved);
                    if (action == "save-max")
                    {
                        await Task.Delay(800);
                        var normal = win.GetBounds();
                        using var maximized = new ManualResetEventSlim();
                        win.Maximized += (_, _) => maximized.Set();
                        win.Maximize();
                        maximized.Wait(TimeSpan.FromSeconds(5));
                        await Task.Delay(100);
                        Console.WriteLine($"BOUNDS {Format(normal)} max={win.IsMaximized}");
                    }
                    else
                    {
                        await Task.Delay(50);
                        Console.WriteLine($"BOUNDS {Format(win.GetBounds())}");
                    }
                }
                win.Destroy();
            });
        };
        return app.Run();
    }

    private static bool NameIsFree(string name)
    {
        try { new BrowserWindow(new BrowserWindowOptions { Name = name, Show = false }).Destroy(); }
        catch (ArgumentException) { return false; }
        return true;
    }

    private static string Format(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    private static Rectangle? BoundsOf(string line) => Parse(line, "BOUNDS ");

    private static Rectangle? NormalOf(string line) => Parse(line, "normal=");

    private static Rectangle? Parse(string line, string prefix)
    {
        var at = line.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return null;
        var parts = line[(at + prefix.Length)..].Split(' ')[0].Split(',');
        return parts.Length == 4 && parts.All(p => int.TryParse(p, out _))
            ? new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]))
            : null;
    }

    private static JsonObject? Entries(string userData)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(userData, "Local State")))?["windowStates"] as JsonObject; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    private static JsonObject? Entry(string userData, string name) => Entries(userData)?[name] as JsonObject;

    private static int? Int(JsonObject entry, string key) => entry[key]?.GetValue<int>();

    private static string NewUserData() => Directory.CreateTempSubdirectory("oriel-window-state-").FullName;

    private static void DeleteUserData(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
