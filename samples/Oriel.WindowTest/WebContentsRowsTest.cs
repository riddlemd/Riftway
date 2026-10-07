using Oriel;

/// <summary>webContents methods checked against what the page, DevTools' HTTP endpoint, the process table or the
/// clipboard report on their own.</summary>
static class WebContentsRowsTest
{
    internal delegate void CheckFn(string name, bool pass, object? detail = null);

    internal static async Task RunAsync(CheckFn check, TimeSpan step, int debugPort)
    {
        await ProcessAsync(check);
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
