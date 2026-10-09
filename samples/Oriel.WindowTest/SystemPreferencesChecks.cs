using System.Runtime.InteropServices;
using Microsoft.Win32;
using Oriel;
using Oriel.Cef;
using Oriel.Cef.Platform;

/// <summary>systemPreferences on Windows, each value read back through Win32 or the registry by the test itself; and on
/// macOS, read back through <c>defaults</c>, <c>ioreg</c> and Oriel's other modules. The Linux checks are in LinuxThemeAsync,
/// which owns the fake portal.</summary>
static unsafe class SystemPreferencesChecks
{
    private const uint WmSysColorChange = 0x0015;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDwmColorizationColorChanged = 0x0320;

    // Electron's documented Windows names with WinUser.h's COLOR_ values, kept apart from Oriel's table.
    private static readonly (string Name, int Index)[] s_colors =
    [
        ("3d-dark-shadow", 21), ("3d-face", 15), ("3d-highlight", 20), ("3d-light", 22), ("3d-shadow", 16), ("active-border", 10),
        ("active-caption", 2), ("active-caption-gradient", 27), ("app-workspace", 12), ("button-text", 18), ("caption-text", 9),
        ("desktop", 1), ("disabled-text", 17), ("highlight", 13), ("highlight-text", 14), ("hotlight", 26), ("inactive-border", 11),
        ("inactive-caption", 3), ("inactive-caption-gradient", 28), ("inactive-caption-text", 19), ("info-background", 24),
        ("info-text", 23), ("menu", 4), ("menu-highlight", 29), ("menu-text", 7), ("menubar", 30), ("scrollbar", 0), ("window", 5),
        ("window-frame", 6), ("window-text", 8),
    ];

