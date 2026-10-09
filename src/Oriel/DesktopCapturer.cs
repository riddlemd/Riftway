using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What <see cref="DesktopCapturer.GetSourcesAsync"/> lists. Electron's <c>getSources</c> options.</summary>
public sealed record SourcesOptions
{
    /// <summary><c>screen</c> and/or <c>window</c>; other strings are ignored. Required.</summary>
    public IReadOnlyList<string>? Types { get; init; }

    /// <summary>The box thumbnails are scaled into, keeping their aspect; 150 × 150 when null. A 0 width or height
    /// skips thumbnails.</summary>
    public Size? ThumbnailSize { get; init; }

    /// <summary>Whether window sources carry their app's icon.</summary>
    public bool FetchWindowIcons { get; init; }
}

/// <summary>A screen or window that can be captured. Electron's <c>DesktopCapturerSource</c>.</summary>
/// <param name="Id"><c>window:XX:YY</c> or <c>screen:ZZ:0</c>, Chromium's <c>DesktopMediaID</c>: usable as a
/// <c>chromeMediaSourceId</c> constraint, and as a display media handler's video source.</param>
/// <param name="Name">The window's title; <c>Entire screen</c> or <c>Screen N</c> for a screen.</param>
/// <param name="Thumbnail">The thumbnail; empty when none was captured in time or none was asked for.</param>
/// <param name="DisplayId">For a screen, the matching <see cref="Display.Id"/>; empty when not known and for windows.</param>
/// <param name="AppIcon">The owning app's icon for a window when <see cref="SourcesOptions.FetchWindowIcons"/> is set
/// (empty when it has none); null otherwise and for screens.</param>
public sealed record DesktopCapturerSource(string Id, string Name, NativeImage Thumbnail, string DisplayId, NativeImage? AppIcon)
{
    /// <summary>The source as a display media handler's <see cref="DisplayMediaStreams.Video"/>, as Electron's handler
    /// takes a <c>DesktopCapturerSource</c>.</summary>
    public static implicit operator DisplayMediaSource(DesktopCapturerSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new DisplayMediaSource(source.Id, source.Name);
    }

    /// <summary>The same as the implicit conversion.</summary>
    public DisplayMediaSource ToDisplayMediaSource() => this;
}

/// <summary>Lists screens and windows that can be captured. Electron's <c>desktopCapturer</c>.</summary>
public static class DesktopCapturer
{
    // Electron's kDesktopCapturerReadyTimeout: lists still missing thumbnails are returned as they are.
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromSeconds(3);
    // ThumbnailCapturerMac: 100 windows streamed, 25 screenshots per one-second cycle.
    private const int MacMaxSources = 100, MacSourcesPerCycle = 25;
    private static readonly TimeSpan s_macCycle = TimeSpan.FromSeconds(1);

    private static readonly List<(Key Options, Task<IReadOnlyList<DesktopCapturerSource>> Sources)> s_running = [];

    private readonly record struct Key(bool Window, bool Screen, Size ThumbnailSize, bool FetchWindowIcons);

    /// <summary>The screens and windows that can be captured, as Electron's <c>getSources</c>: a request matching one
    /// still running shares its result. On macOS, asks for screen-recording access first and fails without it.</summary>
    /// <exception cref="ArgumentException"><see cref="SourcesOptions.Types"/> is missing (Electron's "Invalid options").</exception>
    /// <exception cref="InvalidOperationException">"Failed to get sources.": macOS screen recording is not allowed.</exception>
    public static Task<IReadOnlyList<DesktopCapturerSource>> GetSourcesAsync(SourcesOptions options)
    {
        if (options?.Types is not { } types) return Task.FromException<IReadOnlyList<DesktopCapturerSource>>(new ArgumentException("Invalid options"));
        var (window, screen) = Kinds(types);
        var key = new Key(window, screen, options.ThumbnailSize ?? new Size(DesktopMedia.DefaultThumbnailSize, DesktopMedia.DefaultThumbnailSize), options.FetchWindowIcons);
        lock (s_running)
        {
            foreach (var (running, task) in s_running)
                if (running == key) return task;
            var started = RunAsync(key);
            s_running.Add((key, started));
            _ = started.ContinueWith(_ => { lock (s_running) s_running.RemoveAll(r => r.Sources == started); }, TaskScheduler.Default);
            return started;
        }
    }

    /// <summary>Electron's <c>types.includes('window')</c> and <c>types.includes('screen')</c>.</summary>
    internal static (bool Window, bool Screen) Kinds(IReadOnlyList<string> types) => (types.Contains("window"), types.Contains("screen"));

    private static async Task<IReadOnlyList<DesktopCapturerSource>> RunAsync(Key key)
    {
        await Task.Yield();
        // ScreenCaptureKit flips a non-resizable window's style mask the first time it captures it; Electron puts it back.
        var resizable = OperatingSystem.IsMacOS() ? BrowserWindow.GetAllWindows().ToDictionary(w => w.Id, w => w.IsResizable) : null;
        try { return await CollectAsync(key).ConfigureAwait(false); }
        finally
        {
            if (resizable is not null)
                foreach (var window in BrowserWindow.GetAllWindows())
                    if (resizable.TryGetValue(window.Id, out var was) && window.IsResizable != was) window.SetResizable(was);
        }
    }

