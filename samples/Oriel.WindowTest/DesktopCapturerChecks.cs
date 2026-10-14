using Oriel;
using Oriel.Cef.Platform;

/// <summary>desktopCapturer on each OS: a known Oriel window is listed by title with a thumbnail of the right size and
/// real pixels, screen sources carry Screen's display ids, and icons follow fetchWindowIcons.</summary>
static class DesktopCapturerChecks
{
    private const string Title = "Oriel desktopCapturer probe";
    private static readonly System.Drawing.Size s_box = new(320, 240);

    internal static async Task RunAsync(Action<string, bool, object?> check)
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } && Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 })
        {
            Console.WriteLine("SKIP  desktopCapturer: Wayland without X (PipeWire is drift DC2)");
            return;
        }
        var invalid = await Catch(() => DesktopCapturer.GetSourcesAsync(new SourcesOptions()));
        check("getSources without types fails with Electron's 'Invalid options'", invalid is ArgumentException { Message: "Invalid options" }, invalid);
        if (OperatingSystem.IsMacOS() && !MacDesktopCapture.HasScreenCaptureAccess())
        {
            var denied = await Catch(() => DesktopCapturer.GetSourcesAsync(new SourcesOptions { Types = ["screen"] }));
            check("macOS without screen recording: getSources fails with 'Failed to get sources.'", denied is InvalidOperationException { Message: "Failed to get sources." }, denied);
            return;
        }

        var probe = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300, Title = Title, BackgroundColor = "#20C040", Show = true });
        try
        {
            await probe.LoadUrl("data:text/html,<title>" + Uri.EscapeDataString(Title) + "</title><body style='margin:0;background:%2320C040'></body>");
            // X11 captures what is on screen; Openbox sometimes leaves a new window under the focused one.
            probe.Focus();
            for (var waited = 0; !probe.IsFocused && waited < 5000; waited += 100) await Task.Delay(100);
            await Task.Delay(500);

            var options = new SourcesOptions { Types = ["window", "screen"], ThumbnailSize = s_box, FetchWindowIcons = true };
            var first = DesktopCapturer.GetSourcesAsync(options);
            var second = DesktopCapturer.GetSourcesAsync(options with { });
            check("a getSources matching one still running shares its result", ReferenceEquals(first, second), null);
            var sources = await first;

            var window = sources.FirstOrDefault(s => s.Name == Title);
            check("the Oriel window is listed by its title", window is not null, string.Join(" | ", sources.Select(s => $"{s.Id} {s.Name}")));
            if (window is not null)
            {
                var parsed = DesktopMedia.TryParseId(window.Id, out var id);
                check("its id is a window DesktopMediaID", parsed && !id.IsScreen, window.Id);
                if (OperatingSystem.IsMacOS())
                    check("macOS: Chromium's own window carries its CGWindowID as the window id", id.WindowId == id.Id, window.Id);
                var (w, h) = window.Thumbnail.GetSize();
                var bounds = probe.GetBounds();
                var (ew, eh) = DesktopMedia.LetterboxSize(s_box.Width, s_box.Height, bounds.Width, bounds.Height);
                check("its thumbnail fits the box at the window's aspect", w <= s_box.Width && h <= s_box.Height && Math.Abs(w - ew) <= 2 && Math.Abs(h - eh) <= 2, $"{w}x{h} expected ~{ew}x{eh}");
                var bgra = window.Thumbnail.ToBitmap();
                var centre = ((h / 2) * w + w / 2) * 4;
                check("its thumbnail shows the window's green, opaque", bgra.Length == w * h * 4 && bgra[centre + 1] > 150 && bgra[centre + 2] < 80 && bgra[centre + 3] == 255,
                    bgra.Length >= centre + 4 ? $"{bgra[centre]},{bgra[centre + 1]},{bgra[centre + 2]},{bgra[centre + 3]}" : $"{bgra.Length} bytes");
                check("fetchWindowIcons gives windows an appIcon", window.AppIcon is not null, null);
                check("and its display_id is empty", window.DisplayId == "", window.DisplayId);
            }

            var screens = sources.Where(s => s.Id.StartsWith("screen:", StringComparison.Ordinal)).ToList();
            var displays = Screen.GetAllDisplays();
            check("one screen source per display", screens.Count == displays.Count, $"{screens.Count} vs {displays.Count}");
            check("screen sources' display_id match Screen.GetAllDisplays ids",
                screens.Select(s => s.DisplayId).OrderBy(x => x).SequenceEqual(displays.Select(d => d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).OrderBy(x => x)),
                $"{string.Join(",", screens.Select(s => s.DisplayId))} vs {string.Join(",", displays.Select(d => d.Id))}");
            check("screens are named as Chromium names them", screens.Count == 1 ? screens[0].Name == "Entire screen" : screens.Select((s, i) => s.Name == $"Screen {i + 1}").All(x => x),
                string.Join(",", screens.Select(s => s.Name)));
            check("screen sources have a thumbnail and no appIcon", screens.All(s => !s.Thumbnail.IsEmpty() && s.AppIcon is null), null);

            var bare = await DesktopCapturer.GetSourcesAsync(new SourcesOptions { Types = ["window"], ThumbnailSize = new(0, 0) });
            check("a 0 thumbnail size gives empty thumbnails and, without fetchWindowIcons, null appIcons",
                bare.Count > 0 && bare.All(s => s.Thumbnail.IsEmpty() && s.AppIcon is null), bare.Count);
        }
        finally { probe.Destroy(); }
    }

    private static async Task<Exception?> Catch(Func<Task> run)
    {
        try { await run(); return null; }
        catch (Exception e) { return e; }
    }
}
