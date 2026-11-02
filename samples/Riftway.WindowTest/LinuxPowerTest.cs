using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Riftway;
using Riftway.Cef.Platform;
using Riftway.Samples;

/// <summary>globalShortcut, powerMonitor and powerSaveBlocker on Linux, read back from outside Riftway: real X input from
/// xdotool, key grabs and screen saver state from a second X client, and fake logind, GNOME session manager,
/// freedesktop power management and screen saver services that record what Riftway asks them.</summary>
[SupportedOSPlatform("linux")]
static class LinuxPowerTest
{
    private const ushort Shift = 1, Lock = 2, Control = 4, Mod1 = 8, Mod2 = 16, Mod4 = 64;
    private const uint F9 = 0xFFC6, F10 = 0xFFC7, F11 = 0xFFC8;

    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step, string appName)
    {
        // Before anything asks for the idle state: Chromium's lock watcher looks for a screen saver service only once.
        using var saver = FakeDBusService.Start(false, call => call.Member switch
        {
            "GetActive" => [s_locked],
            "Inhibit" => [s_cookie++],
            _ => [],
        }, "org.freedesktop.ScreenSaver");
        using var x = TestXcb.Connect();
        if (x is null)
        {
            check("an X display for the global shortcut and idle checks", false);
            return;
        }
        await ShortcutsAsync(check, step, x);
        await IdleAndLockAsync(check, step, x, saver);
        await LogindAsync(check, step);
        await BlockersAsync(check, step, x, saver, appName);
    }

    private static async Task ShortcutsAsync(MessageBoxTest.CheckFn check, TimeSpan step, TestXcb x)
    {
        var fired = 0;
        var onUi = true;
        const ushort Mods = Control | Mod1 | Shift;
        var f9 = x.Keycode(F9);
        check("Register takes a free shortcut", GlobalShortcut.Register("Ctrl+Alt+Shift+F9", () => { fired++; onUi &= Riftway.Cef.CefThread.IsOnUiThread; })
            && GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9"));
        check("Riftway holds the grab on the root window: another X client gets BadAccess for it and for its Num Lock and Caps Lock variants",
            !x.Grab(f9, Mods) && !x.Grab(f9, Mods | Mod2) && !x.Grab(f9, Mods | Lock), f9);
        check("the same combination written differently is the same shortcut", GlobalShortcut.IsRegistered("Shift+Control+Alt+F9") && !GlobalShortcut.Register("Alt+Shift+Ctrl+F9", () => { }));
        Xdotool("key", "ctrl+alt+shift+F9");
        check("pressing it (xdotool, through XTEST) runs the callback on the UI thread, whatever has focus", Eventually(() => fired == 1, step) && onUi, fired);
        Xdotool("key", "Num_Lock");
        Xdotool("key", "ctrl+alt+shift+F9");
        Xdotool("key", "Num_Lock");
        check("with Num Lock on it still fires (Chromium grabs every lock variant)", Eventually(() => fired == 2, step), fired);
        Xdotool("key", "Caps_Lock");
        Xdotool("key", "ctrl+alt+shift+F9");
        Xdotool("key", "Caps_Lock");
        check("with Caps Lock on it still fires", Eventually(() => fired == 3, step), fired);
        Xdotool("key", "ctrl+alt+F9");
        await Task.Delay(400);
        check("another modifier set does not fire it", fired == 3, fired);
        // The X server's auto-repeat sends a KeyPress per repeat; Chromium dispatches each one.
        fired = 0;
        Xdotool("keydown", "ctrl+alt+shift+F9");
        await Task.Delay(1500);
        Xdotool("keyup", "ctrl+alt+shift+F9");
        check("a held shortcut repeats the callback", Eventually(() => fired >= 3, step), fired);

        GlobalShortcut.Unregister("Ctrl+Alt+Shift+F9");
        check("Unregister releases every grab: the other client can now take them all", !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9") && x.GrabAll(f9, Mods));
        fired = 0;
        Xdotool("key", "ctrl+alt+shift+F9");
        await Task.Delay(400);
        check("a released shortcut no longer runs the callback", fired == 0, fired);
        check("Register returns false while another client holds the key", !GlobalShortcut.Register("Ctrl+Alt+Shift+F9", () => fired++) && !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9"));
        x.UngrabAll(f9, Mods);
        check("... and false when the other client holds only the Caps Lock variant", x.Grab(f9, Mods | Lock) && !GlobalShortcut.Register("Ctrl+Alt+Shift+F9", () => fired++));
        x.Ungrab(f9, Mods | Lock);
        check("a failed Register leaves no grab of its own behind", x.GrabAll(f9, Mods));
        x.UngrabAll(f9, Mods);

        check("RegisterAll grabs each and is true", GlobalShortcut.RegisterAll(["Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F11"], () => { })
            && GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F10") && GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F11")
            && !x.Grab(x.Keycode(F10), Mods) && !x.Grab(x.Keycode(F11), Mods));
        GlobalShortcut.UnregisterAll();
        check("UnregisterAll releases them all", !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F10") && x.GrabAll(x.Keycode(F10), Mods) && x.GrabAll(x.Keycode(F11), Mods));
        x.UngrabAll(x.Keycode(F10), Mods);
        x.UngrabAll(x.Keycode(F11), Mods);
        await RegisterAllRollbackAsync(check, x);
        await SuspendAsync(check, step, x);
        await AltGrAsync(check, x);

        fired = 0;
        check("Super is grabbed as Mod4, not dropped (DRIFT J3)", GlobalShortcut.Register("Super+F9", () => fired++) && !x.Grab(f9, Mod4) && x.Grab(f9, 0));
        x.Ungrab(f9, 0);
        Xdotool("key", "super+F9");
        check("Super+F9 fires it", Eventually(() => fired == 1, step), fired);
        GlobalShortcut.Unregister("Super+F9");
        check("an invalid accelerator throws", Throws<ArgumentException>(() => GlobalShortcut.Register("Ctrl+Nope", () => { })));
    }

    // Electron's RegisterAll: at the first refusal, those registered by this call are unregistered again, and only those.
    private static Task RegisterAllRollbackAsync(MessageBoxTest.CheckFn check, TestXcb x)
    {
        const ushort Mods = Control | Mod1 | Shift;
        byte f9 = x.Keycode(F9), f10 = x.Keycode(F10), f11 = x.Keycode(F11), f12 = x.Keycode(0xFFC9);
        check("(setup) another client holds Ctrl+Alt+Shift+F11", x.Grab(f11, Mods));
        var result = GlobalShortcut.RegisterAll(["Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F11", "Ctrl+Alt+Shift+F12"], () => { });
        check("RegisterAll is false when one key is taken, and lets go of the ones it took: F10 is free again, F12 was never tried",
            !result && !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F10") && !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F12") && x.GrabAll(f10, Mods) && x.GrabAll(f12, Mods));
        x.UngrabAll(f10, Mods);
        x.UngrabAll(f12, Mods);
        x.Ungrab(f11, Mods);
        GlobalShortcut.Register("Ctrl+Alt+Shift+F9", () => { });
        result = GlobalShortcut.RegisterAll(["Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F9"], () => { });
        check("a key this app already holds fails RegisterAll too; the rollback leaves that earlier registration alone",
            !result && !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F10") && GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9") && !x.Grab(f9, Mods) && x.GrabAll(f10, Mods));
        x.UngrabAll(f10, Mods);
        GlobalShortcut.UnregisterAll();
        return Task.CompletedTask;
    }

    // Chromium's SetShortcutHandlingSuspended on X11: every grab is released while suspended and taken again on resume.
    private static async Task SuspendAsync(MessageBoxTest.CheckFn check, TimeSpan step, TestXcb x)
    {
        const ushort Mods = Control | Mod1 | Shift;
        byte f9 = x.Keycode(F9);
        var fired = 0;
        GlobalShortcut.Register("Ctrl+Alt+Shift+F9", () => fired++);
        GlobalShortcut.SetSuspended(true);
        check("SetSuspended(true): suspended, still registered, and every grab is released (another client can take them)",
            GlobalShortcut.IsSuspended() && GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9") && x.GrabAll(f9, Mods));
        x.UngrabAll(f9, Mods);
        Xdotool("key", "ctrl+alt+shift+F9");
        await Task.Delay(400);
        check("a suspended shortcut does not fire", fired == 0, fired);
        check("Register is false while suspended", !GlobalShortcut.Register("Ctrl+Alt+Shift+F10", () => { }) && !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F10"));
        GlobalShortcut.SetSuspended(false);
        check("SetSuspended(false) grabs it again", !GlobalShortcut.IsSuspended() && !x.Grab(f9, Mods) && !x.Grab(f9, Mods | Mod2));
        Xdotool("key", "ctrl+alt+shift+F9");
        check("... and it fires again", Eventually(() => fired == 1, step), fired);
        GlobalShortcut.SetSuspended(true);
        x.Grab(f9, Mods);
        GlobalShortcut.SetSuspended(false);
        x.Ungrab(f9, Mods);
        Xdotool("key", "ctrl+alt+shift+F9");
        await Task.Delay(400);
        check("resuming while another client holds the key keeps it registered but ungrabbed, as Chromium ignores the failure",
            GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9") && fired == 1, fired);
        GlobalShortcut.SetSuspended(true);
        GlobalShortcut.Unregister("Ctrl+Alt+Shift+F9");
        GlobalShortcut.SetSuspended(false);
        check("Unregister while suspended removes it; resuming grabs nothing for it", !GlobalShortcut.IsRegistered("Ctrl+Alt+Shift+F9") && x.GrabAll(f9, Mods));
        x.UngrabAll(f9, Mods);
    }

    // AltGr is its own modifier, but Chromium's X11 listener grabs only Shift, Control and Alt (DRIFT J5).
    private static async Task AltGrAsync(MessageBoxTest.CheckFn check, TestXcb x)
    {
        byte f9 = x.Keycode(F9);
        var fired = new List<string>();
        check("AltGr+F9 registers as a shortcut of its own", GlobalShortcut.Register("AltGr+F9", () => fired.Add("altgr"))
            && GlobalShortcut.IsRegistered("AltGr+F9") && !GlobalShortcut.IsRegistered("F9") && !GlobalShortcut.IsRegistered("Alt+F9"));
        check("... grabbing the bare F9 (and its lock variants), not Alt+F9", !x.Grab(f9, 0) && !x.Grab(f9, Mod2) && x.Grab(f9, Mod1));
        x.Ungrab(f9, Mod1);
        Xdotool("key", "F9");
        await Task.Delay(400);
        check("pressing F9 runs no AltGr callback (Electron fails a CHECK there)", fired.Count == 0, string.Join(",", fired));
        check("F9 itself can still be registered: the same client may grab the same key again", GlobalShortcut.Register("F9", () => fired.Add("plain")));
        Xdotool("key", "F9");
        await Task.Delay(400);
        check("... and pressing F9 runs only its callback", fired.SequenceEqual(["plain"]), string.Join(",", fired));
        GlobalShortcut.UnregisterAll();
        check("UnregisterAll lets go of the shared grab", x.GrabAll(f9, 0));
        x.UngrabAll(f9, 0);
    }

    private static async Task IdleAndLockAsync(MessageBoxTest.CheckFn check, TimeSpan step, TestXcb x, FakeDBusService? saver)
    {
        Xdotool("mousemove_relative", "--", "5", "5");
        await Task.Delay(2500);
        var quiet = PowerMonitor.GetSystemIdleTime();
        var state = PowerMonitor.GetSystemIdleState(2);
        Xdotool("mousemove_relative", "--", "-5", "-5");
        var after = PowerMonitor.GetSystemIdleTime();
        check("GetSystemIdleTime counts seconds without input (X screen saver extension), and xdotool input resets it", quiet is 2 or 3 && after <= 1, $"{quiet} -> {after}");
        check("GetSystemIdleState: idle at the threshold, active under it", state == SystemIdleState.Idle && PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Active, state);
        check("GetSystemIdleState rejects a threshold of 0, as Electron throws a TypeError", Throws<ArgumentOutOfRangeException>(() => PowerMonitor.GetSystemIdleState(0)));

        // The X fallback for "locked": the MIT screen saver's state, or xscreensaver's _SCREENSAVER_STATUS.
        x.ForceScreenSaver(activate: true);
        var on = Eventually(X11System.ScreenSaverActive, step);
        x.ForceScreenSaver(activate: false);
        var off = Eventually(() => !X11System.ScreenSaverActive(), step);
        check("the X screen saver counts as active while it is on (Chromium's IsXScreensaverActive)", on && off, $"{on} {off}");
        x.SetRootAtomProperty("_SCREENSAVER_STATUS", "LOCK");
        var xsLocked = X11System.ScreenSaverActive();
        x.SetRootAtomProperty("_SCREENSAVER_STATUS", "BLANK");
        check("xscreensaver's _SCREENSAVER_STATUS LOCK counts too, BLANK does not", xsLocked && !X11System.ScreenSaverActive());
        x.DeleteRootProperty("_SCREENSAVER_STATUS");

        if (saver is null)
        {
            Console.WriteLine("SKIP  the D-Bus screen lock checks: no session bus, or org.freedesktop.ScreenSaver is taken");
            return;
        }
        check("the lock state comes from the session bus screen saver's GetActive, once it has answered",
            Eventually(() => saver.Calls.Any(c => c.Member == "GetActive") && PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Active, step));
        s_locked = true;
        saver.Emit("/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", true);
        check("ActiveChanged(true) makes the state locked", Eventually(() => PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Locked, step));
        saver.Emit("/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", false, "extra");
        await Task.Delay(400);
        check("an ActiveChanged with more than one argument is ignored", PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Locked);
        s_locked = false;
        saver.Emit("/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver", "ActiveChanged", false);
        check("ActiveChanged(false) unlocks it", Eventually(() => PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Active, step));
    }

    private static uint s_cookie = 100;
    private static volatile bool s_locked;

    private static async Task LogindAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        using (var probe = FakeDBusService.Start(true, null))
        {
            if (probe is null)
            {
                Console.WriteLine("SKIP  the logind checks: no system bus (eng/docker/with-system-bus.sh provides one)");
                return;
            }
        }
        var who = Path.GetFileName(Argv0());
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var locks = new List<(string What, FakeDBusLock Lock)>();
        FakeDBusLock? LastLock(string what) { lock (locks) return locks.LastOrDefault(l => l.What == what).Lock; }
        int Inhibits(FakeDBusService s, string what) => s.Calls.Count(c => c.Member == "Inhibit" && c.Args is [string w, ..] && w == what);
        FakeDBusService? logind = null;

        PowerMonitor.Suspended += (_, _) => events.Enqueue($"suspend held={LastLock("sleep") is { Released: false }} ui={Riftway.Cef.CefThread.IsOnUiThread}");
        PowerMonitor.Resumed += (_, _) => events.Enqueue($"resume sleep-inhibits={(logind is null ? -1 : Inhibits(logind, "sleep"))}");
        check("the events Electron never raises on Linux subscribe without throwing", !Throws<Exception>(() =>
        {
            PowerMonitor.OnAc += (_, _) => events.Enqueue("on-ac");
            PowerMonitor.OnBattery += (_, _) => events.Enqueue("on-battery");
            PowerMonitor.ScreenLocked += (_, _) => events.Enqueue("lock");
            PowerMonitor.ScreenUnlocked += (_, _) => events.Enqueue("unlock");
            PowerMonitor.SpeedLimitChanged += (_, e) => events.Enqueue($"speed {e.Limit}");
        }));
        check("IsOnBatteryPower is false: Chromium's Linux power monitor source always reports external power", !PowerMonitor.IsOnBatteryPower() && !PowerMonitor.OnBatteryPower);

        // logind appears after the first listener: Electron waits for the service, then takes the sleep lock.
        await Task.Delay(500);
        logind = FakeDBusService.Start(true, call =>
        {
            if (call.Member != "Inhibit" || call.Args is not [string what, ..]) return [];
            var l = new FakeDBusLock();
            lock (locks) locks.Add((what, l));
            return [l];
        }, "org.freedesktop.login1");
        if (logind is null)
        {
            check("a fake logind owns org.freedesktop.login1 on the private system bus", false);
            return;
        }
        bool Saw(FakeDBusCall c, params object[] args) => c.Interface == "org.freedesktop.login1.Manager" && c.Member == "Inhibit" && c.Args.SequenceEqual(args);
        check("once logind is on the bus a sleep delay lock is taken, named for the executable",
            Eventually(() => logind.Calls.Any(c => Saw(c, "sleep", who, "Application cleanup before suspend", "delay")), step), string.Join(" ", logind.Calls));
        const string LoginPath = "/org/freedesktop/login1", Manager = "org.freedesktop.login1.Manager";
        var first = LastLock("sleep");
        logind.Emit(LoginPath, Manager, "PrepareForSleep", true);
        check("PrepareForSleep(true) raises Suspended on the UI thread while the lock is held, then releases it",
            Eventually(() => events.Contains("suspend held=True ui=True") && first is { Released: true }, step), string.Join(",", events));
        logind.Emit(LoginPath, Manager, "PrepareForSleep", false);
        check("PrepareForSleep(false) takes a new sleep lock, then raises Resumed",
            Eventually(() => events.Contains("resume sleep-inhibits=2"), step) && LastLock("sleep") is { Released: false } l2 && l2 != first, string.Join(",", events));
        logind.Emit(LoginPath, Manager, "PrepareForSleep", "yes");
        logind.Emit(LoginPath, Manager, "PrepareForShutdown", true);
        await Task.Delay(500);
        check("a malformed PrepareForSleep, and PrepareForShutdown without a Shutdown handler, raise nothing; no shutdown lock was taken",
            events.Count(e => e.StartsWith("suspend", StringComparison.Ordinal)) == 1 && Inhibits(logind, "shutdown") == 0, string.Join(",", events));

        var shutdowns = 0;
        var cancel = false;
        EventHandler<CancelEventArgs> onShutdown = (_, e) => { shutdowns++; e.Cancel = cancel; };
        PowerMonitor.Shutdown += onShutdown;
        check("a Shutdown handler takes a shutdown delay lock", Eventually(() => logind.Calls.Any(c => Saw(c, "shutdown", who, "Ensure a clean shutdown", "delay")), step), string.Join(" ", logind.Calls));
        var shutdownLock = LastLock("shutdown");
        logind.Emit(LoginPath, Manager, "PrepareForShutdown", true);
        check("PrepareForShutdown(true) raises Shutdown; not cancelled, the lock is released", Eventually(() => shutdowns == 1 && shutdownLock is { Released: true }, step), shutdowns);
        logind.Emit(LoginPath, Manager, "PrepareForShutdown", false);
        await Task.Delay(400);
        check("PrepareForShutdown(false) raises nothing", shutdowns == 1, shutdowns);
        PowerMonitor.Shutdown -= onShutdown;
        cancel = true;
        PowerMonitor.Shutdown += onShutdown;
        check("a new handler takes a new shutdown lock", Eventually(() => Inhibits(logind, "shutdown") == 2, step));
        shutdownLock = LastLock("shutdown");
        logind.Emit(LoginPath, Manager, "PrepareForShutdown", true);
        await Task.Delay(800);
        check("a cancelled Shutdown keeps the lock, delaying shutdown (Electron's preventDefault)", shutdowns == 2 && shutdownLock is { Released: false }, shutdowns);
        PowerMonitor.Shutdown -= onShutdown;
        check("removing the last Shutdown handler releases the lock", Eventually(() => shutdownLock is { Released: true }, step));
        check("no Windows-only event was raised", !events.Any(e => e is "on-ac" or "on-battery" or "lock" or "unlock" || e.StartsWith("speed", StringComparison.Ordinal)), string.Join(",", events));
        logind.Dispose();
    }

    private static async Task BlockersAsync(MessageBoxTest.CheckFn check, TimeSpan step, TestXcb x, FakeDBusService? saver, string appName)
    {
        var gnomeRefuses = false;
        using var gnome = FakeDBusService.Start(false, call => call.Member == "Inhibit" ? gnomeRefuses ? null : [s_cookie++] : [], "org.gnome.SessionManager");
        using var pm = FakeDBusService.Start(false, call => call.Member == "Inhibit" ? [s_cookie++] : [], "org.freedesktop.PowerManagement");
        if (saver is null || gnome is null || pm is null)
        {
            Console.WriteLine("SKIP  the powerSaveBlocker D-Bus checks: no session bus, or a name is taken");
            return;
        }
        var app = Argv0();
        var reason = PowerSaveBlockers.Reason(PowerSaveBlockers.ProductName(), appName);
        string Log(FakeDBusService s) => string.Join(" ", s.Calls.Where(c => c.Member is "Inhibit" or "Uninhibit" or "UnInhibit"));
        bool Inhibited(FakeDBusService s, int count) => Eventually(() => s.Calls.Count(c => c.Member is "Inhibit" or "Uninhibit" or "UnInhibit") >= count, TimeSpan.FromSeconds(5));
        FakeDBusCall[] Tail(FakeDBusService s, int n) => [.. s.Calls.Where(c => c.Member is "Inhibit" or "Uninhibit" or "UnInhibit").TakeLast(n)];
        bool Is(FakeDBusCall c, string member, params object[] args) => c.Member == member && c.Args.SequenceEqual(args);

        // Without a blocker the X screen saver starts after its timeout; a blocker of either type suspends it.
        x.SetScreenSaver(1);
        Xdotool("mousemove_relative", "--", "5", "5");
        var baseline = Eventually(X11System.ScreenSaverActive, TimeSpan.FromSeconds(4));
        x.ForceScreenSaver(activate: false);
        Xdotool("mousemove_relative", "--", "-5", "-5");

        var appId = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventAppSuspension);
        check("GNOME first: Inhibit(argv[0], 0, reason, 4 suspend-session) on org.gnome.SessionManager",
            Inhibited(gnome, 1) && Is(Tail(gnome, 1)[0], "Inhibit", app, 0u, reason, 4u), Log(gnome));
        var cookie1 = s_cookie - 1;
        if (baseline)
        {
            await Task.Delay(2500);
            check("a running blocker (even app suspension) suspends the X screen saver", !X11System.ScreenSaverActive());
        }
        else Console.WriteLine("SKIP  X screen saver suspension: the saver never starts on this X server");
        var displayId = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventDisplaySleep);
        check("a display blocker swaps the lock: Uninhibit the old cookie, then Inhibit with 8 mark-session-idle",
            Inhibited(gnome, 3) && Is(Tail(gnome, 2)[0], "Uninhibit", cookie1) && Is(Tail(gnome, 2)[1], "Inhibit", app, 0u, reason, 8u) && displayId == appId + 1, Log(gnome));
        var cookie2 = s_cookie - 1;
        check("Stop of the display blocker swaps back to the app type", PowerSaveBlocker.Stop(displayId) && Inhibited(gnome, 5)
            && Is(Tail(gnome, 2)[0], "Uninhibit", cookie2) && Is(Tail(gnome, 2)[1], "Inhibit", app, 0u, reason, 4u), Log(gnome));
        var cookie3 = s_cookie - 1;
        check("Stop of the last blocker uninhibits; stopping it again is false", PowerSaveBlocker.Stop(appId) && !PowerSaveBlocker.Stop(appId) && !PowerSaveBlocker.IsStarted(appId)
            && Inhibited(gnome, 6) && Is(Tail(gnome, 1)[0], "Uninhibit", cookie3), Log(gnome));
        check("with GNOME there, the freedesktop services are never asked", pm.Calls.IsEmpty && !saver.Calls.Any(c => c.Member is "Inhibit" or "UnInhibit"));
        if (baseline)
            check("with no blocker left, the X screen saver starts again", Eventually(X11System.ScreenSaverActive, TimeSpan.FromSeconds(4)));
        x.SetScreenSaver(-1);
        x.ForceScreenSaver(activate: false);

        gnomeRefuses = true;
        var a = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventAppSuspension);
        check("GNOME refusing falls back to org.freedesktop.PowerManagement for app suspension: Inhibit(argv[0], reason)",
            Inhibited(pm, 1) && Is(Tail(pm, 1)[0], "Inhibit", app, reason) && Tail(gnome, 1)[0].Member == "Inhibit", Log(pm));
        var cookie4 = s_cookie - 1;
        PowerSaveBlocker.Stop(a);
        check("... and UnInhibit(cookie) there on Stop", Inhibited(pm, 2) && Is(Tail(pm, 1)[0], "UnInhibit", cookie4), Log(pm));
        var d = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventDisplaySleep);
        bool SaverInhibits(int n) => Eventually(() => saver.Calls.Count(c => c.Member is "Inhibit" or "UnInhibit") >= n, TimeSpan.FromSeconds(5));
        check("... and to org.freedesktop.ScreenSaver for display sleep", SaverInhibits(1) && Is(Tail(saver, 1)[0], "Inhibit", app, reason), Log(saver));
        var cookie5 = s_cookie - 1;
        PowerSaveBlocker.Stop(d);
        check("... UnInhibit(cookie) there on Stop", SaverInhibits(2) && Is(Tail(saver, 1)[0], "UnInhibit", cookie5), Log(saver));

        gnome.Release("org.gnome.SessionManager");
        var gnomeCalls = gnome.Calls.Count;
        d = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventDisplaySleep);
        check("with no GNOME session manager on the bus, the freedesktop API is asked directly", SaverInhibits(3) && Is(Tail(saver, 1)[0], "Inhibit", app, reason) && gnome.Calls.Count == gnomeCalls, Log(saver));
        PowerSaveBlocker.Stop(d);
        _ = SaverInhibits(4);
        pm.Release("org.freedesktop.PowerManagement");
        var pmCalls = pm.Calls.Count;
        a = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventAppSuspension);
        await Task.Delay(500);
        check("with no service at all, Start still hands out a running id and nothing is asked", PowerSaveBlocker.IsStarted(a) && pm.Calls.Count == pmCalls && PowerSaveBlocker.Stop(a));
    }

    private static string Argv0()
    {
        var cmdline = File.ReadAllBytes("/proc/self/cmdline");
        return System.Text.Encoding.UTF8.GetString(cmdline, 0, Array.IndexOf(cmdline, (byte)0));
    }

    private static void Xdotool(params string[] args)
    {
        var psi = new ProcessStartInfo("xdotool") { UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
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

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}

/// <summary>A second X client, on XCB: grabs keys against Riftway's grabs and drives the server's screen saver.</summary>
[SupportedOSPlatform("linux")]
sealed unsafe partial class TestXcb : IDisposable
{
    private const string Lib = "libxcb.so.1";
    private readonly nint _c;
    private readonly uint _root;

    private TestXcb(nint c, uint root) => (_c, _root) = (c, root);

    public static TestXcb? Connect()
    {
        try
        {
            var c = xcb_connect(null, null);
            if (c == 0 || xcb_connection_has_error(c) != 0) return null;
            // The first screen's root: xcb_setup_t's fixed part is 40 bytes, then the vendor (padded to 4), then 8 bytes per
            // pixmap format, then the screens, whose first field is the root.
            var setup = (byte*)xcb_get_setup(c);
            var vendor = (*(ushort*)(setup + 24) + 3) & ~3;
            var screen = setup + 40 + vendor + 8 * setup[29];
            return new TestXcb(c, *(uint*)screen);
        }
        catch (DllNotFoundException) { return null; }
    }

    public byte Keycode(uint keysym)
    {
        var setup = (byte*)xcb_get_setup(_c);
        byte min = setup[34], max = setup[35];
        var reply = (byte*)xcb_get_keyboard_mapping_reply(_c, xcb_get_keyboard_mapping(_c, min, (byte)(max - min + 1)), null);
        var per = reply[1];
        var syms = new ReadOnlySpan<uint>(reply + 32, (int)*(uint*)(reply + 4));
        byte found = 0;
        for (var i = 0; i < syms.Length && found == 0; i++) if (syms[i] == keysym) found = (byte)(min + i / per);
        free(reply);
        return found;
    }

    /// <summary>Grabs one exact modifier combination; false when the server refuses (another client holds it).</summary>
    public bool Grab(byte keycode, ushort modifiers)
    {
        var error = xcb_request_check(_c, xcb_grab_key_checked(_c, 0, _root, modifiers, keycode, 1, 1));
        if (error == null) return true;
        free(error);
        return false;
    }

    public void Ungrab(byte keycode, ushort modifiers)
    {
        _ = xcb_ungrab_key(_c, keycode, _root, modifiers);
        _ = xcb_flush(_c);
    }

    private static readonly ushort[] s_locks = [0, 16, 2, 128, 18, 144, 130, 146];

    /// <summary>Grabs every lock variant, as Chromium does; on any refusal undoes them and returns false.</summary>
    public bool GrabAll(byte keycode, ushort modifiers)
    {
        foreach (var mask in s_locks)
        {
            if (Grab(keycode, (ushort)(modifiers | mask))) continue;
            UngrabAll(keycode, modifiers);
            return false;
        }
        return true;
    }

    public void UngrabAll(byte keycode, ushort modifiers)
    {
        foreach (var mask in s_locks) Ungrab(keycode, (ushort)(modifiers | mask));
    }

    /// <summary>The server's screen saver timeout in seconds; -1 restores the default.</summary>
    public void SetScreenSaver(short timeout)
    {
        // Interval 0, prefer blanking and allow exposures at their defaults (2).
        _ = xcb_request_check(_c, xcb_set_screen_saver_checked(_c, timeout, 0, 2, 2));
    }

    public void ForceScreenSaver(bool activate)
    {
        _ = xcb_request_check(_c, xcb_force_screen_saver_checked(_c, activate ? (byte)1 : (byte)0));
    }

    public void SetRootAtomProperty(string property, string atom)
    {
        var value = Atom(atom);
        // PropModeReplace, XA_ATOM (4), format 32.
        _ = xcb_request_check(_c, xcb_change_property_checked(_c, 0, _root, Atom(property), 4, 32, 1, &value));
    }

    public void DeleteRootProperty(string property) => _ = xcb_request_check(_c, xcb_delete_property_checked(_c, _root, Atom(property)));

    private uint Atom(string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name);
        void* reply;
        fixed (byte* p = bytes) reply = xcb_intern_atom_reply(_c, xcb_intern_atom(_c, 0, (ushort)bytes.Length, p), null);
        var atom = *(uint*)((byte*)reply + 8);
        free(reply);
        return atom;
    }

    public void Dispose() => xcb_disconnect(_c);

    [LibraryImport("libc.so.6")] private static partial void free(void* p);
    [LibraryImport(Lib)] private static partial nint xcb_connect(byte* display, int* screen);
    [LibraryImport(Lib)] private static partial int xcb_connection_has_error(nint c);
    [LibraryImport(Lib)] private static partial void xcb_disconnect(nint c);
    [LibraryImport(Lib)] private static partial void* xcb_get_setup(nint c);
    [LibraryImport(Lib)] private static partial int xcb_flush(nint c);
    [LibraryImport(Lib)] private static partial void* xcb_request_check(nint c, uint cookie);
    [LibraryImport(Lib)] private static partial uint xcb_grab_key_checked(nint c, byte ownerEvents, uint window, ushort modifiers, byte key, byte pointerMode, byte keyboardMode);
    [LibraryImport(Lib)] private static partial uint xcb_ungrab_key(nint c, byte key, uint window, ushort modifiers);
    [LibraryImport(Lib)] private static partial uint xcb_get_keyboard_mapping(nint c, byte first, byte count);
    [LibraryImport(Lib)] private static partial void* xcb_get_keyboard_mapping_reply(nint c, uint cookie, void** error);
    [LibraryImport(Lib)] private static partial uint xcb_set_screen_saver_checked(nint c, short timeout, short interval, byte preferBlanking, byte allowExposures);
    [LibraryImport(Lib)] private static partial uint xcb_force_screen_saver_checked(nint c, byte mode);
    [LibraryImport(Lib)] private static partial uint xcb_change_property_checked(nint c, byte mode, uint window, uint property, uint type, byte format, uint length, void* data);
    [LibraryImport(Lib)] private static partial uint xcb_delete_property_checked(nint c, uint window, uint property);
    [LibraryImport(Lib)] private static partial uint xcb_intern_atom(nint c, byte onlyIfExists, ushort length, byte* name);
    [LibraryImport(Lib)] private static partial void* xcb_intern_atom_reply(nint c, uint cookie, void** error);
}