    internal static Task RunAsync(Action<string, bool, object?> check, TimeSpan step) =>
        Task.Run(() =>
        {
            if (OperatingSystem.IsWindows()) Run(check, step);
            else if (OperatingSystem.IsMacOS()) RunMac(check, step);
        });

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void RunMac(Action<string, bool, object?> check, TimeSpan step)
    {
        void Try(string name, Func<(bool Pass, object? Detail)> test)
        {
            try
            {
                var (pass, detail) = test();
                check(name, pass, detail);
            }
            catch (Exception e) { check(name, false, $"threw {e.GetType().Name}: {e.Message}"); }
        }
        static bool IsHex(string s, bool hash) => s.Length == (hash ? 9 : 8) && (!hash || s[0] == '#')
            && s[(hash ? 1 : 0)..].All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

        string[] macNames =
        [
            "control-background", "control", "control-text", "disabled-control-text", "find-highlight", "grid", "header-text",
            "highlight", "keyboard-focus-indicator", "label", "link", "placeholder-text", "quaternary-label",
            "scrubber-textured-background", "secondary-label", "selected-content-background", "selected-control",
            "selected-control-text", "selected-menu-item-text", "selected-text-background", "selected-text", "separator", "shadow",
            "tertiary-label", "text-background", "text", "under-page-background", "unemphasized-selected-content-background",
            "unemphasized-selected-text-background", "unemphasized-selected-text", "window-background", "window-frame-text",
        ];
        Try("systemPreferences (macOS): GetColor gives #RRGGBBAA for all 32 Electron names, GetSystemColor for all 9", () =>
        {
            var bad = macNames.Select(n => (n, c: SystemPreferences.GetColor(n)))
                .Concat(new[] { "blue", "brown", "gray", "green", "orange", "pink", "purple", "red", "yellow" }.Select(n => (n, c: SystemPreferences.GetSystemColor(n))))
                .Where(x => !IsHex(x.c, hash: true)).Select(x => $"{x.n}={x.c}").ToList();
            return (bad.Count == 0, bad.Count == 0 ? $"label={SystemPreferences.GetColor("label")} blue={SystemPreferences.GetSystemColor("blue")}" : string.Join("; ", bad));
        });
        Try("systemPreferences (macOS): GetAccentColor is opaque RRGGBBAA without #", () =>
        {
            var accent = SystemPreferences.GetAccentColor();
            return (IsHex(accent, hash: false) && accent.EndsWith("FF", StringComparison.Ordinal), accent);
        });

        // Dynamic colours and the appearance resolve on the main thread against the app's appearance, which ThemeSource sets.
        var source = NativeTheme.ThemeSource;
        try
        {
            NativeTheme.ThemeSource = ThemeSource.Dark;
            var darkAppearance = SystemPreferences.GetEffectiveAppearance();
            var darkLabel = SystemPreferences.GetColor("label");
            NativeTheme.ThemeSource = ThemeSource.Light;
            var lightAppearance = SystemPreferences.GetEffectiveAppearance();
            var lightLabel = SystemPreferences.GetColor("label");
            check("systemPreferences (macOS): GetEffectiveAppearance follows the app's appearance (Dark, then Light)",
                darkAppearance == SystemAppearance.Dark && lightAppearance == SystemAppearance.Light && SystemPreferences.EffectiveAppearance == SystemAppearance.Light,
                $"{darkAppearance} / {lightAppearance}");
            check("systemPreferences (macOS): GetColor(label) resolves against the app's appearance (light text on dark)",
                Convert.ToInt32(darkLabel[1..3], 16) > 0xC0 && Convert.ToInt32(lightLabel[1..3], 16) < 0x40, $"dark={darkLabel} light={lightLabel}");
            NativeTheme.ThemeSource = ThemeSource.System;
            var systemDark = Defaults("read -g AppleInterfaceStyle") == "Dark";
            check("systemPreferences (macOS): with ThemeSource System the appearance is the system's (defaults AppleInterfaceStyle)",
                SystemPreferences.GetEffectiveAppearance() == (systemDark ? SystemAppearance.Dark : SystemAppearance.Light), $"system dark={systemDark}");
        }
        finally { NativeTheme.ThemeSource = source; }

#pragma warning disable CS0618 // The deprecated property is part of what is checked.
        Try("systemPreferences (macOS): accessibilityDisplayShouldReduceTransparency matches NativeTheme.PrefersReducedTransparency",
            () => (SystemPreferences.AccessibilityDisplayShouldReduceTransparency == NativeTheme.PrefersReducedTransparency, NativeTheme.PrefersReducedTransparency));
#pragma warning restore CS0618
        Try("systemPreferences (macOS): IsSwipeTrackingFromScrollEventsEnabled matches AppleEnableSwipeNavigateWithScrolls (on when unset)", () =>
        {
            var raw = Defaults("read -g AppleEnableSwipeNavigateWithScrolls");
            var want = raw is null || raw == "1";
            return (SystemPreferences.IsSwipeTrackingFromScrollEventsEnabled() == want, $"defaults={raw ?? "unset"}");
        });
        Try("systemPreferences (macOS): GetAnimationSettings follows Reduce motion and NSScrollAnimationEnabled", () =>
        {
            var s = SystemPreferences.GetAnimationSettings();
            var reduce = Defaults("read com.apple.universalaccess reduceMotion") == "1";
            var scroll = Defaults("read -g NSScrollAnimationEnabled");
            return (s.PrefersReducedMotion == reduce && s.ShouldRenderRichAnimation == !reduce && s.ScrollAnimationsEnabledBySystem == (scroll == "1"),
                $"{s} vs reduceMotion={reduce} NSScrollAnimationEnabled={scroll ?? "unset"}");
        });
        Try("systemPreferences (macOS): IsTrustedAccessibilityClient(false) is AXIsProcessTrusted", () =>
            (SystemPreferences.IsTrustedAccessibilityClient(false) == Oriel.Cef.Platform.MacHotKeys.IsTrustedAccessibilityClient(), SystemPreferences.IsTrustedAccessibilityClient(false)));
        Try("systemPreferences (macOS): CanPromptTouchID is false without a Touch ID sensor", () =>
        {
            var sensor = (Run("/usr/sbin/ioreg", "-c AppleBiometricSensor -r") ?? "").Contains("AppleBiometricSensor", StringComparison.Ordinal);
            var can = SystemPreferences.CanPromptTouchID();
            return (sensor || !can, $"sensor={sensor} canPrompt={can} (lid closed or no enrolled finger also gives false)");
        });
        foreach (var type in new[] { MediaAccessType.Camera, MediaAccessType.Microphone })
        {
            Try($"systemPreferences (macOS): AskForMediaAccessAsync({type}) answers an already-decided consent without a prompt", () =>
            {
                var status = SystemPreferences.GetMediaAccessStatus(type);
                if (status is not (MediaAccessStatus.Granted or MediaAccessStatus.Denied)) return (true, $"{status}: skipped, asking would prompt");
                var ask = SystemPreferences.AskForMediaAccessAsync(type);
                return (ask.Wait(step) && ask.Result == (status == MediaAccessStatus.Granted), $"{status} -> {(ask.IsCompleted ? ask.Result : "pending")}");
            });
        }

        // Distributed notifications come back through the system's daemon to the main run loop.
        var name = $"dev.oriel.windowtest.sysprefs.{Guid.NewGuid():N}";
        var got = new List<(SubscribedNotification N, bool OnUi)>();
        var id = SystemPreferences.SubscribeNotification(name, n => { lock (got) got.Add((n, CefThread.IsOnUiThread)); });
        int Got() { lock (got) return got.Count; }
        Try("systemPreferences (macOS): PostNotification reaches SubscribeNotification on the UI thread with its user info", () =>
        {
            SystemPreferences.PostNotification(name, new Dictionary<string, object?> { ["k"] = "v", ["n"] = 2 }, deliverImmediately: true);
            var arrived = Eventually(() => Got() == 1, step);
            (SubscribedNotification N, bool OnUi) first;
            lock (got) first = got.FirstOrDefault();
            return (arrived && first.OnUi && first.N.Object == "" && first.N.UserInfo.ToJsonString() == "{\"k\":\"v\",\"n\":2}",
                arrived ? $"{first.N} onUi={first.OnUi}" : "nothing arrived");
        });
        Try("systemPreferences (macOS): UnsubscribeNotification stops delivery", () =>
        {
            SystemPreferences.UnsubscribeNotification(id);
            SystemPreferences.PostNotification(name, new Dictionary<string, object?>(), deliverImmediately: true);
            Thread.Sleep(1000);
            return (Got() == 1, Got());
        });
        Try("systemPreferences (macOS): a local post from a worker thread is delivered on the UI thread", () =>
        {
            var onUi = (bool?)null;
            var local = SystemPreferences.SubscribeLocalNotification(name, _ => onUi = CefThread.IsOnUiThread);
            SystemPreferences.PostLocalNotification(name, new Dictionary<string, object?>());
            SystemPreferences.UnsubscribeLocalNotification(local);
            return (onUi == true, onUi);
        });
    }

