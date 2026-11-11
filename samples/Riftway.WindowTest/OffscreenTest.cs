using Riftway;

/// <summary>Electron's offscreen rendering: a BrowserWindow whose page paints into 'paint' at its window's content size,
/// with start/stop painting, the frame rate, invalidate, a resize, and the window closing with its page.</summary>
static class OffscreenTest
{
    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var win = new BrowserWindow(new BrowserWindowOptions
        {
            Title = "Riftway window test: offscreen",
            Width = 320,
            Height = 240,
            Show = false,
            WebPreferences = new WebPreferences { Offscreen = new OffscreenOptions() },
        });
        var wc = win.WebContents;
        var frames = 0;
        PaintEventArgs? last = null;
        wc.Paint += (_, e) =>
        {
            Interlocked.Increment(ref frames);
            last = e;
        };
        try
        {
            check("offscreen: IsOffscreen", wc.IsOffscreen());
            // A red page whose corner counter changes every 50 ms, so it keeps painting a small part.
            await wc.LoadUrl("data:text/html,<body style='margin:0;background:rgb(255,0,0)'><div id=d style='font:10px monospace'>0</div>"
                + "<script>let n=0;setInterval(()=>d.textContent=++n,50)</script>");
            var painted = Eventually(() => Volatile.Read(ref frames) >= 10, step);
            var size = last?.Image.GetSize() ?? (0, 0);
            check("offscreen: the page paints at its window's outer size, at scale 1 whatever the display", painted && size == (320, 240) && last!.Image.GetScaleFactors() is [1.0], $"{frames} {size}");
            check("offscreen: a frame is the page's pixels", last is not null && IsRed(last.Image, 160, 120), last?.DirtyRect);
            check("offscreen: the dirty rectangle is the part that changed, not the whole frame",
                last is { DirtyRect: { Width: > 0 and < 320 } or { Height: > 0 and < 240 } }, last?.DirtyRect);

            check("offscreen: the frame rate starts at Electron's 60", wc.GetFrameRate() == 60, wc.GetFrameRate());
            wc.SetFrameRate(500);
            check("offscreen: SetFrameRate clamps to 240", wc.FrameRate == 240, wc.FrameRate);
            wc.SetFrameRate(10);
            await Task.Delay(500);
            var before = Volatile.Read(ref frames);
            await Task.Delay(2000);
            var slow = Volatile.Read(ref frames) - before;
            check("offscreen: at 10 frames a second, about 20 frames in 2 s", slow is >= 10 and <= 25, slow);
            wc.SetFrameRate(60);

            check("offscreen: IsPainting starts true", wc.IsPainting());
            wc.StopPainting();
            await Task.Delay(500);
            before = Volatile.Read(ref frames);
            await Task.Delay(1000);
            check("offscreen: StopPainting stops the frames", !wc.IsPainting() && Volatile.Read(ref frames) == before, Volatile.Read(ref frames) - before);
            wc.StartPainting();
            check("offscreen: StartPainting starts them again", wc.IsPainting() && Eventually(() => Volatile.Read(ref frames) > before + 5, step));

            // A still page paints nothing until invalidated, then the whole view.
            await wc.ExecuteJavaScriptAsync("for (let i = 1; i < 99999; i++) clearInterval(i); 0");
            await Task.Delay(500);
            before = Volatile.Read(ref frames);
            await Task.Delay(500);
            var still = Volatile.Read(ref frames) == before;
            wc.Invalidate();
            var invalidated = Eventually(() => Volatile.Read(ref frames) > before, step);
            check("offscreen: Invalidate repaints a still page whole", still && invalidated && last?.DirtyRect == new Rectangle(0, 0, 320, 240), $"{still} {invalidated} {last?.DirtyRect}");

            win.SetSize(400, 300);
            check("offscreen: a resized window's page paints at its new size",
                Eventually(() => last?.Image.GetSize() == (400, 300), step), last?.Image.GetSize());

            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            win.Closed += (_, _) => closed.TrySetResult();
            // The page closes before its script's result comes back, so that call fails; the window is what counts.
            _ = wc.ExecuteJavaScriptAsync("window.close()", userGesture: true).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            check("offscreen: the page's window.close() closes its window", await closed.Task.WaitAsync(step).ContinueWith(t => t.IsCompletedSuccessfully));
        }
        finally
        {
            if (!win.IsDestroyed) win.Destroy();
        }

        await PopupAsync(check, step);
        await ViewAsync(check, step);
        await SelectPopupAsync(check, step);