    private static async Task<IReadOnlyList<DesktopCapturerSource>> CollectAsync(Key key)
    {
        if (OperatingSystem.IsMacOS() && !MacDesktopCapture.RequestScreenCaptureAccess())
            throw new InvalidOperationException("Failed to get sources.");

        var lists = new List<ListRun>();
        var deadline = Task.Delay(s_readyTimeout);
        var box = key.ThumbnailSize;
        var needThumbnails = box.Width > 0 && box.Height > 0;
        var windows = key.Window ? new ListRun(screen: false, needThumbnails) : null;
        var screens = key.Screen ? new ListRun(screen: true, needThumbnails) : null;
        if (OperatingSystem.IsLinux())
        {
            // One X connection, one thread: Xlib is not thread-safe and its error handler is process-wide.
            var display = X11Window.OpenCaptureConnection();
            if (display != 0)
            {
                lists.AddRange(new[] { windows, screens }.OfType<ListRun>());
                _ = Task.Run(() => { if (OperatingSystem.IsLinux()) RunLinux(display, windows, screens, key); });
            }
        }
        else
        {
            foreach (var run in new[] { windows, screens }.OfType<ListRun>())
            {
                lists.Add(run);
                _ = Task.Run(() => ListAsync(run, key));
            }
        }

        var collected = new List<DesktopCapturerSource>();
        var pending = lists.ToList();
        // Electron appends each list as it becomes ready, then on the deadline the window list before the screen list.
        while (pending.Count > 0)
        {
            var next = await Task.WhenAny([.. pending.Select(r => (Task)r.Ready.Task), deadline]).ConfigureAwait(false);
            if (next == deadline) break;
            var run = pending.First(r => r.Ready.Task == next);
            pending.Remove(run);
            collected.AddRange(Finish(run.Ready.Task.Result, run.Screen, key));
        }
        foreach (var run in pending.OrderBy(r => r.Screen))
            collected.AddRange(Finish(run.Partial, run.Screen, key));
        return collected;
    }

    // A source as listed, with whatever thumbnail has arrived; filled in by the capture tasks until the list finishes.
    private sealed class Listed(CaptureSource source, string name)
    {
        public CaptureSource Source { get; } = source;
        public string Name { get; } = name;
        public volatile CaptureBitmap? Thumbnail;
        public CaptureBitmap? Icon;
    }

    // One of Electron's ListObservers: Partial is what a deadline takes; Ready completes when the list would notify.
    private sealed class ListRun(bool screen, bool needThumbnails)
    {
        public bool Screen { get; } = screen;
        public bool NeedThumbnails { get; } = needThumbnails;
        public TaskCompletionSource<IReadOnlyList<Listed>> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile IReadOnlyList<Listed> Partial = [];

        // Chromium's list is ready only once it has a source and, when asked for, every thumbnail.
        public void Done(List<Listed> items)
        {
            if (items.Count > 0 && (!NeedThumbnails || items.All(i => i.Thumbnail is not null))) Ready.TrySetResult(items);
        }
    }

    private static async Task ListAsync(ListRun run, Key key)
    {
        try
        {
            var items = Name(await EnumerateAsync(run.Screen).ConfigureAwait(false), run.Screen);
            run.Partial = items;
            if (run.NeedThumbnails && items.Count > 0) await ThumbnailsAsync(run.Screen, items, key.ThumbnailSize).ConfigureAwait(false);
            run.Done(items);
        }
        catch (Exception e) { Debug.WriteLine($"[Oriel] Listing desktop sources failed: {e}"); }
    }

    private static List<Listed> Name(IReadOnlyList<CaptureSource> sources, bool screen)
        => [.. sources.Select((s, i) => new Listed(s, screen ? DesktopMedia.ScreenName(i, sources.Count) : s.Title))];

    private static async Task<IReadOnlyList<CaptureSource>> EnumerateAsync(bool screen)
    {
        if (OperatingSystem.IsWindows()) return screen ? WindowsDesktopCapture.CaptureScreens() : WindowsDesktopCapture.CaptureWindows();
        if (!OperatingSystem.IsMacOS()) return [];
        if (MacDesktopCapture.UsesScreenCaptureKit)
        {
            if (await MacDesktopCapture.ShareableContentAsync().ConfigureAwait(false) is not { } content) return [];
            if (screen) return [.. content.Displays.Select(d => MacScreen(d.Id))];
            var displays = content.Displays.Select(d => (d.X, d.Y, d.Width, d.Height)).ToList();
            return [.. DesktopMedia.MacScreenCaptureKitWindows(content.Windows, MacDesktopCapture.OnScreenOrder(), displays).Select(MacWindow)];
        }
        return screen
            ? [.. MacDesktopCapture.ActiveDisplays().Select(MacScreen)]
            : [.. DesktopMedia.MacCoreGraphicsWindows(MacDesktopCapture.CoreGraphicsWindows()).Select(MacWindow)];
    }

