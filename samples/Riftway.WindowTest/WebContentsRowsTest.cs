using Riftway;

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
        await CopyImageAsync(check, step);
        await VideoFrameAsync(check, step);
        await OpenerAsync(check, step);
        await SavePageAsync(check);
        await PrintToPdfAsync(check);
        await WebRtcAsync(check);
        await ImageAnimationAsync(check);
        await AdditionalArgumentsAsync(check);
        await CaretBrowsingAsync(check);
        await SharedWorkersAsync(check, debugPort);
        await CloseUnstartedAsync(check, step);
    }

    // Electron's webContents.close() on a view never added to a window: 'destroyed' once on a later task, isDestroyed,
    // and the view can no longer be added ("Can't add a destroyed child view to a parent view").
    static async Task CloseUnstartedAsync(CheckFn check, TimeSpan step)
    {
        var view = new WebContentsView();
        var page = view.WebContents;
        var destroyed = 0;
        page.Destroyed += (_, _) => Interlocked.Increment(ref destroyed);
        page.Close();
        var deadline = DateTime.UtcNow + step;
        while (!page.IsDestroyed() && DateTime.UtcNow < deadline) await Task.Delay(50);
        await Task.Delay(500);
        check("WebContents.Close on a view's page that never started destroys it: Destroyed once, IsDestroyed",
            page.IsDestroyed() && Volatile.Read(ref destroyed) == 1, $"destroyed={destroyed} isDestroyed={page.IsDestroyed()}");
        page.Close();
        await Task.Delay(500);
        check("closing it again raises nothing more", Volatile.Read(ref destroyed) == 1, destroyed);

        var host = new BaseWindow(new BaseWindowOptions { Show = false });
        var tabs = RiftwayApp.Current.GetAppMetrics().Count(m => m.Type == "Tab");
        string? refused = null;
        try { host.ContentView.AddChildView(view); }
        catch (InvalidOperationException e) { refused = e.Message; }
        await Task.Delay(1500);
        var tabsAfter = RiftwayApp.Current.GetAppMetrics().Count(m => m.Type == "Tab");
        check("adding that view to a window throws Electron's error, and no page starts",
            refused == "Can't add a destroyed child view to a parent view" && host.ContentView.Children.Count == 0 && page.GetProcessId() < 0 && tabsAfter == tabs,
            $"{refused} children={host.ContentView.Children.Count} pid={page.GetProcessId()} tabs={tabs}->{tabsAfter}");
        host.Destroy();
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
        await wc.ExecuteJavaScriptAsync("window.kept = new (class RiftwayHeapMarker { constructor() { this.tag = ['riftway', 'heap', 'marker', 6 * 7].join('-'); } })(); 1");
        var dir = Path.Combine(Path.GetTempPath(), "riftway-heap-" + Environment.ProcessId);
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
            && strings.Contains("RiftwayHeapMarker") && strings.Contains("riftway-heap-marker-42"),
            $"error={error} length={text.Length} marker={strings.Contains("RiftwayHeapMarker")}/{strings.Contains("riftway-heap-marker-42")}");
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
    // Windows PowerShell in another process reads the clipboard independently of Riftway's code.
    static string Ps(string script)
    {
        var outFile = Path.Combine(Path.GetTempPath(), "riftway-rows-clipboard-" + Path.GetRandomFileName());
        var wrapped = "Add-Type -AssemblyName System.Windows.Forms, System.Drawing; $o = & { " + script + " }; [IO.File]::WriteAllText('" + outFile + "', [string]$o, [Text.Encoding]::UTF8)";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-STA", "-Command", wrapped }) psi.ArgumentList.Add(arg);
        System.Diagnostics.Process.Start(psi)!.WaitForExit();
        var text = File.Exists(outFile) ? File.ReadAllText(outFile).TrimStart('﻿') : "";
        File.Delete(outFile);
        return text;
    }

    // Off the UI thread: the clipboard owner answers render requests there.
    static Task<string> PsAsync(string script) => Task.Run(() => Ps(script));

    static async Task<bool> Eventually(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return await condition();
    }

    // Electron's copyVideoFrameAt and saveVideoFrameAs: Blink's "Copy Video Frame" at the size the video is drawn, and
    // "Save Video Frame As" at its own size as a download named videoframe_<playback position in ms>.
    internal static async Task VideoFrameAsync(CheckFn check, TimeSpan step)
    {
        if (!OperatingSystem.IsWindows()) return;
        static Task<string> ClipImage() => PsAsync("$i = [Windows.Forms.Clipboard]::GetImage(); if ($i) { $p = $i.GetPixel(1, 1); '' + $i.Width + 'x' + $i.Height + ' ' + $p.R + ',' + $p.G + ',' + $p.B } else { 'none' }");
        // Video frames go through YUV, so red comes back within a few levels of 255,0,0.
        static bool IsRed(string clip, string size) => clip.Split(' ') is [var s, var rgb] && s == size
            && rgb.Split(',').Select(int.Parse).ToArray() is [> 245, < 10, < 10];
        var win = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300 });
        var wc = win.WebContents;
        try
        {
            // A 160x120 red canvas streamed into a 200x100 video: drawn at 133x100 (object-fit contain).
            await wc.LoadUrl("data:text/html,<style>body{margin:0}</style><video id=v muted autoplay style='position:absolute;left:0;top:0;width:200px;height:100px'></video>"
                + "<script>const c = document.createElement('canvas'); c.width = 160; c.height = 120; const g = c.getContext('2d');"
                + "(function f() { g.fillStyle = 'red'; g.fillRect(0, 0, 160, 120); requestAnimationFrame(f); })(); v.srcObject = c.captureStream(30);</script>");
            // Until the video shows red: a capture stream's first frame can come before the canvas has drawn.
            var playing = await Eventually(async () => (await wc.ExecuteJavaScriptAsync("v.readyState >= 2 && v.videoWidth === 160 && (() => {"
                + " const k = document.createElement('canvas'); k.width = k.height = 1; const x = k.getContext('2d'); x.drawImage(v, 0, 0, 1, 1);"
                + " const d = x.getImageData(0, 0, 1, 1).data; return d[0] > 200 && d[1] < 50 && d[2] < 50; })()"))?.GetValue<bool>() == true, step);
            check("video frame: the test video plays", playing);

            await Clipboard.WriteTextAsync("before");
            wc.CopyVideoFrameAt(100, 50);
            var image = "";
            var copied = await Eventually(async () => IsRed(image = await ClipImage(), "133x100"), step);
            check("CopyVideoFrameAt copies the frame at the size the video is drawn (object-fit contain)", copied, image);

            await wc.ExecuteJavaScriptAsync("v.style.objectFit = 'fill'");
            await Clipboard.WriteTextAsync("before");
            wc.CopyVideoFrameAt(100, 50);
            check("CopyVideoFrameAt follows object-fit: fill copies the video's box", await Eventually(async () => IsRed(image = await ClipImage(), "200x100"), step), image);

            await Clipboard.WriteTextAsync("before");
            wc.CopyVideoFrameAt(300, 250);
            await Task.Delay(1500);
            check("CopyVideoFrameAt where there is no video leaves the clipboard alone", await PsAsync("Get-Clipboard -Raw") == "before", await ClipImage());

            var dir = Directory.CreateTempSubdirectory("riftway-videoframe");
            var names = new List<string>();
            var finished = new TaskCompletionSource<DownloadState>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnWillDownload(object? sender, WillDownloadEventArgs e)
            {
                lock (names) names.Add(e.Item.GetFilename());
                e.Item.SetSavePath(Path.Combine(dir.FullName, e.Item.GetFilename()));
                e.Item.Done += (_, d) => finished.TrySetResult(d.State);
            }
            wc.Session.WillDownload += OnWillDownload;
            try
            {
                wc.SaveVideoFrameAs(100, 50);
                var state = await finished.Task.WaitAsync(step).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result.ToString() : "no download");
                string name;
                lock (names) name = names.Count > 0 ? names[0] : "";
                var file = Path.Combine(dir.FullName, name);
                var size = name.Length > 0 && File.Exists(file) ? NativeImage.CreateFromPath(file).GetSize() : (0, 0);
                check("SaveVideoFrameAs downloads the frame at the video's own size as videoframe_<ms>.png",
                    state == "Completed" && System.Text.RegularExpressions.Regex.IsMatch(name, @"^videoframe_\d+\.png$") && size == (160, 120), $"{state} {name} {size}");

                lock (names) names.Clear();
                wc.SaveVideoFrameAs(300, 250);
                await Task.Delay(1500);
                int count;
                lock (names) count = names.Count;
                check("SaveVideoFrameAs where there is no video downloads nothing", count == 0, count);
            }
            finally { wc.Session.WillDownload -= OnWillDownload; }
        }
        finally { win.Destroy(); }
    }

    static async Task CopyImageAsync(CheckFn check, TimeSpan step)
    {
        if (!OperatingSystem.IsWindows()) return;
        string saved;
        try { saved = await Clipboard.ReadTextAsync(); } catch (Exception) { saved = ""; }
        // A 4x3 red PNG shown at 80x60, and a 10x10 green canvas: what is copied is the image at its own size.
        var pixels = new byte[4 * 3 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0, 0, 255, 255);
        var png = Convert.ToBase64String(NativeImage.CreateFromBitmap(pixels, new NativeImageBitmapOptions { Width = 4, Height = 3 }).ToPng());
        var win = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300 });
        var wc = win.WebContents;
        await wc.LoadUrl("data:text/html,<style>body{margin:0}</style>"
            + $"<img id=i src='data:image/png;base64,{png}' alt='A %26 \"B\"' style='position:absolute;left:0;top:0;width:80px;height:60px'>"
            + "<canvas id=c width=10 height=10 style='position:absolute;left:100px;top:0;width:50px;height:50px'></canvas>"
            + "<p style='position:absolute;left:0;top:150px'>text</p>"
            + "<script>const g = c.getContext('2d'); g.fillStyle = '%2300ff00'; g.fillRect(0, 0, 10, 10);</script>");
        static Task<string> ClipImage() => PsAsync("$i = [Windows.Forms.Clipboard]::GetImage(); if ($i) { $p = $i.GetPixel(1, 1); '' + $i.Width + 'x' + $i.Height + ' ' + $p.R + ',' + $p.G + ',' + $p.B } else { 'none' }");
        static Task<string> ClipHtml() => PsAsync("[Windows.Forms.Clipboard]::GetText('Html')");
        static Task<string> Formats() => PsAsync("[Windows.Forms.Clipboard]::GetDataObject().GetFormats() -join ','");

        await Clipboard.WriteTextAsync("before");
        wc.CopyImageAt(20, 20);
        var image = "";
        var copied = await Eventually(async () => (image = await ClipImage()) == "4x3 255,0,0", step);
        var html = await ClipHtml();
        var formats = await Formats();
        check("CopyImageAt copies the image at the point at its own size, with <img> markup and the src as a bookmark, as Blink's Copy Image",
            copied && html.Contains($"<img src=\"data:image/png;base64,{png}\" alt=\"A &amp; &quot;B&quot;\"/>", StringComparison.Ordinal)
            && formats.Contains("UniformResourceLocatorW", StringComparison.Ordinal), $"{image} formats={formats} html={(html.Length > 160 ? html[..160] : html)}");

        await Clipboard.WriteTextAsync("before");
        wc.CopyImageAt(120, 20);
        var canvas = await Eventually(async () => (image = await ClipImage()) == "10x10 0,255,0", step);
        formats = await Formats();
        check("CopyImageAt on a canvas copies its pixels, without markup or bookmark", canvas && !formats.Contains("HTML Format", StringComparison.Ordinal)
            && !formats.Contains("UniformResourceLocatorW", StringComparison.Ordinal), $"{image} formats={formats}");

        wc.SetZoomFactor(2);
        await Task.Delay(500);
        await Clipboard.WriteTextAsync("before");
        wc.CopyImageAt(140, 100);
        check("CopyImageAt's point is in DIPs of the view, whatever the zoom", await Eventually(async () => (image = await ClipImage()) == "4x3 255,0,0", step), image);
        wc.SetZoomFactor(1);
        await Task.Delay(500);

        await Clipboard.WriteTextAsync("before");
        wc.CopyImageAt(10, 160);
        await Task.Delay(1500);
        check("CopyImageAt where there is no image leaves the clipboard alone", await PsAsync("Get-Clipboard -Raw") == "before" && await ClipImage() == "none", await Formats());

        // An iframe's own document: Blink's hit test goes down into frames in the same process.
        var framed = Path.Combine(Path.GetTempPath(), $"riftway-copy-image-{Environment.ProcessId}.html");
        File.WriteAllText(framed, "<!doctype html><style>body{margin:0}</style><iframe style='position:absolute;left:50px;top:50px;width:200px;height:150px;border:5px solid;padding:7px' "
            + "srcdoc=\"<style>body{margin:0}</style><canvas id=c width=6 height=5 style='width:60px;height:50px'></canvas><script>const g = c.getContext('2d'); g.fillStyle = 'blue'; g.fillRect(0, 0, 6, 5);</script>\"></iframe>");
        await wc.LoadFile(framed).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        await Clipboard.WriteTextAsync("before");
        // Near the canvas's far corner: off it unless the frame's border and padding are allowed for.
        wc.CopyImageAt(50 + 5 + 7 + 58, 50 + 5 + 7 + 48);
        check("CopyImageAt finds the image inside a same-process iframe", await Eventually(async () => (image = await ClipImage()) == "6x5 0,0,255", step), image);

        // A cross-origin image taints a canvas, so its pixels come from the bytes the page loaded.
        var blue = new byte[5 * 4 * 4];
        for (var i = 0; i < blue.Length; i += 4) (blue[i], blue[i + 1], blue[i + 2], blue[i + 3]) = (255, 0, 0, 255);
        var bluePng = NativeImage.CreateFromBitmap(blue, new NativeImageBitmapOptions { Width = 5, Height = 4 }).ToPng();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                using var _ = client;
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {bluePng.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(bluePng);
                }
                catch (Exception) { }
            }
        });
        File.WriteAllText(framed, $"<!doctype html><style>body{{margin:0}}</style><img src='http://127.0.0.1:{port}/blue.png' style='width:100px;height:80px'>");
        await wc.LoadFile(framed).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        await Clipboard.WriteTextAsync("before");
        wc.CopyImageAt(30, 30);
        var tainted = await Eventually(async () => (image = await ClipImage()) == "5x4 0,0,255", step);
        html = await ClipHtml();
        check("CopyImageAt copies a cross-origin image the page cannot read from the bytes it loaded",
            tainted && html.Contains($"<img src=\"http://127.0.0.1:{port}/blue.png\"/>", StringComparison.Ordinal), $"{image} {html}");
        listener.Stop();
        win.Destroy();
        File.Delete(framed);
        try { await Clipboard.WriteTextAsync(saved); } catch (Exception) { }
    }

    static async Task OpenerAsync(CheckFn check, TimeSpan step)
    {
        var dir = Directory.CreateTempSubdirectory("riftway-opener-").FullName;
        File.WriteAllText(Path.Combine(dir, "opener.html"), "<!doctype html><title>opener</title><a id=blank href=popup.html target=_blank>blank</a><iframe id=f src=inner.html></iframe>");
        File.WriteAllText(Path.Combine(dir, "inner.html"), "<!doctype html><title>inner</title><p>inner</p>");
        File.WriteAllText(Path.Combine(dir, "popup.html"), "<!doctype html><title>popup</title><p>popup</p>");
        var win = new BrowserWindow(new BrowserWindowOptions { Show = false });
        var wc = win.WebContents;
        var created = new System.Collections.Concurrent.ConcurrentQueue<BrowserWindow>();
        // Handlers run on the UI thread, where Opener answers from the read made as the page was created.
        var onUi = new System.Collections.Concurrent.ConcurrentDictionary<BrowserWindow, WebFrameMain?>();
        wc.DidCreateWindow += (_, e) =>
        {
            var popup = e.Window;
            popup.WebContents.DidFinishLoad += (_, _) => onUi.TryAdd(popup, popup.WebContents.Opener);
            created.Enqueue(popup);
        };
        wc.SetWindowOpenHandler(_ => WindowOpenHandlerResponse.Allow(outlivesOpener: true));
        await wc.LoadFile(Path.Combine(dir, "opener.html"));
        await Task.Delay(300);
        async Task<BrowserWindow?> Open(string code, WebFrameMain? frame = null)
        {
            var before = created.Count;
            await (frame ?? wc.MainFrame!).ExecuteJavaScriptAsync(code, true);
            for (var i = 0; i < 100 && created.Count == before; i++) await Task.Delay(50);
            var popup = created.ToArray().ElementAtOrDefault(before);
            if (popup is not null) for (var i = 0; i < 100 && popup.WebContents.GetTitle() != "popup"; i++) await Task.Delay(50);
            return popup;
        }
        WebFrameMain? OpenerOf(BrowserWindow? popup) => popup is null ? null : Task.Run(() => popup.WebContents.Opener).Result;

        check("a window nothing opened has no Opener", OpenerOf(win) is null);
        var fromMain = await Open("window.open('popup.html', 'one'); 1");
        check("Opener of a window.open popup is the frame that opened it: the opener's main frame",
            fromMain is not null && OpenerOf(fromMain) == wc.MainFrame && fromMain.WebContents.Opener == wc.MainFrame, OpenerOf(fromMain)?.Url);
        var inner = wc.MainFrame!.Frames.FirstOrDefault();
        var fromFrame = inner is null ? null : await Open("window.open('popup.html', 'two'); 1", inner);
        check("Opener of a popup an iframe opened is that iframe", fromFrame is not null && inner is not null && OpenerOf(fromFrame) == inner, OpenerOf(fromFrame)?.Url);
        var noOpener = await Open("window.open('popup.html', 'three', 'noopener'); 1");
        check("a popup opened with noopener has no Opener", noOpener is not null && OpenerOf(noOpener) is null, OpenerOf(noOpener)?.Url);
        WebFrameMain? Ui(BrowserWindow? popup) => popup is not null && onUi.TryGetValue(popup, out var frame) ? frame : null;
        check("on the UI thread, Opener is the opener read as the popup was created", fromMain is not null && onUi.ContainsKey(fromMain) && Ui(fromMain) == wc.MainFrame
            && Ui(fromFrame) == inner && noOpener is not null && onUi.ContainsKey(noOpener) && Ui(noOpener) is null, $"{Ui(fromMain)?.Url} {Ui(fromFrame)?.Url} {onUi.Count}");
        var blank = await Open("document.getElementById('blank').click(); 1");
        check("a target=_blank link opens without an opener, so no Opener", blank is not null && OpenerOf(blank) is null, OpenerOf(blank)?.Url);
        await fromMain!.WebContents.ExecuteJavaScriptAsync("window.opener = null; 1");
        check("a popup that drops window.opener has no Opener", OpenerOf(fromMain) is null, OpenerOf(fromMain)?.Url);
        win.Destroy();
        await Task.Delay(1000);
        // Read on the UI thread before any read off it refreshes the value.
        var afterGone = new TaskCompletionSource<WebFrameMain?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (fromFrame is not null)
        {
            fromFrame.WebContents.PageTitleUpdated += (_, _) => afterGone.TrySetResult(fromFrame.WebContents.Opener);
            await fromFrame.WebContents.ExecuteJavaScriptAsync("document.title = 'gone'; 1");
        }
        var goneOnUi = await afterGone.Task.WaitAsync(step).ContinueWith(t => t.IsCompletedSuccessfully ? (t.Result is null ? "null" : t.Result.Url) : "no title event");
        check("on the UI thread, Opener is null once the opener has gone", goneOnUi == "null", goneOnUi);
        check("Opener is null once the opener has gone", fromFrame is not null && OpenerOf(fromFrame) is null, OpenerOf(fromFrame)?.Url);
        foreach (var popup in created) if (!popup.IsDestroyed) popup.Destroy();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    static async Task SavePageAsync(CheckFn check)
    {
        var dir = Directory.CreateTempSubdirectory("riftway-save-").FullName;
        var site = Directory.CreateDirectory(Path.Combine(dir, "site")).FullName;
        var pixels = new byte[3 * 2 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0, 255, 0, 255);
        var png = NativeImage.CreateFromBitmap(pixels, new NativeImageBitmapOptions { Width = 3, Height = 2 }).ToPng();
        File.WriteAllBytes(Path.Combine(site, "pic.png"), png);
        File.WriteAllText(Path.Combine(site, "look.css"), "p { color: rgb(1, 2, 3); }");
        var source = "<!doctype html><html><head><title>saved</title><link rel=stylesheet href=look.css></head><body><p id=t>Saved text</p><img src=pic.png>"
            + "<script>document.getElementById('t').dataset.ran = 'yes';</script></body></html>";
        var page = Path.Combine(site, "page.html");
        File.WriteAllText(page, source);
        var win = new BrowserWindow(new BrowserWindowOptions { Show = false });
        var wc = win.WebContents;
        await wc.LoadFile(page);
        async Task<string?> Save(string path, SavePageType type)
        {
            try { await wc.SavePageAsync(path, type).WaitAsync(TimeSpan.FromSeconds(20)); return null; }
            catch (Exception e) { return e.GetBaseException().Message; }
        }

        var only = Path.Combine(dir, "only.html");
        var onlyError = await Save(only, SavePageType.HtmlOnly);
        check("SavePageAsync HtmlOnly writes the page's HTML as it was loaded", onlyError is null && File.Exists(only) && File.ReadAllText(only) == source
            && !Directory.Exists(Path.Combine(dir, "only_files")), onlyError ?? (File.Exists(only) ? File.ReadAllText(only)[..Math.Min(80, File.ReadAllText(only).Length)] : "missing"));

        var mhtml = Path.Combine(dir, "page.mhtml");
        var mhtmlError = await Save(mhtml, SavePageType.Mhtml);
        var mhtmlText = File.Exists(mhtml) ? File.ReadAllText(mhtml) : "";
        check("SavePageAsync Mhtml writes one MHTML file with the page and its image in it", mhtmlError is null && mhtmlText.StartsWith("From: <Saved by Blink>", StringComparison.Ordinal)
            && mhtmlText.Contains("Content-Location: " + new Uri(page).AbsoluteUri, StringComparison.Ordinal)
            && mhtmlText.Contains("Content-Location: " + new Uri(Path.Combine(site, "pic.png")).AbsoluteUri, StringComparison.Ordinal),
            mhtmlError ?? mhtmlText[..Math.Min(120, mhtmlText.Length)]);

        var complete = Path.Combine(dir, "whole.html");
        var completeError = await Save(complete, SavePageType.HtmlComplete);
        var completeText = File.Exists(complete) ? File.ReadAllText(complete) : "";
        var files = Path.Combine(dir, "whole_files");
        check("SavePageAsync HtmlComplete writes the page with its links into whole_files, which holds the resources as loaded",
            completeError is null && completeText.StartsWith("<!DOCTYPE html>\n<!-- saved from url=(", StringComparison.Ordinal)
            && completeText.Contains("src=\"./whole_files/pic.png\"", StringComparison.Ordinal) && completeText.Contains("href=\"./whole_files/look.css\"", StringComparison.Ordinal)
            && completeText.Contains("data-ran=\"yes\"", StringComparison.Ordinal)
            && File.Exists(Path.Combine(files, "pic.png")) && File.ReadAllBytes(Path.Combine(files, "pic.png")).SequenceEqual(png)
            && File.Exists(Path.Combine(files, "look.css")) && File.ReadAllText(Path.Combine(files, "look.css")) == "p { color: rgb(1, 2, 3); }",
            completeError ?? completeText[..Math.Min(200, completeText.Length)]);

        // Over http the page is downloaded again, as SavePackage does; no-store keeps a cached copy out of it.
        var served = 0;
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                using var _ = client;
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream);
                    var first = await reader.ReadLineAsync() ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var body = first.Contains("/page", StringComparison.Ordinal)
                        ? System.Text.Encoding.UTF8.GetBytes($"<!doctype html><title>net</title><p>served {Interlocked.Increment(ref served)}</p>") : [];
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {(body.Length > 0 ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(body);
                }
                catch (Exception) { }
            }
        });
        await wc.LoadUrl($"http://127.0.0.1:{port}/page");
        var net = Path.Combine(dir, "net.html");
        var netError = await Save(net, SavePageType.HtmlOnly);
        var netText = File.Exists(net) ? File.ReadAllText(net) : "missing";
        check("SavePageAsync HtmlOnly over http downloads the page again, as SavePackage does", netError is null && served == 2
            && netText == "<!doctype html><title>net</title><p>served 2</p>", $"{netError} served={served} {netText}");
        listener.Stop();

        check("SavePageAsync rejects a relative path with Electron's message", await Save("relative.html", SavePageType.HtmlOnly) == "Path must be absolute");
        check("SavePageAsync rejects a path it cannot write", await Save(Path.Combine(dir, "missing", "x.mhtml"), SavePageType.Mhtml) == "Failed to save the page");
        win.Destroy();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    // What a PDF says of itself: its page boxes in points, and every content stream inflated.
    static (List<(double W, double H)> Pages, string Content) ReadPdf(byte[]? pdf)
    {
        if (pdf is null) return ([], string.Empty);
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var pages = System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page(?![a-zA-Z])[\s\S]*?/MediaBox\s*\[\s*[\d.]+\s+[\d.]+\s+([\d.]+)\s+([\d.]+)\s*\]")
            .Select(m => (double.Parse(m.Groups[1].Value, invariant), double.Parse(m.Groups[2].Value, invariant))).ToList();
        var content = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"stream\r?\n"))
        {
            var start = m.Index + m.Length;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            try
            {
                using var inflate = new System.IO.Compression.ZLibStream(new MemoryStream(pdf, start, end - start), System.IO.Compression.CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                inflate.CopyTo(inflated);
                content.Append(System.Text.Encoding.Latin1.GetString(inflated.ToArray())).Append('\n');
            }
            catch (InvalidDataException) { }
        }
        return (pages, content.ToString());
    }

    static async Task PrintToPdfAsync(CheckFn check)
    {
        var win = new BrowserWindow(new BrowserWindowOptions { Show = false });
        var wc = win.WebContents;
        // Three pages; a red page background and a blue block that print only as backgrounds.
        var page = Path.Combine(Path.GetTempPath(), $"riftway-pdf-{Environment.ProcessId}.html");
        File.WriteAllText(page, "<!doctype html><title>pdf</title><style>html{background:rgb(255,0,0)}section{break-after:page}"
            + "div{width:100px;height:100px;background:rgb(0,0,255)}</style><section>one<div></div></section><section>two</section><section>three"
            + "<iframe srcdoc='<p>inner</p>'></iframe></section>");
        await wc.LoadFile(page);
        async Task<(byte[]? Pdf, string? Error)> Print(PrintToPdfOptions? options, WebFrameMain? frame = null)
        {
            try { return (await (frame is null ? wc.PrintToPDFAsync(options) : frame.PrintToPdfAsync(options)).WaitAsync(TimeSpan.FromSeconds(30)), null); }
            catch (Exception e) { return (null, $"{e.GetType().Name}: {e.Message}"); }
        }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1.5;
        static string Sizes((List<(double W, double H)> Pages, string Content) read) => string.Join(",", read.Pages);

        var (pdf, error) = await Print(null);
        var read = ReadPdf(pdf);
        check("PrintToPdfAsync returns a PDF of every page on Letter paper by default",
            error is null && pdf!.AsSpan(0, 5).SequenceEqual("%PDF-"u8) && read.Pages.Count == 3 && read.Pages.All(p => Near(p.W, 612) && Near(p.H, 792)),
            error ?? Sizes(read));
        check("PrintToPdfAsync leaves backgrounds out by default", error is null && !read.Content.Contains("0 0 1 rg", StringComparison.Ordinal), error);

        (pdf, error) = await Print(new PrintToPdfOptions { PageSize = "A4", Landscape = true, PrintBackground = true, PageRanges = "2-3" });
        read = ReadPdf(pdf);
        check("PrintToPdfAsync takes a named paper in any case, landscape and page ranges",
            error is null && read.Pages.Count == 2 && read.Pages.All(p => Near(p.W, Math.Ceiling(11.7 * 72)) && Near(p.H, Math.Ceiling(8.27 * 72))),
            error ?? Sizes(read));
        check("PrintToPdfAsync prints backgrounds when asked", error is null && read.Content.Contains("1 0 0 rg", StringComparison.Ordinal), error);

        (pdf, error) = await Print(new PrintToPdfOptions { PageSize = PageSize.Custom(4, 5), PageRanges = "1", PrintBackground = true });
        read = ReadPdf(pdf);
        check("PrintToPdfAsync takes a paper size in inches", error is null && read.Pages.Count == 1 && Near(read.Pages[0].W, 288) && Near(read.Pages[0].H, 360)
            && read.Content.Contains("0 0 1 rg", StringComparison.Ordinal), error ?? Sizes(read));

        (pdf, error) = await Print(new PrintToPdfOptions
        {
            DisplayHeaderFooter = true,
            PageRanges = "1",
            HeaderTemplate = "<div style='width:50px;height:20px;background:rgb(0,255,0);-webkit-print-color-adjust:exact'></div>",
        });
        read = ReadPdf(pdf);
        check("PrintToPdfAsync prints the header template", error is null && read.Content.Contains("0 1 0 rg", StringComparison.Ordinal), error);

        var (_, paperError) = await Print(new PrintToPdfOptions { PageSize = "A9" });
        check("PrintToPdfAsync rejects an unknown paper with Electron's message", paperError == "ArgumentException: Invalid pageSize A9", paperError);
        var (_, marginError) = await Print(new PrintToPdfOptions { PageSize = PageSize.Custom(4, 5), Margins = new PdfMargins { Left = 4.5 } });
        check("PrintToPdfAsync rejects margins larger than the paper", marginError == "ArgumentException: margins must be less than or equal to pageSize", marginError);
        var (_, scaleError) = await Print(new PrintToPdfOptions { Scale = 3 });
        check("PrintToPdfAsync rejects a scale outside 0.1 to 2 with Chromium's message",
            scaleError == "ArgumentException: Invalid print parameters: scale is outside of [0.1 - 2] range", scaleError);
        // Each margin fits the paper, as Electron checks, but together they leave nothing to print on.
        var (_, areaError) = await Print(new PrintToPdfOptions { PageSize = PageSize.Custom(1, 1), Margins = new PdfMargins { Top = 0.6, Bottom = 0.6 } });
        check("PrintToPdfAsync rejects margins that leave no content area with Chromium's message",
            areaError == "ArgumentException: Invalid print parameters: invalid print parameters: content area is empty", areaError);
        var (_, syntaxError) = await Print(new PrintToPdfOptions { PageRanges = "abc" });
        check("PrintToPdfAsync rejects a page range it cannot read", syntaxError == "InvalidOperationException: Failed to generate PDF: Page range syntax error", syntaxError);
        var (_, countError) = await Print(new PrintToPdfOptions { PageRanges = "7" });
        check("PrintToPdfAsync rejects a page range past the last page", countError == "InvalidOperationException: Failed to generate PDF: Page range exceeds page count", countError);

        var all = await Task.WhenAll(Enumerable.Range(1, 3).Select(i => Print(new PrintToPdfOptions { PageRanges = $"{i}-3" })));
        check("PrintToPdfAsync queues jobs started together, each printing its own pages",
            all.Select((r, i) => r.Error is null && ReadPdf(r.Pdf!).Pages.Count == 3 - i).All(ok => ok), string.Join(" | ", all.Select(r => r.Error ?? Sizes(ReadPdf(r.Pdf!)))));

        (pdf, error) = await Print(new PrintToPdfOptions { PageRanges = "1" }, wc.MainFrame);
        check("WebFrameMain.PrintToPdfAsync prints the main frame", error is null && ReadPdf(pdf!).Pages.Count == 1, error);
        var inner = wc.MainFrame!.Frames.FirstOrDefault();
        var (_, innerError) = inner is null ? (null, "no iframe") : await Print(null, inner);
        check("WebFrameMain.PrintToPdfAsync refuses a subframe (drift C51)", innerError?.StartsWith("NotSupportedException:", StringComparison.Ordinal) == true, innerError);
        await wc.LoadUrl("data:text/html,<p>after</p>");
        var (_, goneError) = inner is null ? (null, "no iframe") : await Print(null, inner);
        check("WebFrameMain.PrintToPdfAsync of a frame that has gone rejects with Electron's message",
            goneError == "InvalidOperationException: Render frame was disposed before WebFrameMain could be accessed", goneError);
        win.Destroy();
        File.Delete(page);
    }
    // The ICE candidates a fresh peer connection gathers, as "protocol port" pairs.
    const string GatherCandidates = """
        (async () => {
          const pc = new RTCPeerConnection();
          pc.createDataChannel('x');
          const found = [];
          pc.onicecandidate = e => { if (e.candidate && e.candidate.candidate) found.push(e.candidate.protocol + ' ' + e.candidate.port); };
          await pc.setLocalDescription(await pc.createOffer());
          await new Promise(done => {
            pc.onicegatheringstatechange = () => { if (pc.iceGatheringState === 'complete') done(); };
            setTimeout(done, 8000);
          });
          pc.close();
          return found;
        })()
        """;

    static async Task WebRtcAsync(CheckFn check)
    {
        var win = new BrowserWindow(new BrowserWindowOptions { Show = false });
        var wc = win.WebContents;
        await wc.LoadUrl("data:text/html,<p>webrtc</p>");
        // A gathering that fails reports a "failed" candidate, which every check below refuses.
        async Task<List<(string Protocol, int Port)>> Gather()
        {
            try
            {
                var list = await wc.ExecuteJavaScriptAsync(GatherCandidates).WaitAsync(TimeSpan.FromSeconds(20));
                return [.. list!.AsArray().Select(n => ((string)n!).Split(' ')).Select(p => (p[0], int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))];
            }
            catch (Exception) { return [("failed", -1)]; }
        }
        static string Show(List<(string Protocol, int Port)> found) => string.Join(",", found.Select(c => $"{c.Protocol}:{c.Port}"));

        var before = await Gather();
        check("GetWebRtcIpHandlingPolicy is default, and WebRTC gathers UDP candidates", wc.GetWebRtcIpHandlingPolicy() == "default" && before.Any(c => c.Protocol == "udp"), Show(before));
        wc.SetWebRtcIpHandlingPolicy("disable_non_proxied_udp");
        var blocked = await Gather();
        check("SetWebRtcIpHandlingPolicy(disable_non_proxied_udp) stops UDP candidates on the open page",
            wc.GetWebRtcIpHandlingPolicy() == "disable_non_proxied_udp" && blocked.All(c => c.Protocol is not ("udp" or "failed")), Show(blocked));
        wc.SetWebRtcIpHandlingPolicy("nonsense");
        var reset = await Gather();
        check("SetWebRtcIpHandlingPolicy of an unknown policy is default, as Electron's", wc.GetWebRtcIpHandlingPolicy() == "default" && reset.Any(c => c.Protocol == "udp"), Show(reset));

        check("GetWebRtcUdpPortRange is 0, 0 by default", wc.GetWebRtcUdpPortRange() == new WebRtcUdpPortRange(0, 0));
        // A range wide enough for one port per interface.
        var range = new WebRtcUdpPortRange(47100, 47140);
        wc.SetWebRtcUdpPortRange(range);
        var ranged = await Gather();
        check("SetWebRtcUdpPortRange keeps WebRTC's UDP candidates in the range",
            wc.GetWebRtcUdpPortRange() == range && ranged.Any(c => c.Protocol == "udp") && ranged.Where(c => c.Protocol == "udp").All(c => c.Port is >= 47100 and <= 47140), Show(ranged));
        wc.SetWebRtcUdpPortRange(default);
        var any = await Gather();
        check("SetWebRtcUdpPortRange(0, 0) lifts it", wc.GetWebRtcUdpPortRange() == default && any.Any(c => c.Protocol == "udp" && c.Port is < 47100 or > 47140), Show(any));

        string? Refused(WebRtcUdpPortRange bad)
        {
            try { wc.SetWebRtcUdpPortRange(bad); return null; }
            catch (ArgumentException e) { return e.Message; }
        }
        check("SetWebRtcUdpPortRange refuses bad ranges with Electron's messages",
            Refused(new(-1, 5)) == "'min' and 'max' are both required"
            && Refused(new(0, 5)) == "'min' and 'max' must be in the (0, 65535] range or [0, 0]"
            && Refused(new(5, 70000)) == "'min' and 'max' must be in the (0, 65535] range or [0, 0]"
            && Refused(new(10, 5)) == "'max' must be greater than or equal to 'min'"
            && wc.GetWebRtcUdpPortRange() == default && Refused(new(65535, 65535)) is null && Refused(new(5, 5)) is null && wc.GetWebRtcUdpPortRange() == new WebRtcUdpPortRange(5, 5),
            $"{Refused(new(-1, 5))} | {Refused(new(0, 5))} | {Refused(new(5, 70000))} | {Refused(new(10, 5))}");
        wc.SetWebRtcUdpPortRange(default);

        // Drift C52: Chromium keeps these per profile, so the session's other pages follow.
        var other = new BrowserWindow(new BrowserWindowOptions { Show = false });
        await other.WebContents.LoadUrl("data:text/html,<p>other</p>");
        wc.SetWebRtcIpHandlingPolicy("disable_non_proxied_udp");
        List<string> otherFound;
        try { otherFound = [.. (await other.WebContents.ExecuteJavaScriptAsync(GatherCandidates).WaitAsync(TimeSpan.FromSeconds(20)))!.AsArray().Select(n => (string)n!)]; }
        catch (Exception) { otherFound = ["failed"]; }
        check("SetWebRtcIpHandlingPolicy reaches the session's other pages, whose own getter stays default (drift C52)",
            other.WebContents.GetWebRtcIpHandlingPolicy() == "default" && !otherFound.Any(c => c == "failed" || c.StartsWith("udp", StringComparison.Ordinal)), string.Join(",", otherFound));
        wc.SetWebRtcIpHandlingPolicy("default");
        other.Destroy();
        win.Destroy();
    }
    // A 1x1 GIF that loops red, blue, red, ... every 100 ms.
    static readonly byte[] BlinkingGif =
    [
        .. "GIF89a"u8, 1, 0, 1, 0, 0x80, 0, 0, 0xFF, 0, 0, 0, 0, 0xFF,
        0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 3, 1, 0, 0, 0,
        0x21, 0xF9, 4, 0, 10, 0, 0, 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0,
        0x21, 0xF9, 4, 0, 10, 0, 0, 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x4C, 0x01, 0,
        0x3B,
    ];

    // Shows the GIF as a new image (a fresh blob URL, so no cached image keeps an older policy), then reads its pixel
    // from the page's own rendering every 60 ms for 3 s: "r" or "b" per sample. A canvas would only ever draw
    // the first frame, as the HTML spec has it.
    static async Task<string> SampleGif(WebContents wc)
    {
        // The load event, not decode(): decode() waits on the compositor, which stops while Chromium thinks the window hidden.
        try
        {
            await wc.ExecuteJavaScriptAsync($$"""
                new Promise(loaded => {
                  const g = document.getElementById('g');
                  g.onload = () => loaded(1);
                  g.src = URL.createObjectURL(new Blob([Uint8Array.from(atob('{{Convert.ToBase64String(BlinkingGif)}}'), c => c.charCodeAt(0))], { type: 'image/gif' }));
                })
                """).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception e) { return $"no image ({e.GetType().Name}; page {await VisibilityOf(wc)})"; }
        var seen = new System.Text.StringBuilder();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 3000)
        {
            byte[] pixel;
            try { pixel = (await wc.CapturePageAsync(new Rectangle(40, 40, 2, 2)).WaitAsync(TimeSpan.FromSeconds(5))).ToBitmap(); }
            catch (Exception e) { return $"{seen} capture failed ({e.GetType().Name}; page {await VisibilityOf(wc)})"; }
            seen.Append(pixel[2] > pixel[0] ? 'r' : 'b');
            await Task.Delay(60);
        }
        return seen.ToString();
    }

    static async Task<string> VisibilityOf(WebContents wc)
    {
        try { return (string?)await wc.ExecuteJavaScriptAsync("document.visibilityState").WaitAsync(TimeSpan.FromSeconds(5)) ?? "?"; }
        catch (Exception e) { return e.GetType().Name; }
    }

    static async Task ImageAnimationAsync(CheckFn check)
    {
        var win = new BrowserWindow(new BrowserWindowOptions { Show = true, Width = 300, Height = 200 });
        var wc = win.WebContents;
        win.Focus();
        await wc.LoadUrl("data:text/html,<body style='margin:0'><img id=g style='width:100px;height:100px;image-rendering:pixelated'></body>");
        // Captures take longer than a frame, so the samples alias: looping shows red again after blue; playing once rests on blue.
        static bool Loops(string s) => s.IndexOf('b') is >= 0 and var b && s.IndexOf('r', b) > b;
        static bool PlayedOnce(string s) => s.IndexOf('b') is >= 0 and var b && s.IndexOf('r', b) < 0;

        var animated = await SampleGif(wc);
        check("a new image animates by default", Loops(animated), animated);
        wc.SetImageAnimationPolicy("noAnimation");
        var still = await SampleGif(wc);
        check("SetImageAnimationPolicy(noAnimation) keeps a new image on its first frame", still.Length > 4 && still.All(c => c == 'r'), still);
        wc.SetImageAnimationPolicy("bogus");
        var ignored = await SampleGif(wc);
        check("SetImageAnimationPolicy ignores an unknown policy, as Electron's", ignored.Length > 4 && ignored.All(c => c == 'r'), ignored);
        wc.SetImageAnimationPolicy("animateOnce");
        var once = await SampleGif(wc);
        check("SetImageAnimationPolicy(animateOnce) plays a new image's frames once", PlayedOnce(once), once);
        wc.SetImageAnimationPolicy("animate");
        var again = await SampleGif(wc);
        check("SetImageAnimationPolicy(animate) animates new images again", Loops(again), again);
        win.Destroy();

        // webPreferences.imageAnimationPolicy: set as the page is made, in a session of its own (drift C53).
        foreach (var (policy, name) in new[] { ("noAnimation", "keeps the page's images on their first frame"), ("Animate", "ignores an unknown policy, as Electron's") })
        {
            var made = new BrowserWindow(new BrowserWindowOptions
            {
                Show = true, Width = 300, Height = 200,
                WebPreferences = new WebPreferences { Partition = "riftway-image-animation-" + policy, ImageAnimationPolicy = policy },
            });
            await made.WebContents.LoadUrl("data:text/html,<body style='margin:0'><img id=g style='width:100px;height:100px;image-rendering:pixelated'></body>");
            var seen = await SampleGif(made.WebContents);
            check($"WebPreferences.ImageAnimationPolicy {policy} {name}", policy == "noAnimation" ? seen.Length > 4 && seen.All(c => c == 'r') : Loops(seen), seen);
            made.Destroy();
        }
    }

    // Electron's additionalArguments: the preload's process.argv ends with them (drift W66).
    static async Task AdditionalArgumentsAsync(CheckFn check)
    {
        var preload = Path.Combine(Path.GetTempPath(), "riftway-argv-preload.js");
        File.WriteAllText(preload, "riftway.contextBridge.exposeInMainWorld('preloadArgv', JSON.stringify(process.argv));");
        var page = Path.Combine(Path.GetTempPath(), "riftway-argv.html");
        File.WriteAllText(page, "<!doctype html><title>argv</title><p>argv</p>");
        var win = new BrowserWindow(new BrowserWindowOptions
        {
            Show = true, Width = 300, Height = 200,
            WebPreferences = new WebPreferences { Preload = preload, AdditionalArguments = ["--riftway-test=1", "two words"] },
        });
        await win.WebContents.LoadFile(page);
        var argv = (await win.WebContents.ExecuteJavaScriptAsync("String(window.preloadArgv)"))?.GetValue<string>();
        var expected = System.Text.Json.JsonSerializer.Serialize(new[] { Environment.ProcessPath, "--riftway-test=1", "two words" });
        check("AdditionalArguments: a preload's process.argv is the app's executable, then the arguments", argv == expected, argv);
        win.Destroy();
        File.Delete(preload);
        File.Delete(page);
    }

    static async Task CaretBrowsingAsync(CheckFn check)
    {
        var win = new BrowserWindow(new BrowserWindowOptions { Show = true, Width = 400, Height = 200 });
        var wc = win.WebContents;
        await wc.LoadUrl("data:text/html,<p id=p>caret browsing text</p>");
        // A collapsed selection at the paragraph's start, then a right arrow: only caret browsing moves it.
        // -1 when the page does not answer.
        async Task<int> ArrowFromStart()
        {
            try
            {
                await wc.ExecuteJavaScriptAsync("getSelection().collapse(document.getElementById('p').firstChild, 0); 1").WaitAsync(TimeSpan.FromSeconds(10));
                wc.Focus();
                wc.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "Right"));
                wc.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "Right"));
                await Task.Delay(300);
                return (int)(await wc.ExecuteJavaScriptAsync("getSelection().focusOffset").WaitAsync(TimeSpan.FromSeconds(10)))!;
            }
            catch (Exception) { return -1; }
        }

        var off = await ArrowFromStart();
        check("caret browsing is off by default: an arrow key leaves the caret", !wc.IsCaretBrowsingEnabled() && off == 0, off);
        wc.SetCaretBrowsingEnabled(true);
        var on = await ArrowFromStart();
        check("SetCaretBrowsingEnabled(true) moves the caret with the arrow keys on the open page", wc.IsCaretBrowsingEnabled() && on == 1, on);
        wc.SetCaretBrowsingEnabled(false);
        var offAgain = await ArrowFromStart();
        check("SetCaretBrowsingEnabled(false) turns it off again", !wc.IsCaretBrowsingEnabled() && offAgain == 0, offAgain);
        win.Destroy();
    }
    // A loopback HTTP server answering GET paths from `files`; 404 otherwise. Stop the listener when done.
    static (System.Net.Sockets.TcpListener Listener, int Port) Serve(Dictionary<string, (string Type, string Body)> files)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = client;
                    try
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream);
                        var path = (await reader.ReadLineAsync() ?? "").Split(' ').ElementAtOrDefault(1) ?? "";
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                        var found = files.TryGetValue(path.Split('?')[0], out var file);
                        var body = System.Text.Encoding.UTF8.GetBytes(found ? file.Body : "");
                        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: {(found ? file.Type : "text/plain")}\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(body);
                    }
                    catch (Exception) { }
                });
            }
        });
        return (listener, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
    }

    static async Task SharedWorkersAsync(CheckFn check, int debugPort)
    {
        var (listener, port) = Serve(new()
        {
            ["/page"] = ("text/html", "<!doctype html><title>workers</title>"),
            ["/worker.js"] = ("text/javascript", "onconnect = e => e.ports[0].postMessage('ready');"),
        });
        var origin = $"http://127.0.0.1:{port}";
        var win = new BrowserWindow(new BrowserWindowOptions { Show = false });
        var other = new BrowserWindow(new BrowserWindowOptions { Show = false });
        var closed = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { DevTools = false } });
        var wc = win.WebContents;
        await wc.LoadUrl($"{origin}/page");
        await other.WebContents.LoadUrl("data:text/html,<p>other</p>");
        await closed.WebContents.LoadUrl("data:text/html,<p>no devtools</p>");
        static string Show(IReadOnlyList<SharedWorkerInfo> list) => string.Join(",", list.Select(w => $"{w.Id[..Math.Min(8, w.Id.Length)]} {w.Url}"));

        var none = wc.GetAllSharedWorkers();
        check("GetAllSharedWorkers is empty with no shared worker running", none.Count == 0, Show(none));
        string Start(string query) => $"new Promise(r => {{ const w = new SharedWorker('/worker.js{query}'); w.port.onmessage = e => r(e.data); }})";
        System.Text.Json.Nodes.JsonNode? started;
        try { started = await wc.ExecuteJavaScriptAsync($"Promise.all([{Start("?a")}, {Start("?b")}])").WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { started = null; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var listed = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/list"))!.AsArray()
            .Where(t => (string?)t!["type"] == "shared_worker").ToDictionary(t => (string)t!["id"]!, t => (string)t!["url"]!);
        var two = wc.GetAllSharedWorkers();
        check("GetAllSharedWorkers lists each shared worker with the id and URL Chromium's DevTools endpoint gives it",
            started?.ToJsonString() == "[\"ready\",\"ready\"]" && two.Count == 2 && listed.Count == 2
            && two.All(w => listed.GetValueOrDefault(w.Id) == w.Url) && two.Select(w => w.Url).Order().SequenceEqual([$"{origin}/worker.js?a", $"{origin}/worker.js?b"]),
            $"{Show(two)} listed={listed.Count}");
        var fromOther = other.WebContents.GetAllSharedWorkers();
        check("GetAllSharedWorkers lists the app's shared workers from any page", fromOther.Select(w => w.Id).Order().SequenceEqual(two.Select(w => w.Id).Order()), Show(fromOther));
        check("GetAllSharedWorkers is empty for a page whose devTools preference is off", closed.WebContents.GetAllSharedWorkers().Count == 0);
        var fresh = new BrowserWindow(new BrowserWindowOptions { Show = false });
        await fresh.WebContents.LoadUrl("data:text/html,<p>fresh</p>");
        var fromUi = Riftway.Cef.CefThread.InvokeOnUi(() => fresh.WebContents.GetAllSharedWorkers().Count);
        check("GetAllSharedWorkers on the UI thread answers a page's first call before any worker is known (drift C54)", fromUi == 0, fromUi);
        fresh.Destroy();

        // Closing the only page connected to them ends both workers.
        win.Destroy();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyList<SharedWorkerInfo> after;
        while ((after = other.WebContents.GetAllSharedWorkers()).Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        check("GetAllSharedWorkers drops a worker once it ends", after.Count == 0, Show(after));
        other.Destroy();
        closed.Destroy();
        listener.Stop();
    }
}
