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
                screenIds.SequenceEqual(devices.Select(i => $"screen:{i}:0")), $"{string.Join(",", screenIds)} vs devices {string.Join(",", devices)}");
        }
        finally { cover.Destroy(); }
    }

    private static List<long> WindowsZOrder()
    {
        var order = new List<long>();
        EnumWindows((hwnd, _) => { order.Add(hwnd); return true; }, 0);
        return order;
    }

    private static List<int> ActiveDisplayDevices()
    {
        var active = new List<int>();
        for (var i = 0; ; i++)
        {
            var device = new DisplayDevice { Size = System.Runtime.InteropServices.Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;
            if ((device.StateFlags & 0x1 /* DISPLAY_DEVICE_ACTIVE */) != 0) active.Add(i);
        }
        return active;
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

    private static async Task<Exception?> Catch(Func<Task> run)
    {
        try { await run(); return null; }
        catch (Exception e) { return e; }
    }
}