    // On macOS the display ids of the two APIs match; Chromium registers its own windows' CGWindowIDs as their window id.
    private static CaptureSource MacScreen(uint id) => new(id, "", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static CaptureSource MacWindow(MacWindowEntry w) => new(w.Id, w.Title ?? "", "", w.Pid == Environment.ProcessId ? w.Id : 0);

    private static async Task ThumbnailsAsync(bool screen, List<Listed> items, Size box)
    {
        if (OperatingSystem.IsMacOS() && MacDesktopCapture.UsesScreenCaptureKit)
        {
            var results = new ConcurrentDictionary<uint, CaptureBitmap>();
            var selected = items.Take(MacMaxSources).ToList();
            for (var start = 0; start < selected.Count; start += MacSourcesPerCycle)
            {
                if (start > 0) await Task.Delay(s_macCycle).ConfigureAwait(false);
                var batch = selected.Skip(start).Take(MacSourcesPerCycle).ToList();
                var ids = batch.Select(i => (uint)i.Source.Id).ToList();
                await MacDesktopCapture.CaptureAsync(screen, ids, box.Width, box.Height, results).ConfigureAwait(false);
                foreach (var item in batch)
                    if (results.TryGetValue((uint)item.Source.Id, out var frame)) item.Thumbnail = DesktopMedia.ScaleThumbnail(frame, box.Width, box.Height);
            }
            return;
        }
        foreach (var item in items)
        {
            var frame = Capture(screen, item.Source.Id);
            if (frame is not null) item.Thumbnail = DesktopMedia.ScaleThumbnail(frame, box.Width, box.Height);
        }
    }

    private static CaptureBitmap? Capture(bool screen, long id)
    {
        if (OperatingSystem.IsWindows()) return screen ? WindowsDesktopCapture.CaptureScreenImage(id) : WindowsDesktopCapture.CaptureWindowImage(id);
        if (OperatingSystem.IsMacOS()) return MacDesktopCapture.CaptureCoreGraphics(screen, (uint)id);
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void RunLinux(nint display, ListRun? windows, ListRun? screens, Key key)
    {
        try { LinuxLists(display, windows, screens, key); }
        catch (Exception e) { Debug.WriteLine($"[Oriel] Listing desktop sources failed: {e}"); }
        finally { X11Window.CloseCaptureConnection(display); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void LinuxLists(nint display, ListRun? windows, ListRun? screens, Key key)
    {
        var windowItems = windows is null ? [] : Name(X11Window.CaptureWindows(display), screen: false);
        var screenItems = screens is null ? [] : Name(X11Window.CaptureScreens(display), screen: true);
        // Icons are read now, on the capture connection, as Finish may run after it closes.
        if (key.FetchWindowIcons)
            foreach (var item in windowItems) item.Icon = X11Window.CaptureWindowIcon(display, (nint)item.Source.Id);
        if (windows is not null) windows.Partial = windowItems;
        if (screens is not null) screens.Partial = screenItems;
        var box = key.ThumbnailSize;
        if (windows is { NeedThumbnails: true })
            foreach (var item in windowItems)
                if (X11Window.CaptureWindowImage(display, (nint)item.Source.Id) is { } frame) item.Thumbnail = DesktopMedia.ScaleThumbnail(frame, box.Width, box.Height);
        windows?.Done(windowItems);
        if (screens is { NeedThumbnails: true })
            foreach (var item in screenItems)
                if (X11Window.CaptureScreenImage(display, item.Source.Id) is { } frame) item.Thumbnail = DesktopMedia.ScaleThumbnail(frame, box.Width, box.Height);
        screens?.Done(screenItems);
    }

    private static IEnumerable<DesktopCapturerSource> Finish(IReadOnlyList<Listed> items, bool screen, Key key)
    {
        foreach (var item in items)
        {
            var id = DesktopMedia.FormatId(new DesktopMediaId(screen, item.Source.Id, item.Source.WindowId));
            NativeImage? icon = null;
            if (!screen && key.FetchWindowIcons) icon = Image(WindowIcon(item));
            yield return new DesktopCapturerSource(id, item.Name, Image(item.Thumbnail), screen ? item.Source.DisplayId : "", icon);
        }
    }

    private static CaptureBitmap? WindowIcon(Listed item)
    {
        if (OperatingSystem.IsLinux()) return item.Icon;
        if (OperatingSystem.IsWindows()) return WindowsDesktopCapture.WindowIcon(item.Source.Id);
        if (OperatingSystem.IsMacOS()) return MacDesktopCapture.WindowIcon((uint)item.Source.Id);
        return null;
    }

    private static NativeImage Image(CaptureBitmap? bitmap)
        => bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0
            ? NativeImage.CreateEmpty()
            : NativeImage.CreateFromBitmap(bitmap.Bgra, new NativeImageBitmapOptions { Width = bitmap.Width, Height = bitmap.Height });
}
