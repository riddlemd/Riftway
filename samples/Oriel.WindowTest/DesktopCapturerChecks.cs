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

            if (OperatingSystem.IsWindows()) await WindowsRulesAsync(probe, check);
        }
        finally { probe.Destroy(); }
    }

    // The Windows-only rules: Chromium's merge of this process's windows into webrtc's list by EnumWindows z-order,
    // webrtc's screen ids as EnumDisplayDevices indices, PrintWindow thumbnails and WM_GETICON icons.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WindowsRulesAsync(BrowserWindow probe, Action<string, bool, object?> check)
    {
        var cover = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300, Title = Title + " cover", BackgroundColor = "#2040C0", Show = true });
        try
        {
            await cover.LoadUrl("data:text/html,<body style='margin:0;background:%232040C0'></body>");
            cover.SetBounds(probe.GetBounds());
            var red = new byte[32 * 32 * 4];
            for (var i = 0; i < red.Length; i += 4) (red[i], red[i + 1], red[i + 2], red[i + 3]) = (0, 0, 255, 255);
            probe.SetIcon(NativeImage.CreateFromBitmap(red, new NativeImageBitmapOptions { Width = 32, Height = 32 }));
            cover.MoveTop();
            await Task.Delay(500);

            var options = new SourcesOptions { Types = ["window", "screen"], ThumbnailSize = s_box, FetchWindowIcons = true };
            var sources = await DesktopCapturer.GetSourcesAsync(options);
            var zOrder = WindowsZOrder();
            var listed = sources.Select(s => DesktopMedia.TryParseId(s.Id, out var id) && !id.IsScreen ? id.Id : -1).Where(h => h != -1).ToList();
            var positions = listed.Select(h => zOrder.IndexOf(h)).ToList();
            var probeHwnd = (long)probe.GetNativeWindowHandle();
            var coverHwnd = (long)cover.GetNativeWindowHandle();
            var others = listed.Count(h => WindowsShell.ProcessOf((nint)h) != Environment.ProcessId);
            check("Windows: window sources come front to back in EnumWindows order, this process's merged among the others'",
                positions.All(p => p >= 0) && positions.Zip(positions.Skip(1)).All(p => p.First < p.Second) && listed.IndexOf(coverHwnd) is >= 0 and var c && c < listed.IndexOf(probeHwnd),
                $"{others} other-process windows; order {string.Join(",", positions)}; cover at {listed.IndexOf(coverHwnd)}, probe at {listed.IndexOf(probeHwnd)}");

            var probeSource = sources.FirstOrDefault(s => s.Name == Title);
            var thumb = probeSource?.Thumbnail.ToBitmap() ?? [];
            var (tw, th) = probeSource?.Thumbnail.GetSize() ?? (0, 0);
            var mid = ((th / 2) * tw + tw / 2) * 4;
            check("Windows: a covered window's thumbnail is its own pixels (PrintWindow), not what covers it",
                thumb.Length >= mid + 4 && thumb[mid + 1] > 150 && thumb[mid] < 80, thumb.Length >= mid + 4 ? $"{thumb[mid]},{thumb[mid + 1]},{thumb[mid + 2]}" : "none");
            var icon = probeSource?.AppIcon?.ToBitmap() ?? [];
            var (iw, ih) = probeSource?.AppIcon?.GetSize() ?? (0, 0);
            var iconMid = ((ih / 2) * iw + iw / 2) * 4;
            check("Windows: appIcon is the icon the window answers WM_GETICON with (SetIcon's red)",
                icon.Length >= iconMid + 4 && icon[iconMid + 2] > 200 && icon[iconMid + 1] < 60 && icon[iconMid] < 60, icon.Length >= iconMid + 4 ? $"{iw}x{ih} {icon[iconMid]},{icon[iconMid + 1]},{icon[iconMid + 2]}" : "none");

            var devices = ActiveDisplayDevices();
            var screenIds = sources.Where(s => s.Id.StartsWith("screen:", StringComparison.Ordinal)).Select(s => s.Id).ToList();
            check("Windows: screen ids are the active EnumDisplayDevices indices, in that order",
                screenIds.SequenceEqual(devices.Select(d => $"screen:{d.Index}:0")), $"{string.Join(",", screenIds)} vs devices {string.Join(",", devices.Select(d => d.Index))}");
            WindowsScreenSources(sources, devices, check);

            await WindowsOwnWindowsAsync(probe, check);
            await WindowsOtherProcessAsync(probe, cover, check);
            await WindowsThumbnailsAsync(check);
        }
        finally { cover.Destroy(); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsScreenSources(IReadOnlyList<DesktopCapturerSource> sources, List<(int Index, string Name, int Flags)> devices, Action<string, bool, object?> check)
    {
        var primary = Screen.GetPrimaryDisplay().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var primaryDevice = devices.FindIndex(d => (d.Flags & 0x4 /* DISPLAY_DEVICE_PRIMARY_DEVICE */) != 0);
        var primarySource = primaryDevice < 0 ? null : sources.FirstOrDefault(s => s.Id == $"screen:{devices[primaryDevice].Index}:0");
        check("Windows: the primary display device's screen source carries Screen.GetPrimaryDisplay's id",
            primarySource is not null && primarySource.DisplayId == primary, $"{primarySource?.Id ?? "no primary device"} {primarySource?.DisplayId} vs {primary}");

        var displays = Screen.GetAllDisplays();
        var sized = true;
        var detail = new List<string>();
        foreach (var source in sources.Where(s => s.Id.StartsWith("screen:", StringComparison.Ordinal)))
        {
            var device = devices.FirstOrDefault(d => $"screen:{d.Index}:0" == source.Id);
            var mode = device.Name is null ? null : CurrentMode(device.Name);
            var display = displays.FirstOrDefault(d => d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == source.DisplayId);
            sized &= mode is { } m && display is not null
                && Math.Abs(display.Bounds.Width * display.ScaleFactor - m.Width) <= 1 && Math.Abs(display.Bounds.Height * display.ScaleFactor - m.Height) <= 1;
            detail.Add($"{source.Id} mode {mode?.Width}x{mode?.Height} display {display?.Bounds.Width}x{display?.Bounds.Height}@{display?.ScaleFactor}");
        }
        check("Windows: each screen source's display is the size of its device's current mode", sized && detail.Count > 0, string.Join("; ", detail));
    }

    // This process's windows go through Chromium's own collector, not webrtc's, so they are made on a second UI thread
    // that pumps: Oriel asks each one WM_NULL and WM_GETTEXT.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WindowsOwnWindowsAsync(BrowserWindow probe, Action<string, bool, object?> check)
    {
        var longTitle = "Oriel dc long " + new string('x', 586);
        var (x, y, _, _) = WindowsShell.Bounds(probe.GetNativeWindowHandle());
        using var popups = new Popups(x, y, p =>
        {
            var shown = p.Add("Oriel dc shown");
            p.Add("Oriel dc hidden", visible: false);
            _ = ShowWindow(p.Add("Oriel dc minimized"), 7 /* SW_SHOWMINNOACTIVE */);
            p.Add("");
            p.Add("Oriel dc owned", owner: shown);
            p.Add("Oriel dc owned app", exStyle: 0x00040000 /* WS_EX_APPWINDOW */, owner: shown);
            var cloak = 1;
            _ = DwmSetWindowAttribute(p.Add("Oriel dc cloaked"), 13 /* DWMWA_CLOAK */, ref cloak, sizeof(int));
            p.Add(longTitle);
            var big = p.Add("Oriel dc big icon");
            p.SetIcon(big, 1 /* ICON_BIG */, 32, 0, 0, 255);
            p.SetIcon(big, 0 /* ICON_SMALL */, 16, 255, 0, 0);
            p.SetIcon(p.Add("Oriel dc class icon", classIcon: true), 0 /* ICON_SMALL */, 16, 255, 0, 0);
            p.Add("Oriel dc no icon");
        });
        await Task.Delay(300);

        var sources = await DesktopCapturer.GetSourcesAsync(new SourcesOptions { Types = ["window"], ThumbnailSize = new(0, 0), FetchWindowIcons = true });
        var listed = sources.Select(Handle).ToList();
        var ours = listed.Where(h => WindowsShell.ProcessOf((nint)h) == Environment.ProcessId).ToList();
        var expected = OwnCapturableWindows();
        long[] kept = [popups["Oriel dc shown"], popups["Oriel dc owned app"]];
        long[] left = [popups["Oriel dc hidden"], popups["Oriel dc minimized"], popups[""], popups["Oriel dc owned"]];
        check("Windows: this process's windows follow Chromium's collector: hidden, minimized, untitled and owned ones left out, an owned WS_EX_APPWINDOW one kept",
            ours.SequenceEqual(expected) && kept.All(expected.Contains) && !left.Any(expected.Contains), $"listed {string.Join(",", ours)} vs EnumWindows {string.Join(",", expected)}");

        var cloaked = popups["Oriel dc cloaked"];
        var cloakState = 0;
        var isCloaked = DwmGetWindowAttribute(cloaked, 14 /* DWMWA_CLOAKED */, out cloakState, sizeof(int)) == 0 && cloakState != 0;
        check("Windows: a cloaked window of this process is still listed, as Chromium's collector does not ask DWM",
            isCloaked && listed.Contains(cloaked), $"DWMWA_CLOAKED {cloakState}, listed {listed.Contains(cloaked)}");

        var fullTitle = WindowText(popups[longTitle]);
        var longSource = sources.FirstOrDefault(s => Handle(s) == popups[longTitle]);
        check("Windows: this process's window titles are read in full, past webrtc's 500 characters",
            fullTitle.Length == 600 && longSource?.Name == fullTitle, $"{longSource?.Name.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not listed"} of {fullTitle.Length} characters");

        var bigIcon = Centre(sources.FirstOrDefault(s => Handle(s) == popups["Oriel dc big icon"])?.AppIcon);
        check("Windows: appIcon prefers WM_GETICON's big icon (red, 32px) to its small one (blue, 16px)",
            GetIcon(popups["Oriel dc big icon"], 1) != 0 && bigIcon is { W: 32, H: 32, R: > 200, G: < 60, B: < 60 }, bigIcon);
        var classHwnd = popups["Oriel dc class icon"];
        var classIcon = Centre(sources.FirstOrDefault(s => Handle(s) == classHwnd)?.AppIcon);
        check("Windows: appIcon takes the class icon (green) before WM_GETICON's small one (blue)",
            GetIcon(classHwnd, 1) == 0 && GetClassLongPtrW(classHwnd, -14 /* GCLP_HICON */) != 0 && classIcon is { G: > 200, R: < 60, B: < 60 }, classIcon);
        var bare = popups["Oriel dc no icon"];
        var bareSource = sources.FirstOrDefault(s => Handle(s) == bare);
        check("Windows: a window without any icon gets an empty appIcon, not null",
            GetIcon(bare, 1) == 0 && GetIcon(bare, 0) == 0 && GetIcon(bare, 2) == 0 && GetClassLongPtrW(bare, -14) == 0 && GetClassLongPtrW(bare, -34 /* GCLP_HICONSM */) == 0
                && bareSource?.AppIcon is { } none && none.IsEmpty(),
            bareSource is null ? "not listed" : bareSource.AppIcon is null ? "null" : (object)bareSource.AppIcon.GetSize());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WindowsOtherProcessAsync(BrowserWindow probe, BrowserWindow cover, Action<string, bool, object?> check)
    {
        const string otherTitle = "Oriel dc other";
        var (x, y, _, _) = WindowsShell.Bounds(probe.GetNativeWindowHandle());
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; $f.Text = '{otherTitle}'; $f.StartPosition = 'Manual'; " +
            $"$f.Left = {x + 40}; $f.Top = {y + 40}; $f.Width = 300; $f.Height = 200; [System.Windows.Forms.Application]::Run($f)" })
            start.ArgumentList.Add(arg);
        using var other = System.Diagnostics.Process.Start(start)!;
        try
        {
            nint hwnd = 0;
            for (var waited = 0; hwnd == 0 && waited < 30000; waited += 200)
            {
                await Task.Delay(200);
                hwnd = FindWindowW(null, otherTitle);
                if (hwnd != 0 && WindowsShell.ProcessOf(hwnd) != other.Id) hwnd = 0;
            }
            if (hwnd == 0)
            {
                check("Windows: another process's window stacked between two of ours is listed between them", false, "powershell's form never showed");
                return;
            }
            var (coverHwnd, probeHwnd, otherHwnd) = ((long)cover.GetNativeWindowHandle(), (long)probe.GetNativeWindowHandle(), (long)hwnd);
            const uint zOrderOnly = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
            _ = SetWindowPos(hwnd, (nint)coverHwnd, 0, 0, 0, 0, zOrderOnly);
            _ = SetWindowPos((nint)probeHwnd, hwnd, 0, 0, 0, 0, zOrderOnly);
            await Task.Delay(300);

            var zOrder = WindowsZOrder();
            var stacked = zOrder.IndexOf(coverHwnd) is >= 0 and var zc && zOrder.IndexOf(otherHwnd) is var zo && zo > zc && zOrder.IndexOf(probeHwnd) > zo;
            var listed = (await DesktopCapturer.GetSourcesAsync(new SourcesOptions { Types = ["window"], ThumbnailSize = new(0, 0) })).Select(Handle).ToList();
            check("Windows: another process's window stacked between two of ours is listed between them",
                stacked && listed.IndexOf(coverHwnd) is >= 0 and var c && listed.IndexOf(otherHwnd) is var o && o > c && listed.IndexOf(probeHwnd) > o,
                $"EnumWindows stacked {stacked}; listed cover {listed.IndexOf(coverHwnd)}, other {listed.IndexOf(otherHwnd)}, probe {listed.IndexOf(probeHwnd)}");

            _ = ShowWindowAsync(hwnd, 6 /* SW_MINIMIZE */);
            for (var waited = 0; !IsIconic(hwnd) && waited < 5000; waited += 100) await Task.Delay(100);
            listed = [.. (await DesktopCapturer.GetSourcesAsync(new SourcesOptions { Types = ["window"], ThumbnailSize = new(0, 0) })).Select(Handle)];
            check("Windows: and once minimized it is not listed", IsIconic(hwnd) && !listed.Contains(otherHwnd), $"iconic {IsIconic(hwnd)}, listed at {listed.IndexOf(otherHwnd)}");
        }
        finally
        {
            other.Kill(entireProcessTree: true);
            other.WaitForExit(5000);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WindowsThumbnailsAsync(Action<string, bool, object?> check)
    {
        var gradient = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300, Title = Title + " gradient", Frame = false, AlwaysOnTop = true, Show = true });
        var area = Screen.GetPrimaryDisplay().WorkArea;
        var marker = new BrowserWindow(new BrowserWindowOptions
        {
            X = area.X + 60, Y = area.Y + 60, Width = 300, Height = 200, Title = Title + " marker", Frame = false, AlwaysOnTop = true, BackgroundColor = "#C020C0", Show = true,
        });
        try
        {
            await gradient.LoadUrl("data:text/html,<title>" + Uri.EscapeDataString(Title + " gradient") +
                "</title><html style='height:100%25'><body style='margin:0;height:100%25;background:linear-gradient(%23C02020 50%25,%232040C0 50%25)'></body></html>");
            await marker.LoadUrl("data:text/html,<body style='margin:0;background:%23C020C0'></body>");
            // The marker sits top-left on the primary display, so the gradient goes to its far side, clear of it.
            gradient.SetBounds(new Rectangle(area.X + area.Width - 460, area.Y + area.Height - 360, 400, 300));
            await Task.Delay(800);

            var hwnd = gradient.GetNativeWindowHandle();
            var (x, y, w, h) = WindowsShell.Bounds(hwnd);
            var top = WindowsShell.ScreenPixel(x + w / 2, y + h / 4);
            var bottom = WindowsShell.ScreenPixel(x + w / 2, y + h * 3 / 4);
            var windows = await DesktopCapturer.GetSourcesAsync(new SourcesOptions { Types = ["window"], ThumbnailSize = s_box });
            var thumbnail = windows.FirstOrDefault(s => Handle(s) == hwnd)?.Thumbnail;
            var thumbTop = Pixel(thumbnail, 0.5, 0.25);
            var thumbBottom = Pixel(thumbnail, 0.5, 0.75);
            check("Windows: window thumbnails are upright and in colour order: the top and bottom match the screen's",
                top is { R: > 150, B: < 80 } && bottom is { B: > 150, R: < 80 } && Near(top, thumbTop) && Near(bottom, thumbBottom),
                $"screen {top}/{bottom} thumbnail {thumbTop}/{thumbBottom}");

            var (mx, my, mw, mh) = WindowsShell.Bounds(marker.GetNativeWindowHandle());
            var (cx, cy) = (mx + mw / 2, my + mh / 2);
            var onScreen = WindowsShell.ScreenPixel(cx, cy);
            var device = ActiveDisplayDevices().Select(d => (d.Index, Mode: CurrentMode(d.Name)))
                .FirstOrDefault(d => d.Mode is { } m && cx >= m.X && cx < m.X + m.Width && cy >= m.Y && cy < m.Y + m.Height);
            var screens = await DesktopCapturer.GetSourcesAsync(new SourcesOptions { Types = ["screen"], ThumbnailSize = s_box });
            var screen = device.Mode is null ? null : screens.FirstOrDefault(s => s.Id == $"screen:{device.Index}:0")?.Thumbnail;
            var mode = device.Mode ?? default;
            var shown = mode.Width == 0 ? default : Pixel(screen, (cx - mode.X + 0.5) / mode.Width, (cy - mode.Y + 0.5) / mode.Height);
            check("Windows: a screen thumbnail shows a window where it sits on that screen",
                onScreen is { R: > 150, G: < 80, B: > 150 } && Near(onScreen, shown), $"screen:{device.Index} {onScreen} thumbnail {shown}");
            var (tw, th) = screen?.GetSize() ?? (0, 0);
            var fit = mode.Width == 0 ? 0 : Math.Min((double)s_box.Width / mode.Width, (double)s_box.Height / mode.Height);
            var (ew, eh) = ((int)Math.Round(mode.Width * fit), (int)Math.Round(mode.Height * fit));
            check("Windows: a screen thumbnail is its mode's size fitted into the box", ew > 0 && Math.Abs(tw - ew) <= 1 && Math.Abs(th - eh) <= 1, $"{tw}x{th} expected {ew}x{eh}");
        }
        finally
        {
            gradient.Destroy();
            marker.Destroy();
        }
    }

    private static long Handle(DesktopCapturerSource source) => DesktopMedia.TryParseId(source.Id, out var id) && !id.IsScreen ? id.Id : -1;

    private static (byte R, byte G, byte B) Pixel(NativeImage? image, double fx, double fy)
    {
        var (w, h) = image?.GetSize() ?? (0, 0);
        if (image is null || w == 0 || h == 0) return default;
        var bgra = image.ToBitmap();
        var i = ((int)(fy * h) * w + (int)(fx * w)) * 4;
        return i + 4 <= bgra.Length ? (bgra[i + 2], bgra[i + 1], bgra[i]) : default;
    }

    private static bool Near((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
        => Math.Abs(a.R - b.R) <= 40 && Math.Abs(a.G - b.G) <= 40 && Math.Abs(a.B - b.B) <= 40;

    private static (int W, int H, byte R, byte G, byte B)? Centre(NativeImage? image)
    {
        var (w, h) = image?.GetSize() ?? (0, 0);
        if (image is null || w == 0 || h == 0) return null;
        var (r, g, b) = Pixel(image, 0.5, 0.5);
        return (w, h, r, g, b);
    }

    // Chromium's CapturableCurrentProcessHwndCollector and its UI-thread title test, walked here with Win32 alone.
    private static List<long> OwnCapturableWindows()
        => [.. WindowsZOrder().Where(h =>
        {
            var hwnd = (nint)h;
            if (WindowsShell.ProcessOf(hwnd) != Environment.ProcessId || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
            if (GetWindow(hwnd, 4 /* GW_OWNER */) != 0 && (GetWindowLongW(hwnd, -20 /* GWL_EXSTYLE */) & 0x00040000 /* WS_EX_APPWINDOW */) == 0) return false;
            return GetWindowTextLengthW(hwnd) > 0;
        })];

    private static string WindowText(nint hwnd)
    {
        var buffer = new char[GetWindowTextLengthW(hwnd) + 1];
        return new string(buffer, 0, GetWindowTextW(hwnd, buffer, buffer.Length));
    }

    private static nint GetIcon(nint hwnd, int which)
    {
        _ = SendMessageTimeoutW(hwnd, 0x007F /* WM_GETICON */, which, 0, 0x2 /* SMTO_ABORTIFHUNG */, 1000, out var icon);
        return icon;
    }

    private static (int X, int Y, int Width, int Height)? CurrentMode(string device)
    {
        var mode = new DevMode { Size = (short)System.Runtime.InteropServices.Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettingsW(device, -1 /* ENUM_CURRENT_SETTINGS */, ref mode) ? (mode.PositionX, mode.PositionY, mode.PelsWidth, mode.PelsHeight) : null;
    }

    private static List<long> WindowsZOrder()
    {
        var order = new List<long>();
        EnumWindows((hwnd, _) => { order.Add(hwnd); return true; }, 0);
        return order;
    }

    private static List<(int Index, string Name, int Flags)> ActiveDisplayDevices()
    {
        var active = new List<(int, string, int)>();
        for (var i = 0; ; i++)
        {
            var device = new DisplayDevice { Size = System.Runtime.InteropServices.Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;
            if ((device.StateFlags & 0x1 /* DISPLAY_DEVICE_ACTIVE */) != 0) active.Add((i, device.DeviceName, device.StateFlags));
        }
        return active;
    }

    /// <summary>Top-level popups owned by a thread of their own that pumps messages, as Chromium's other UI windows
    /// would be; destroyed with the thread.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class Popups : IDisposable
    {
        private const string PlainClass = "OrielDcPopup", IconClass = "OrielDcClassIcon";
        private readonly Dictionary<string, nint> _byTitle = [];
        private readonly List<nint> _icons = [];
        private readonly Thread _thread;
        private readonly int _x, _y;
        private uint _threadId;

        internal Popups(int x, int y, Action<Popups> build)
        {
            (_x, _y) = (x, y);
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                Register(PlainClass, 0);
                Register(IconClass, NewIcon(32, 0, 255, 0));
                try { build(this); }
                finally { ready.Set(); }
                while (GetMessageW(out var msg, 0, 0, 0) > 0) _ = DispatchMessageW(ref msg);
                foreach (var hwnd in _byTitle.Values) _ = DestroyWindow(hwnd);
                _ = UnregisterClassW(PlainClass, GetModuleHandleW(null));
                _ = UnregisterClassW(IconClass, GetModuleHandleW(null));
                foreach (var icon in _icons) _ = DestroyIcon(icon);
            }) { IsBackground = true, Name = "desktopCapturer popups" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
        }

        internal nint this[string title] => _byTitle[title];

        internal nint Add(string title, uint exStyle = 0, bool visible = true, nint owner = 0, bool classIcon = false)
        {
            var offset = 24 * _byTitle.Count;
            var hwnd = CreateWindowExW(exStyle | 0x08000000 /* WS_EX_NOACTIVATE */, classIcon ? IconClass : PlainClass, title,
                0x80800000 /* POPUP | BORDER */ | (visible ? 0x10000000u /* VISIBLE */ : 0), _x + offset, _y + offset, 160, 120, owner, 0, GetModuleHandleW(null), 0);
            _byTitle[title] = hwnd;
            return hwnd;
        }

        internal void SetIcon(nint hwnd, int which, int size, byte b, byte g, byte r)
            => _ = SendMessageW(hwnd, 0x0080 /* WM_SETICON */, which, NewIcon(size, b, g, r));

        private nint NewIcon(int size, byte b, byte g, byte r)
        {
            var bgra = new byte[size * size * 4];
            for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (b, g, r, 255);
            var colour = CreateBitmap(size, size, 1, 32, bgra);
            var mask = CreateBitmap(size, size, 1, 1, new byte[size * size]);
            var info = new IconInfo { IsIcon = 1, Mask = mask, Colour = colour };
            var icon = CreateIconIndirect(ref info);
            _ = DeleteObject(colour);
            _ = DeleteObject(mask);
            _icons.Add(icon);
            return icon;
        }

        // DefWindowProc itself as the window procedure: these windows only need to answer the system's messages.
        private static void Register(string name, nint icon)
        {
            var user32 = System.Runtime.InteropServices.NativeLibrary.Load("user32.dll");
            var cls = new WndClass
            {
                WndProc = System.Runtime.InteropServices.NativeLibrary.GetExport(user32, "DefWindowProcW"),
                Instance = GetModuleHandleW(null), Icon = icon, Background = 6 /* COLOR_WINDOW + 1 */, ClassName = name,
            };
            _ = RegisterClassW(ref cls);
        }

        public void Dispose()
        {
            _ = PostThreadMessageW(_threadId, 0x0012 /* WM_QUIT */, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint Mask;
        public nint Colour;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DevMode
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint param);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool EnumDisplayDevicesW(string? device, int index, ref DisplayDevice info, int flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetClassLongPtrW(nint hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, char[] text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string? name);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern ushort RegisterClassW(ref WndClass cls);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool UnregisterClassW(string cls, nint instance);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Msg msg);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint CreateIconIndirect(ref IconInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    private static async Task<Exception?> Catch(Func<Task> run)
    {
        try { await run(); return null; }
        catch (Exception e) { return e; }
    }
}
