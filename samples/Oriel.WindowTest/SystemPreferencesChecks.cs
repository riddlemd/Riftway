using System.Runtime.InteropServices;
using Microsoft.Win32;
using Oriel;
using Oriel.Cef;
using Oriel.Cef.Platform;

/// <summary>systemPreferences on Windows, each value read back through Win32 or the registry by the test itself.</summary>
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
        Task.Run(() => { if (OperatingSystem.IsWindows()) Run(check, step); });

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
