using Oriel.Cef;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

public unsafe partial class BaseWindow
{
    /// <summary>Sets the window and taskbar icon from a PNG or JPEG file (Windows, Linux). Electron's <c>win.setIcon()</c>.</summary>
    public void SetIcon(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        SetIcon(NativeImage.CreateFromBuffer(File.ReadAllBytes(path)));
    }

    /// <summary>Sets the window and taskbar icon, every scale factor of it (Windows, Linux). Electron's <c>win.setIcon()</c>.</summary>
    public void SetIcon(NativeImage icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        Command(w => SetIconOn(w, icon));
    }

    private static void SetIconOn(cef_window_t* w, NativeImage icon)
    {
        var image = icon.ToCefImage();
        NativeObject.AddRef(image);
        w->set_window_icon(w, image);
        NativeObject.AddRef(image);
        w->set_window_app_icon(w, image);
        NativeObject.Release(image);
    }

    /// <summary>Shows progress on the taskbar button, from 0 to 1; below 0 removes it and above 1 shows it as
    /// indeterminate. Electron's <c>win.setProgressBar()</c>.</summary>
    /// <remarks>macOS: on the Dock icon, which all windows share; error and paused show as the plain bar.
    /// Linux: a Unity launcher entry signal for the whole app, as Electron's: the raw value, shown only inside (0, 1),
    /// whatever the mode; only some desktops show it.</remarks>
    public void SetProgressBar(double progress, ProgressBarOptions? options = null)
    {
        var (state, percent) = ProgressBar.Map(progress, options?.Mode ?? ProgressBarMode.Normal);
        Command(w =>
        {
            if (OperatingSystem.IsWindows() && !WindowsTaskbar.SetProgress(Hwnd(w), state, percent))
                Console.Error.WriteLine("[Oriel] The taskbar did not take the progress bar.");
            // One Dock tile for the whole application; error and paused show as the plain bar.
            if (OperatingSystem.IsMacOS())
                MacDock.SetProgress(state == TaskbarProgressState.None ? null : percent / 100.0, state == TaskbarProgressState.Indeterminate);
            if (OperatingSystem.IsLinux()) _ = LinuxLauncherEntry.SetProgress(progress);
        });
    }

    /// <summary>Flashes the title bar and taskbar button to get the user's attention, or stops. Electron's <c>win.flashFrame()</c>.</summary>
    /// <remarks>macOS: bounces the Dock icon once, only while the application is not active. Linux: sets the
    /// window's urgency hint, which the window manager shows its own way.</remarks>
    public void FlashFrame(bool flag) => Command(w =>
    {
        if (OperatingSystem.IsWindows()) WindowsTaskbar.FlashFrame(Hwnd(w), flag);
        if (OperatingSystem.IsLinux() && !X11Window.SetUrgency(Hwnd(w), flag)) Console.Error.WriteLine("[Oriel] No X display for FlashFrame.");
        if (OperatingSystem.IsMacOS())
        {
            MacDock.CancelAttention(_attentionRequest);
            _attentionRequest = flag ? MacDock.RequestAttention() : 0;
        }
    });

    /// <summary>Removes the window's taskbar button, or restores it (Windows, Linux; macOS has none per window).
    /// Electron's <c>win.setSkipTaskbar()</c>.</summary>
    public void SetSkipTaskbar(bool skip) => Command(w =>
    {
        var changed = _skipTaskbar != skip;
        _skipTaskbar = skip;
        if (changed || skip) ApplySkipTaskbar(w, restore: !skip);
    });

