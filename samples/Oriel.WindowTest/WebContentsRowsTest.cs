using Oriel;

/// <summary>webContents methods checked against what the page, DevTools' HTTP endpoint, the process table or the
/// clipboard report on their own.</summary>
static class WebContentsRowsTest
{
    internal delegate void CheckFn(string name, bool pass, object? detail = null);

    internal static async Task RunAsync(CheckFn check, TimeSpan step, int debugPort)
    {
        await ProcessAsync(check);
        await DevToolsTargetAsync(check, debugPort);
        await HeapSnapshotAsync(check);
    }

    // The ids Chromium's own DevTools HTTP endpoint lists, by URL.
    static async Task<Dictionary<string, string>> ListedTargets(int debugPort)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var list = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/list"))!.AsArray();
        var byUrl = new Dictionary<string, string>();
        foreach (var target in list)
            if ((string?)target!["type"] == "page") byUrl[(string)target["url"]!] = (string)target["id"]!;
        return byUrl;
    }

    static async Task DevToolsTargetAsync(CheckFn check, int debugPort)
    {
        var first = new BrowserWindow(new BrowserWindowOptions { Show = false });

        var second = new BrowserWindow(new BrowserWindowOptions { Show = false });
        const string FirstUrl = "data:text/html,target-one", SecondUrl = "data:text/html,target-two";
        await first.WebContents.LoadUrl(FirstUrl);
        await second.WebContents.LoadUrl(SecondUrl);
        var (a, b) = (first.WebContents, second.WebContents);
        var listed = await ListedTargets(debugPort);
        var (idA, idB) = (a.GetOrCreateDevToolsTargetId(), b.GetOrCreateDevToolsTargetId());
        check("GetOrCreateDevToolsTargetId is the id Chromium's remote-debugging endpoint lists for the page",
            idA.Length > 0 && idA != idB && listed.GetValueOrDefault(FirstUrl) == idA && listed.GetValueOrDefault(SecondUrl) == idB,
            $"{idA} {idB} listed={string.Join(",", listed.Where(p => p.Key.Contains("target-")).Select(p => p.Value))}");
        await b.LoadUrl("data:text/html,<p>target-two-again</p>");
        check("the target id stays with the page across navigations", b.GetOrCreateDevToolsTargetId() == idB, b.GetOrCreateDevToolsTargetId());
        check("FromDevToolsTargetId finds each page by its id, and nothing for an unknown id",
            WebContents.FromDevToolsTargetId(idA) == a && WebContents.FromDevToolsTargetId(idB) == b && WebContents.FromDevToolsTargetId("0123456789ABCDEF0123456789ABCDEF") is null);
        var closed = new TaskCompletionSource();
        b.Destroyed += (_, _) => closed.TrySetResult();
        second.Destroy();
        await Task.WhenAny(closed.Task, Task.Delay(5000));
        check("FromDevToolsTargetId finds nothing once the page has closed", WebContents.FromDevToolsTargetId(idB) is null);
        first.Destroy();
    }

    static async Task HeapSnapshotAsync(CheckFn check)
    {
        var win = new BrowserWindow(new BrowserWindowOptions { Show = false });
        var wc = win.WebContents;
        await wc.LoadUrl("data:text/html,<p>heap</p>");
        await wc.ExecuteJavaScriptAsync("window.kept = new (class OrielHeapMarker { constructor() { this.tag = ['oriel', 'heap', 'marker', 6 * 7].join('-'); } })(); 1");
        var dir = Path.Combine(Path.GetTempPath(), "oriel-heap-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "page.heapsnapshot");
        File.WriteAllText(file, "stale");
        string? error = null;
        try { await wc.TakeHeapSnapshotAsync(file).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception e) { error = e.Message; }
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var parsed = error is null ? TryParse(text) : null;
        var strings = parsed?["strings"]?.AsArray().Select(s => (string?)s).ToHashSet() ?? [];
        check("TakeHeapSnapshotAsync writes the renderer's V8 heap as .heapsnapshot JSON, replacing the file, with the page's objects in it",
            error is null && parsed?["snapshot"]?["meta"]?["node_fields"] is not null && (parsed["nodes"]?.AsArray().Count ?? 0) > 1000
            && strings.Contains("OrielHeapMarker") && strings.Contains("oriel-heap-marker-42"),
            $"error={error} length={text.Length} marker={strings.Contains("OrielHeapMarker")}/{strings.Contains("oriel-heap-marker-42")}");
        var both = new[] { wc.TakeHeapSnapshotAsync(Path.Combine(dir, "a.heapsnapshot")), wc.TakeHeapSnapshotAsync(Path.Combine(dir, "b.heapsnapshot")) };
        var all = Task.WhenAll(both);
        var bothDone = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60))) == all;
        check("two snapshots at once each get a whole snapshot of their own",
            bothDone && both.All(t => t.IsCompletedSuccessfully) && new[] { "a", "b" }.All(n => TryParse(File.ReadAllText(Path.Combine(dir, n + ".heapsnapshot")))?["snapshot"] is not null),
            string.Join(" ", both.Select(t => t.Status)));
        var missing = Path.Combine(dir, "no-such-folder", "x.heapsnapshot");
        string? pathError = null;
        try { await wc.TakeHeapSnapshotAsync(missing); }
        catch (InvalidOperationException e) { pathError = e.Message; }
        check("a path that cannot be written rejects with Electron's message", pathError == $"Failed to take heap snapshot with invalid file path {missing}", pathError);
        var crashed = new BrowserWindow(new BrowserWindowOptions { Show = false });
        await crashed.WebContents.LoadUrl("data:text/html,crash");
        var gone = new TaskCompletionSource();
        crashed.WebContents.RenderProcessGone += (_, _) => gone.TrySetResult();
        try { System.Diagnostics.Process.GetProcessById(crashed.WebContents.GetOSProcessId()).Kill(); } catch (Exception) { }
        await Task.WhenAny(gone.Task, Task.Delay(20000));
        check("TakeHeapSnapshotAsync on a crashed page rejects with Electron's message", await Rejection(crashed.WebContents.TakeHeapSnapshotAsync(Path.Combine(dir, "crashed.heapsnapshot")))
            == "Failed to take heap snapshot with nonexistent render frame", gone.Task.IsCompleted);
        crashed.Destroy();
        // The renderer is killed while the snapshot waits behind busy script: the snapshot fails and lets go of its file.
        var busy = new BrowserWindow(new BrowserWindowOptions { Show = false });
        await busy.WebContents.LoadUrl("data:text/html,busy");
        _ = busy.WebContents.ExecuteJavaScriptAsync("{ const end = Date.now() + 5000; while (Date.now() < end); } 1").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await Task.Delay(300);
        var midway = busy.WebContents.TakeHeapSnapshotAsync(Path.Combine(dir, "midway.heapsnapshot"));
        await Task.Delay(300);
        try { System.Diagnostics.Process.GetProcessById(busy.WebContents.GetOSProcessId()).Kill(); } catch (Exception) { }
        var midwayError = await Rejection(midway);
        var released = true;
        try { File.Delete(Path.Combine(dir, "midway.heapsnapshot")); } catch (IOException) { released = false; }
        check("a renderer that goes mid-snapshot fails the snapshot and releases its file", midwayError == "Failed to take heap snapshot" && released, $"{midwayError} released={released}");
        busy.Destroy();
        var closing = new BrowserWindow(new BrowserWindowOptions { Show = false });
        await closing.WebContents.LoadUrl("data:text/html,closing");
        _ = closing.WebContents.ExecuteJavaScriptAsync("{ const end = Date.now() + 5000; while (Date.now() < end); } 1").ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        await Task.Delay(300);
        var cut = closing.WebContents.TakeHeapSnapshotAsync(Path.Combine(dir, "cut.heapsnapshot"));
        await Task.Delay(300);
        closing.Destroy();
        var cutError = await Rejection(cut);
        released = true;
        try { File.Delete(Path.Combine(dir, "cut.heapsnapshot")); } catch (IOException) { released = false; }
        check("a page that closes mid-snapshot fails the snapshot and releases its file", cutError == "Failed to take heap snapshot" && released, $"{cutError} released={released}");
        var closed = new TaskCompletionSource();
        wc.Destroyed += (_, _) => closed.TrySetResult();
        win.Destroy();
        await Task.WhenAny(closed.Task, Task.Delay(5000));
        check("TakeHeapSnapshotAsync on a closed page rejects with Electron's message", await Rejection(wc.TakeHeapSnapshotAsync(Path.Combine(dir, "closed.heapsnapshot")))
            == "Failed to take heap snapshot with invalid webContents main frame");
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    // The task's error message within ten seconds; "pending" or "resolved" otherwise.
    static async Task<string> Rejection(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(10000)) != task) return "pending";
        return task.Exception?.GetBaseException().Message ?? "resolved";
    }
    static System.Text.Json.Nodes.JsonNode? TryParse(string text)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // The pid the process table lists for the renderer launched with this render process id.
    static int? PsRenderer(int processId)
    {
        using var ps = System.Diagnostics.Process.Start(OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"Get-CimInstance Win32_Process -Filter 'ParentProcessId={Environment.ProcessId}' | ForEach-Object {{ '' + $_.ProcessId + ' ' + $_.CommandLine }}\"")
            { RedirectStandardOutput = true, CreateNoWindow = true }
            : new System.Diagnostics.ProcessStartInfo("ps", "-eo pid=,args=") { RedirectStandardOutput = true })!;
        var listing = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return listing.Split('\n').Select(l => l.Trim().Split(' ', 2)).Where(p => p.Length == 2
            && p[1].Split(' ').Contains("--type=renderer") && p[1].Split(' ').Contains($"--renderer-client-id={processId}")).Select(p => (int?)int.Parse(p[0])).FirstOrDefault();
    }

    static async Task ProcessAsync(CheckFn check)
    {
        var first = new BrowserWindow(new BrowserWindowOptions { Show = false });
        var second = new BrowserWindow(new BrowserWindowOptions { Show = false });
        await first.WebContents.LoadUrl("data:text/html,<p>first</p>");
        await second.WebContents.LoadUrl("data:text/html,<p>second</p>");
        var (a, b) = (first.WebContents, second.WebContents);
        check("GetProcessId is the main frame's render process id, another for a page in another renderer",
            a.GetProcessId() > 0 && a.GetProcessId() == a.MainFrame?.ProcessId && b.GetProcessId() > 0 && b.GetProcessId() != a.GetProcessId(),
            $"{a.GetProcessId()} main={a.MainFrame?.ProcessId} other={b.GetProcessId()}");
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            check("GetOSProcessId is the renderer the process table lists with GetProcessId as its client id",
                a.GetOSProcessId() > 0 && PsRenderer(a.GetProcessId()) == a.GetOSProcessId() && PsRenderer(b.GetProcessId()) == b.GetOSProcessId()
                && a.GetOSProcessId() != b.GetOSProcessId(), $"{a.GetOSProcessId()} ps={PsRenderer(a.GetProcessId())} other={b.GetOSProcessId()}");
        var view = new WebContentsView();
        check("GetWebContentsType is window for a window's page and a view's, as Electron's getType; IsOffscreen is false",
            a.GetWebContentsType() == "window" && view.WebContents.GetWebContentsType() == "window" && !a.IsOffscreen(),
            $"{a.GetWebContentsType()} {view.WebContents.GetWebContentsType()} {a.IsOffscreen()}");
        view.WebContents.Close();
        var closed = new TaskCompletionSource();
        b.Destroyed += (_, _) => closed.TrySetResult();
        second.Destroy();
        await Task.WhenAny(closed.Task, Task.Delay(5000));
        check("a closed page has no process: GetProcessId -1, GetOSProcessId 0", b.GetProcessId() == -1 && b.GetOSProcessId() == 0,
            $"{b.GetProcessId()} {b.GetOSProcessId()}");
        first.Destroy();
    }
}
