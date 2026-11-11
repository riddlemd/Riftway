using Riftway;

/// <summary>Electron's beginFrameSubscription and endFrameSubscription: frames while the page repaints, the page's own
/// pixels in them, a second subscriber replacing the first, and none after the end.</summary>
static class FrameSubscriptionTest
{
    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var win = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: frame subscription", Width = 320, Height = 240 });
        try
        {
            // A red page whose corner square changes colour every frame, so it keeps repainting.
            await win.WebContents.LoadUrl("data:text/html,<body style='margin:0;background:rgb(255,0,0)'><div id=d style='width:20px;height:20px'></div>"
                + "<script>let n=0;(function f(){d.style.background='rgb(0,0,'+(n++%256)+')';requestAnimationFrame(f)})()</script>");
            var first = 0;
            var second = 0;
            NativeImage? last = null;
            Rectangle lastDirty = default;
            win.WebContents.BeginFrameSubscription((image, dirty) => { Interlocked.Increment(ref first); last = image; lastDirty = dirty; });
            var got = Eventually(() => Volatile.Read(ref first) >= 10, step);
            var size = last?.GetSize() ?? (0, 0);
            check("frame subscription: a repainting page sends frames", got, first);
            check("frame subscription: a frame is the page, its dirty rectangle the whole frame", last is not null && size.Width > 0 && size.Height > 0
                && lastDirty == new Rectangle(0, 0, size.Width, size.Height) && IsRed(last, size.Width / 2, size.Height / 2), $"{size} {lastDirty}");

            win.WebContents.BeginFrameSubscription(true, (_, _) => Interlocked.Increment(ref second));
            await Task.Delay(300);
            var firstAfterReplace = Volatile.Read(ref first);
            var replaced = Eventually(() => Volatile.Read(ref second) >= 10, step);
            check("frame subscription: a second subscriber replaces the first", replaced && Volatile.Read(ref first) == firstAfterReplace, $"first {first} second {second}");

            win.WebContents.EndFrameSubscription();
            await Task.Delay(300);
            var atEnd = Volatile.Read(ref second);
            await Task.Delay(800);
            check("frame subscription: EndFrameSubscription stops the frames", Volatile.Read(ref second) == atEnd, $"{atEnd} then {second}");

            var again = 0;
            win.WebContents.BeginFrameSubscription((_, _) => Interlocked.Increment(ref again));
            check("frame subscription: a new subscription after the end gets frames again", Eventually(() => Volatile.Read(ref again) >= 1, step), again);
            win.WebContents.EndFrameSubscription();
        }
        finally { win.Destroy(); }
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