    private static string? Defaults(string arguments) => Run("/usr/bin/defaults", arguments);

    // Trimmed stdout, or null when the command fails (a missing defaults key exits 1).
    private static string? Run(string file, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Run(Action<string, bool, object?> check, TimeSpan step)
    {

        // Every check catches: a throw would abort the driver and hide the rest of the run.
        void Try(string name, Func<(bool Pass, object? Detail)> test)
        {
            try
            {
                var (pass, detail) = test();
                check(name, pass, detail);
            }
            catch (Exception e) { check(name, false, $"threw {e.GetType().Name}: {e.Message}"); }
        }

        Try("systemPreferences.GetColor matches GetSysColor for every Windows name (#RRGGBBAA)", () =>
        {
            var wrong = s_colors.Select(c => (c.Name, Got: SystemPreferences.GetColor(c.Name), Want: SysColorHex(c.Index)))
                .Where(c => c.Got != c.Want).Select(c => $"{c.Name}: {c.Got} != {c.Want}").ToList();
            return (wrong.Count == 0, wrong.Count == 0 ? $"{s_colors.Length} names, window={SystemPreferences.GetColor("window")}" : string.Join("; ", wrong));
        });
        Try("systemPreferences.GetColor rejects an unknown name", () =>
        {
            try
            {
                return (false, SystemPreferences.GetColor("not-a-color"));
            }
            catch (ArgumentException e) { return (e.Message.StartsWith("Unknown color: not-a-color", StringComparison.Ordinal), e.Message); }
        });
        Try("systemPreferences.GetAccentColor matches the DWM AccentColor (RRGGBBAA)", () =>
        {
            var got = SystemPreferences.GetAccentColor();
            var want = Registry.GetValue(@"HKEY_CURRENT_USER\" + WindowsSystemPreferences.DwmKey, "AccentColor", null) is int raw
                ? $"{raw & 0xFF:X2}{(raw >> 8) & 0xFF:X2}{(raw >> 16) & 0xFF:X2}FF" : "";
            return (got == want, $"{got} vs registry {want}");
        });
        Try("systemPreferences.GetAnimationSettings follows SPI_GETCLIENTAREAANIMATION", () =>
        {
            int enabled = 1;
            var ok = SystemParametersInfoW(0x1042, 0, &enabled, 0) != 0;
            var on = !ok ? GetSystemMetrics(0x1000) == 0 : enabled != 0;
            var s = SystemPreferences.GetAnimationSettings();
            return (s.ShouldRenderRichAnimation == on && s.ScrollAnimationsEnabledBySystem == on && s.PrefersReducedMotion == !(ok ? enabled != 0 : true),
                $"{s} vs SPI ok={ok} value={enabled}");
        });
        foreach (var (type, store, policy) in new[] { (MediaAccessType.Camera, "webcam", "LetAppsAccessCamera"), (MediaAccessType.Microphone, "microphone", "LetAppsAccessMicrophone") })
        {
            Try($"systemPreferences.GetMediaAccessStatus({type}) matches the ConsentStore", () =>
            {
                const string consent = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
                var machine = Registry.LocalMachine.OpenSubKey(consent + store)?.GetValue("Value") as string;
                var user = Registry.CurrentUser.OpenSubKey(consent + store)?.GetValue("Value") as string;
                var desktop = Registry.CurrentUser.OpenSubKey(consent + store + @"\NonPackaged")?.GetValue("Value") as string;
                var forced = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy")?.GetValue(policy) as int?;
                var want = machine == "Deny" || forced == 2 ? MediaAccessStatus.Restricted
                    : user == "Deny" || desktop == "Deny" ? MediaAccessStatus.Denied : MediaAccessStatus.Granted;
                var got = SystemPreferences.GetMediaAccessStatus(type);
                return (got == want, $"{got} vs machine={machine} user={user} desktop={desktop} policy={forced}");
            });
        }
        Try("systemPreferences.GetMediaAccessStatus(Screen) is Granted", () =>
            (SystemPreferences.GetMediaAccessStatus(MediaAccessType.Screen) == MediaAccessStatus.Granted, SystemPreferences.GetMediaAccessStatus(MediaAccessType.Screen)));

        // Events: messages go to Oriel's own hidden window only, never broadcast.
        var colorEvents = 0;
        var colorOffUi = 0;
        var accents = new List<string>();
        var accentOffUi = 0;
        EventHandler onColor = (_, _) => { Interlocked.Increment(ref colorEvents); if (!CefThread.IsOnUiThread) Interlocked.Increment(ref colorOffUi); };
        EventHandler<AccentColorChangedEventArgs> onAccent = (_, e) => { lock (accents) accents.Add(e.NewColor); if (!CefThread.IsOnUiThread) Interlocked.Increment(ref accentOffUi); };
        nint hwnd = 0;
        Try("systemPreferences listening creates a hidden top-level window", () =>
        {
            SystemPreferences.ColorChanged += onColor;
            SystemPreferences.AccentColorChanged += onAccent;
            var deadline = DateTime.UtcNow + step;
            while ((hwnd = FindOwnWindow()) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
            return (hwnd != 0 && IsWindowVisible(hwnd) == 0 && GetAncestor(hwnd, 1 /* GA_PARENT */) == GetDesktopWindow(),
                $"hwnd={hwnd} visible={(hwnd != 0 ? IsWindowVisible(hwnd) : -1)} parent={(hwnd != 0 ? GetAncestor(hwnd, 1) : -1)} desktop={GetDesktopWindow()}");
        });

        int Colors() => Volatile.Read(ref colorEvents);
        int Accents() { lock (accents) return accents.Count; }
        Try("WM_SYSCOLORCHANGE raises ColorChanged on the UI thread", () =>
        {
            var before = Colors();
            if (hwnd != 0) SendMessageW(hwnd, WmSysColorChange, 0, 0);
            return (Colors() == before + 1 && colorOffUi == 0, $"{before} -> {Colors()} offUi={colorOffUi}");
        });
        Try("WM_SETTINGCHANGE with SPI_SETHIGHCONTRAST raises ColorChanged", () =>
        {
            var before = Colors();
            if (hwnd != 0) SendMessageW(hwnd, WmSettingChange, 0x0043, 0);
            return (Colors() == before + 1, $"{before} -> {Colors()}");
        });
        var accentsBefore = Accents();
        Try("ImmersiveColorSet, another SPI and WM_DWMCOLORIZATIONCOLORCHANGED raise no ColorChanged", () =>
        {
            var before = Colors();
            if (hwnd != 0)
            {
                fixed (char* area = "ImmersiveColorSet") SendMessageW(hwnd, WmSettingChange, 0, (nint)area);
                SendMessageW(hwnd, WmSettingChange, 0x1043 /* SPI_SETCLIENTAREAANIMATION */, 0);
                SendMessageW(hwnd, WmDwmColorizationColorChanged, unchecked((nint)0xC40078D4), 1);
            }
            return (Colors() == before, $"{before} -> {Colors()}");
        });
        Thread.Sleep(600);
        check("window messages raise no AccentColorChanged", Accents() == accentsBefore, $"{accentsBefore} -> {Accents()}");

        var original = SystemPreferences.GetAccentColor();
        uint? originalArgb = original.Length == 8 ? (Convert.ToUInt32(original[6..], 16) << 24) | Convert.ToUInt32(original[..6], 16) : null;
        var startCount = Accents();
        Try("a new accent colour raises AccentColorChanged with it, on the UI thread", () =>
        {
            SystemPreferences.SetAccentColorForTesting(0xFF123456);
            var deadline = DateTime.UtcNow + step;
            while (Accents() == startCount && DateTime.UtcNow < deadline) Thread.Sleep(50);
            string? last;
            lock (accents) last = accents.LastOrDefault();
            return (Accents() == startCount + 1 && last == "123456FF" && SystemPreferences.GetAccentColor() == "123456FF" && accentOffUi == 0,
                $"events={Accents() - startCount} last={last} get={SystemPreferences.GetAccentColor()} offUi={accentOffUi}");
        });
        Try("an unchanged accent colour raises nothing", () =>
        {
            var before = Accents();
            SystemPreferences.SetAccentColorForTesting(0xFF123456);
            Thread.Sleep(600);
            return (Accents() == before, $"{before} -> {Accents()}");
        });
        Try("restoring the accent colour raises it again", () =>
        {
            var before = Accents();
            SystemPreferences.SetAccentColorForTesting(originalArgb);
            var deadline = DateTime.UtcNow + step;
            while (Accents() == before && DateTime.UtcNow < deadline) Thread.Sleep(50);
            string? last;
            lock (accents) last = accents.LastOrDefault();
            return (last == original && SystemPreferences.GetAccentColor() == original, $"last={last} original={original}");
        });

        // The DWM key itself must not be written; the same watch on a test key shows each change is reported and re-armed.
        Try("the registry watch reports each change under its key", () =>
        {
            const string key = @"Software\Oriel.WindowTest\SystemPreferencesWatch";
            using var created = Registry.CurrentUser.CreateSubKey(key);
            var fired = 0;
            var watch = WindowsSystemPreferences.WatchKey(key, () => Interlocked.Increment(ref fired));
            try
            {
                if (watch is null) return (false, "WatchKey returned null");
                created.SetValue("A", 1);
                var deadline = DateTime.UtcNow + step;
                while (Volatile.Read(ref fired) < 1 && DateTime.UtcNow < deadline) Thread.Sleep(20);
                var first = Volatile.Read(ref fired);
                Thread.Sleep(200);
                created.SetValue("A", 2);
                deadline = DateTime.UtcNow + step;
                while (Volatile.Read(ref fired) < 2 && DateTime.UtcNow < deadline) Thread.Sleep(20);
                return (first == 1 && Volatile.Read(ref fired) == 2, $"after first={first} after second={Volatile.Read(ref fired)}");
            }
            finally
            {
                watch?.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Oriel.WindowTest", throwOnMissingSubKey: false);
            }
        });

        SystemPreferences.ColorChanged -= onColor;
        SystemPreferences.AccentColorChanged -= onAccent;
    }

    private static string SysColorHex(int index)
    {
        var c = GetSysColor(index);
        return $"#{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}FF";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static nint FindOwnWindow()
    {
        var pid = (uint)Environment.ProcessId;
        nint hwnd = 0;
        while ((hwnd = FindWindowExW(0, hwnd, WindowsSystemPreferences.WindowClass, null)) != 0)
        {
            uint owner;
            _ = GetWindowThreadProcessId(hwnd, &owner);
            if (owner == pid) return hwnd;
        }
        return 0;
    }

    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int SystemParametersInfoW(uint action, uint param, void* value, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* processId);
    [DllImport("user32.dll")] private static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