        var scaled = new BrowserWindow(new BrowserWindowOptions { Width = 200, Height = 200, Show = false, WebPreferences = new WebPreferences { Offscreen = new OffscreenOptions { DeviceScaleFactor = 1.5 } } });
        PaintEventArgs? scaledFrame = null;
        scaled.WebContents.Paint += (_, e) => scaledFrame = e;
        await scaled.WebContents.LoadUrl("data:text/html,<body style='background:red'>");
        var scaledPainted = Eventually(() => scaledFrame is not null, step);
        var pixels = scaledFrame is null ? (0, 0) : NativeImage.CreateFromBuffer(scaledFrame.Image.ToPng(1.5)).GetSize();
        check("offscreen: DeviceScaleFactor 1.5 paints 300x300 pixels, an image of 200x200 at scale 1.5 (Electron's spec, above Windows' minimum width)",
            scaledPainted && scaledFrame!.Image.GetSize() == (200, 200) && scaledFrame.Image.GetScaleFactors() is [1.5] && pixels == (300, 300),
            $"{scaledFrame?.Image.GetSize()} {string.Join(',', scaledFrame?.Image.GetScaleFactors() ?? [])} {pixels}");
        scaled.Destroy();

        var windowed = new BrowserWindow(new BrowserWindowOptions { Show = false });
        check("offscreen: a windowed page is not offscreen, and its painting calls do nothing",
            !windowed.WebContents.IsOffscreen() && !windowed.WebContents.IsPainting() && windowed.WebContents.GetFrameRate() == 0);
        windowed.Destroy();

