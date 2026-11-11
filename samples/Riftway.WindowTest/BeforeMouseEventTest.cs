using System.Text.Json.Nodes;
using Riftway;

/// <summary>Electron's before-mouse-event for the events sendInputEvent sends: what it reports, and that cancelling one
/// keeps it from the page (Electron's own spec: preventing the left press leaves the right one).</summary>
static class BeforeMouseEventTest
{
    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var dir = Directory.CreateTempSubdirectory("riftway-before-mouse");
        var page = Path.Combine(dir.FullName, "mouse.html");
        File.WriteAllText(page, """
            <!doctype html><title>mouse</title>
            <body style="margin:0;height:100vh" oncontextmenu="event.preventDefault()">
            <script>
              window.seen = [];
              for (const type of ['mousedown', 'mouseup', 'contextmenu', 'wheel'])
                addEventListener(type, e => seen.push(type + ':' + e.button), true);
            </script>
            """);
        var win = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: before-mouse-event", Width = 400, Height = 300 });
        var reported = new List<MouseInputEvent>();
        Func<MouseInputEvent, bool> cancel = _ => false;
        win.WebContents.BeforeMouseEvent += (_, e) =>
        {
            lock (reported) reported.Add(e.Mouse);
            e.Cancel = cancel(e.Mouse);
        };
        async Task<string> Seen() => (await win.WebContents.ExecuteJavaScriptAsync("seen.join()"))?.GetValue<string>() ?? "";
        async Task Reset()
        {
            await win.WebContents.ExecuteJavaScriptAsync("seen.length = 0; 1");
            lock (reported) reported.Clear();
        }
        try
        {
            await win.WebContents.LoadUrl(new Uri(page).AbsoluteUri);
            await Task.Delay(300);

            cancel = m => m.Button == MouseButton.Left;
            win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, 100, 100) { Button = MouseButton.Left });
            win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, 100, 100) { Button = MouseButton.Right });
            await Task.Delay(400);
            var seen = await Seen();
            check("before-mouse-event: cancelling the left press keeps it from the page, the right press arrives", seen == "mousedown:2", seen);
            await Reset();
            cancel = _ => false;

            var sent = new MouseInputEvent(MouseInputEventType.MouseDown, 100, 100) { Button = MouseButton.Left, GlobalX = 200, GlobalY = 200, ClickCount = 1 };
            win.WebContents.SendInputEvent(sent);
            win.WebContents.SendInputEvent(sent with { Type = MouseInputEventType.MouseUp });
            await Task.Delay(400);
            MouseInputEvent[] got;
            lock (reported) got = [.. reported];
            check("before-mouse-event: reports the type, position, screen position, button and click count",
                got.Length == 2 && got[0] == sent && got[1] == sent with { Type = MouseInputEventType.MouseUp }, string.Join(" | ", got.Select(g => g.ToString())));
            await Reset();

            cancel = m => m.Type == MouseInputEventType.ContextMenu;
            win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.ContextMenu, 50, 50) { Button = MouseButton.Right });
            await Task.Delay(400);
            seen = await Seen();
            lock (reported) got = [.. reported];
            check("before-mouse-event: a sent contextMenu is one event, and cancelling it sends nothing",
                got.Length == 1 && got[0].Type == MouseInputEventType.ContextMenu && seen == "", $"{seen} / {string.Join(" | ", got.Select(g => g.Type))}");
            await Reset();
            cancel = _ => false;

            win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseWheel, 50, 50) { DeltaY = -120 });
            win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseEnter, 10, 10));
            win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseLeave, 10, 10));
            await Task.Delay(400);
            seen = await Seen();
            lock (reported) got = [.. reported];
            check("before-mouse-event: wheel events are not raised (the page still gets them); enter and leave are",
                seen.Contains("wheel", StringComparison.Ordinal) && got.Select(g => g.Type).SequenceEqual([MouseInputEventType.MouseEnter, MouseInputEventType.MouseLeave]),
                $"{seen} / {string.Join(" | ", got.Select(g => g.Type))}");
            if (OperatingSystem.IsWindows())
            {
                // The user's own mouse: messages posted to the page's page window, as Windows delivers them, never the real
                // pointer. Positions in client pixels; the page's scale turns them into DIPs.
                await Reset();
                var top = win.GetNativeWindowHandle();
                var pageWindow = Riftway.Cef.Platform.WindowsPageMouse.PageWindows(top).FirstOrDefault();
                var scale = Riftway.Cef.Platform.WindowsPageMouse.Scale(pageWindow);
                static nint At(int x, int y) => (nint)((y << 16) | x);
                cancel = _ => false;
                _ = PostMessageW(pageWindow, 0x0200, 0, At(150, 150));
                _ = PostMessageW(pageWindow, 0x0200, 0, At(160, 155));
                _ = PostMessageW(pageWindow, 0x0201, 0x1, At(160, 155));
                _ = PostMessageW(pageWindow, 0x0202, 0, At(160, 155));
                await Task.Delay(500);
                seen = await Seen();
                lock (reported) got = [.. reported];
                var down = got.FirstOrDefault(g => g.Type == MouseInputEventType.MouseDown);
                var lastMove = got.LastOrDefault(g => g.Type == MouseInputEventType.MouseMove);
                check("before-mouse-event: the user's press over the page is reported and reaches the page",
                    pageWindow != 0 && down is { Button: MouseButton.Left, ClickCount: 1 } && seen.Contains("mousedown:0", StringComparison.Ordinal),
                    $"{seen} / {string.Join(" | ", got.Select(g => g.ToString()))}");
                check("before-mouse-event: the user's events carry the page position in DIPs and the movement since the last move",
                    down is not null && down.X == (int)Math.Floor(160 / scale) && down.Y == (int)Math.Floor(155 / scale)
                    && lastMove is not null && lastMove.MovementX == (int)Math.Floor(160 / scale) - (int)Math.Floor(150 / scale) && lastMove.Button == MouseButton.None,
                    $"scale {scale}: {string.Join(" | ", got.Select(g => g.ToString()))}");
                await Reset();
                cancel = m => m.Type is MouseInputEventType.MouseDown or MouseInputEventType.MouseUp;
                _ = PostMessageW(pageWindow, 0x0204, 0x2, At(100, 100));
                _ = PostMessageW(pageWindow, 0x0205, 0, At(100, 100));
                await Task.Delay(500);
                seen = await Seen();
                lock (reported) got = [.. reported];
                check("before-mouse-event: cancelling the user's press and release keeps them from the page",
                    got.Count(g => g.Button == MouseButton.Right) == 2 && !seen.Contains("mousedown", StringComparison.Ordinal) && !seen.Contains("mouseup", StringComparison.Ordinal),
                    $"{seen} / {string.Join(" | ", got.Select(g => g.ToString()))}");
                cancel = _ => false;
            }
        }
        finally
        {
            win.Destroy();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