    /// <summary>Shows a small image over the taskbar icon, with <paramref name="description"/> for screen readers; null
    /// removes it. Windows only for now: does nothing on macOS and Linux. Electron's <c>win.setOverlayIcon()</c>.</summary>
    public void SetOverlayIcon(NativeImage? overlay, string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        Command(w =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var hwnd = Hwnd(w);
            var done = overlay is null ? WindowsTaskbar.ClearOverlayIcon(hwnd, description) : SetOverlayIconOn(hwnd, overlay, description);
            if (!done) Console.Error.WriteLine("[Oriel] The taskbar did not take the overlay icon.");
        });
    }

    /// <summary>Sets the buttons in the window's taskbar thumbnail, at most seven; an empty list removes them. False if
    /// there are more than seven, the shell refused, or not on Windows. Electron's <c>win.setThumbarButtons()</c>.</summary>
    /// <remarks>Call it once the window is shown: the shell adds the toolbar to an existing taskbar button.</remarks>
    /// <exception cref="ArgumentException">A button or its icon is null.</exception>
    public bool SetThumbarButtons(IReadOnlyList<ThumbarButton> buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        if (buttons.Any(b => b?.Icon is null)) throw new ArgumentException("Every thumbar button needs an icon.", nameof(buttons));
        var copy = buttons.ToArray();
        return Query(w => OperatingSystem.IsWindows() && SetThumbarButtonsOn(w, copy), false);
    }

    /// <summary>Sets the window's App User Model ID and relaunch details, which decide how the taskbar groups and pins
    /// it; empty options are left as they are. Windows only: does nothing on macOS and Linux.
    /// Electron's <c>win.setAppDetails()</c>.</summary>
    public void SetAppDetails(AppDetailsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var values = AppDetails.Plan(options);
        Command(w =>
        {
            if (OperatingSystem.IsWindows() && values.Count > 0 && !WindowsWindow.SetAppUserModel(Hwnd(w), values))
                Console.Error.WriteLine("[Oriel] The window's property store did not take the app details.");
        });
    }

    /// <summary>Shows only <paramref name="region"/> of the window (DIPs, from the client area's top left) in its
    /// taskbar thumbnail; an empty region shows the whole window. Windows only: does nothing on macOS and Linux.
    /// Electron's <c>win.setThumbnailClip()</c>.</summary>
    public void SetThumbnailClip(Rectangle region) => Command(w =>
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = Hwnd(w);
        WindowsTaskbar.SetThumbnailClip(hwnd, ClipToPixels(w, region), done =>
        {
            if (!done) Console.Error.WriteLine("[Oriel] The taskbar did not take the thumbnail clip.");
        });
    });

    /// <summary>Sets the tooltip shown over the window's taskbar thumbnail; empty restores the title.
    /// Windows only: does nothing on macOS and Linux. Electron's <c>win.setThumbnailToolTip()</c>.</summary>
    public void SetThumbnailToolTip(string toolTip)
    {
        ArgumentNullException.ThrowIfNull(toolTip);
        Command(w =>
        {
            if (OperatingSystem.IsWindows() && !WindowsTaskbar.SetThumbnailToolTip(Hwnd(w), toolTip))
                Console.Error.WriteLine("[Oriel] The taskbar did not take the thumbnail tooltip.");
        });
    }

    // Electron's TaskbarHost::SetThumbarButtons, including its order: the callbacks are replaced before a bad flag
    // stops the call, and the toolbar counts as added whatever the shell answers.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool SetThumbarButtonsOn(cef_window_t* w, IReadOnlyList<ThumbarButton> buttons)
    {
        if (Thumbar.Plan(buttons) is not { } plan || !WindowsTaskbar.IsAvailable) return false;
        _thumbarClicks.Clear();
        for (var i = 0; i < (plan.InvalidAt ?? buttons.Count); i++) _thumbarClicks[(int)plan.Slots[i].Id] = buttons[i].Click;
        if (plan.InvalidAt is not null) return false;
        var icons = plan.Slots.Select(s => (s.Mask & Thumbar.MaskIcon) != 0 ? StraightIcon(s.Button!.Icon) : 0).ToArray();
        var native = plan.Slots.Select((s, i) => new ThumbButton(s.Id, icons[i], s.Button?.Tooltip, s.Flags, s.Mask)).ToArray();
        var ok = WindowsTaskbar.SetThumbButtons(Hwnd(w), native, add: !_thumbarAdded);
        _thumbarAdded = true;
        _lastThumbarButtons = buttons;
        // A slot sent without an icon keeps the shell's previous one, so its handle stays alive.
        for (var i = 0; i < Thumbar.MaxButtons; i++)
        {
            if (icons[i] == 0) continue;
            WindowsTaskbar.DestroyIconHandle(_thumbarIcons[i]);
            _thumbarIcons[i] = icons[i];
        }
        return ok;
    }

    // Electron's HandleThumbarButtonEvent: a known id is handled even without a callback.
    private bool OnThumbarClick(int id)
    {
        if (!_thumbarClicks.TryGetValue(id, out var click)) return false;
        click?.Invoke();
        return true;
    }

    // Electron's RestoreThumbarButtons: Explorer restarted, so the toolbar is added again.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RestoreThumbarButtons(cef_window_t* w)
    {
        if (!_thumbarAdded) return;
        _thumbarAdded = false;
        SetThumbarButtonsOn(w, _lastThumbarButtons);
    }

    // DIPToScreenRect(hwnd, region) as Electron calls it: the origin goes through the window's display as a screen point.
    private static (int Left, int Top, int Right, int Bottom)? ClipToPixels(cef_window_t* w, Rectangle region)
    {
        var bounds = w->@base.@base.get_bounds_in_screen(&w->@base.@base);
        var display = CefNative.cef_display_get_matching_bounds(&bounds, 0);
        var origin = new cef_point_t { x = region.X, y = region.Y };
        display->convert_point_to_pixels(display, &origin);
        var scale = display->get_device_scale_factor(display);
        NativeObject.Release(display);
        return AppDetails.ClipToPixels(region, scale, (origin.x, origin.y));
    }

    // Electron takes the 1x representation; icon DIBs take straight alpha.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static nint StraightIcon(NativeImage image)
    {
        if (image.IsEmpty()) return 0;
        var bitmap = ImagePixels.Unpremultiply(image.BitmapAt(1.0));
        return WindowsTaskbar.CreateIconHandle(bitmap.Bgra, bitmap.Width, bitmap.Height);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool SetOverlayIconOn(nint hwnd, NativeImage overlay, string description)
    {
        if (overlay.IsEmpty()) return false;
        var bitmap = ImagePixels.Unpremultiply(overlay.BitmapAt(1.0));
        return WindowsTaskbar.SetOverlayIcon(hwnd, bitmap.Bgra, bitmap.Width, bitmap.Height, description);
    }

    // The shell creates the button on the first show, whether at creation or from Show(); later shows keep it removed.
    private void ApplySkipTaskbar(cef_window_t* w, bool restore = false)
    {
        if (OperatingSystem.IsWindows() && (_skipTaskbar || restore) && !WindowsTaskbar.SetSkipTaskbar(Hwnd(w), _skipTaskbar))
            Console.Error.WriteLine("[Oriel] The taskbar did not take SkipTaskbar.");
        if (OperatingSystem.IsLinux() && (_skipTaskbar || restore) && !X11Window.SetSkipTaskbar(Hwnd(w), _skipTaskbar))
            Console.Error.WriteLine("[Oriel] No X display for SkipTaskbar.");
    }
}