        await SharedTextureAsync(check, step);
    }

    // A page an offscreen page opens is offscreen too, as Electron's popups take their opener's web preferences; making
    // it windowed is refused (drift C63); it closes with its opener.
    private static async Task PopupAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var dir = Directory.CreateTempSubdirectory("riftway-offscreen-popup").FullName;
        File.WriteAllText(Path.Combine(dir, "opener.html"), "<body style='background:red'>opener");
        File.WriteAllText(Path.Combine(dir, "popup.html"), "<body style='margin:0;background:lime'>popup");
        var opener = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Offscreen = new OffscreenOptions() } });
        var created = new TaskCompletionSource<BrowserWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCreated(object? sender, BrowserWindow w)
        {
            if (w != opener) created.TrySetResult(w);
        }
        RiftwayApp.Current.BrowserWindowCreated += OnCreated;
        try
        {
            await opener.WebContents.LoadFile(Path.Combine(dir, "opener.html"));
            var opened = await opener.WebContents.ExecuteJavaScriptAsync("window.open('popup.html', '_blank', 'width=250,height=180') ? 'opened' : 'null'", userGesture: true);
            var popup = await created.Task.WaitAsync(step).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
            var paints = 0;
            NativeImage? last = null;
            if (popup is not null) popup.WebContents.Paint += (_, e) => { Interlocked.Increment(ref paints); last = e.Image; };
            var painted = popup is not null && Eventually(() => last is { } image && image.GetSize().Width > 10 && IsGreen(image), step);
            check("offscreen: a page an offscreen page opens is offscreen, loads and paints",
                opened?.ToString() == "opened" && popup is not null && popup.WebContents.IsOffscreen() && popup.WebContents.GetUrl().EndsWith("popup.html", StringComparison.Ordinal) && painted,
                $"{opened} {popup?.WebContents.GetUrl()} paints={paints} {last?.GetSize()}");

            created = new TaskCompletionSource<BrowserWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            opener.WebContents.SetWindowOpenHandler(_ => WindowOpenHandlerResponse.Allow(o => o with { WebPreferences = o.WebPreferences with { Offscreen = null } }));
            var refused = await opener.WebContents.ExecuteJavaScriptAsync("window.open('popup.html', '_blank') ? 'opened' : 'null'", userGesture: true);
            await Task.Delay(1500);
            check("offscreen: making an offscreen page's popup windowed is refused", refused?.ToString() == "null" && !created.Task.IsCompleted, refused);

            opener.Destroy();
            check("offscreen: the popup closes with its opener", popup is not null && Eventually(() => popup.IsDestroyed, step));
        }
        finally
        {
            RiftwayApp.Current.BrowserWindowCreated -= OnCreated;
            if (!opener.IsDestroyed) opener.Destroy();
        }
    }

    // Electron's offscreen web contents in a WebContentsView: it exists and loads outside any window, paints at the outer
    // size of the window its view is in, follows that window's resizes, and closes on its own.
    private static async Task ViewAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var host = new BrowserWindow(new BrowserWindowOptions { Width = 360, Height = 260, Show = false });
        var view = new WebContentsView(new WebContentsViewOptions { WebPreferences = new WebPreferences { Offscreen = new OffscreenOptions() } });
        PaintEventArgs? last = null;
        view.WebContents.Paint += (_, e) => last = e;
        try
        {
            await view.WebContents.LoadUrl("data:text/html,<body style='margin:0;background:rgb(255,0,0)'>view");
            check("offscreen view: the page is offscreen and loads outside any window", view.WebContents.IsOffscreen() && view.WebContents.GetUrl().StartsWith("data:", StringComparison.Ordinal));

            host.ContentView.AddChildView(view);
            view.SetBounds(new Rectangle(10, 10, 100, 80));
            check("offscreen view: in a window it paints at the window's outer size, not the view's bounds",
                Eventually(() => last is { } frame && frame.Image.GetSize() == host.GetSize() && IsRed(frame.Image, 50, 50), step), $"{last?.Image.GetSize()} window {host.GetSize()}");

            host.SetSize(420, 300);
            check("offscreen view: it follows its window's resize", Eventually(() => last?.Image.GetSize() == (420, 300), step), last?.Image.GetSize());

            var destroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.WebContents.Destroyed += (_, _) => destroyed.TrySetResult();
            view.WebContents.Close();
            check("offscreen view: closing the page destroys it", await destroyed.Task.WaitAsync(step).ContinueWith(t => t.IsCompletedSuccessfully));
        }
        finally { host.Destroy(); }
    }

    // A <select>'s list is Chromium's popup widget, which CEF paints apart from the view; Electron's frames show it over
    // the page, so Riftway draws it in.
    private static async Task SelectPopupAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var win = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300, Show = false, WebPreferences = new WebPreferences { Offscreen = new OffscreenOptions() } });
        PaintEventArgs? last = null;
        var frames = 0;
        win.WebContents.Paint += (_, e) => { last = e; Interlocked.Increment(ref frames); };
        try
        {
            await win.WebContents.LoadUrl("data:text/html,<body style='margin:0;background:rgb(255,0,0)'>"
                + "<select id=s style='position:absolute;left:20px;top:20px;width:150px'>" + string.Concat(Enumerable.Range(1, 8).Select(i => $"<option>Option {i}</option>")) + "</select>");
            var settled = Eventually(() => last is { } f && IsRed(f.Image, 60, 90), step);
            win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseDown, 60, 30));
            win.WebContents.SendInputEvent(new MouseInputEvent(MouseInputEventType.MouseUp, 60, 30));
            var opened = Eventually(() => last is { } f && !IsRed(f.Image, 60, 90), step);
            check("offscreen: an open <select> list is drawn into the frame, over the page", settled && opened, last?.DirtyRect);

            win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyDown, "Escape"));
            win.WebContents.SendInputEvent(new KeyboardInputEvent(KeyboardInputEventType.KeyUp, "Escape"));
            check("offscreen: a closed <select> list leaves the page again", Eventually(() => last is { } f && IsRed(f.Image, 60, 90), step), last?.DirtyRect);
        }
        finally { win.Destroy(); }
    }

    // Electron's useSharedTexture: GPU frames in event.texture with an empty image. Windows: an NT handle to a D3D11
    // texture, live while the handler runs and handed back after it (drift C63).
    private static async Task SharedTextureAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                _ = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { Offscreen = new OffscreenOptions { UseSharedTexture = true } } });
                check("offscreen: UseSharedTexture throws off Windows", false);
            }
            catch (NotSupportedException) { check("offscreen: UseSharedTexture throws off Windows", true); }
            return;
        }
        var win = new BrowserWindow(new BrowserWindowOptions { Width = 320, Height = 240, Show = false, WebPreferences = new WebPreferences { Offscreen = new OffscreenOptions { UseSharedTexture = true } } });
        var frames = 0;
        string? seen = null;
        var liveDuring = true;
        OffscreenSharedTexture? kept = null;
        win.WebContents.Paint += (_, e) =>
        {
            if (e.Texture is not { } texture) { seen = "no texture"; return; }
            var info = texture.TextureInfo;
            liveDuring &= info.Handle.NtHandle != 0 && GetHandleInformation(info.Handle.NtHandle, out uint _) && !texture.IsReleased;
            seen = $"{info.WidgetType} {info.PixelFormat} {info.CodedSize} empty={e.Image.IsEmpty()}";
            kept = texture;
            Interlocked.Increment(ref frames);
        };
        try
        {
            await win.WebContents.LoadUrl("data:text/html,<body style='background:rgb(255,0,0)'><div id=d></div><script>let n=0;setInterval(()=>d.textContent=n++,50)</script>");
            var got = Eventually(() => Volatile.Read(ref frames) >= 10, step);
            check("offscreen: UseSharedTexture paints GPU frames with an empty image, Electron's textureInfo filled in",
                got && seen == $"frame bgra {win.GetSize()} empty=True", $"{frames} {seen} window {win.GetSize()}");
            check("offscreen: the texture's NT handle is live during the handler, and handed back after it", liveDuring && kept is { IsReleased: true }, $"{liveDuring} {kept?.IsReleased}");
        }
        finally { win.Destroy(); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetHandleInformation(nint handle, out uint flags);

    private static bool IsGreen(NativeImage image)
    {
        var (width, height) = image.GetSize();
        var bgra = image.ToBitmap();
        var i = (height / 2 * width + width / 2) * 4;
        return i + 2 < bgra.Length && bgra[i + 1] > 200 && bgra[i + 2] < 60 && bgra[i] < 60;
    }

    private static bool IsRed(NativeImage image, int x, int y)
    {
        var (width, _) = image.GetSize();
        var bgra = image.ToBitmap();
        var i = (y * width + x) * 4;
        return i + 2 < bgra.Length && bgra[i + 2] > 200 && bgra[i + 1] < 60 && bgra[i] < 60;
    }

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
