using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Riftway;
using Riftway.Cef;
using Riftway.Cef.Platform;

/// <summary>powerMonitor and powerSaveBlocker on macOS, read back from outside Riftway where the OS allows: assertions
/// through <c>pmset -g assertions</c>, idle time after a posted CGEvent, the thermal state and power source through
/// <c>osascript</c> and <c>pmset -g batt</c>; session notifications posted as the system posts them.</summary>
[SupportedOSPlatform("macos")]
static class MacPowerTest
{
    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step, string appName)
    {
        await BlockersAsync(check, step, appName);
        await IdleAsync(check, step);
        await EventsAsync(check, step);
        ThermalAndBattery(check);
    }

    private static async Task BlockersAsync(MessageBoxTest.CheckFn check, TimeSpan step, string appName)
    {
        var pid = Environment.ProcessId;
        bool Listed(string type) => Run("/usr/bin/pmset", "-g assertions").Split('\n')
            .Any(l => l.Contains($"pid {pid}(", StringComparison.Ordinal) && l.Contains(type, StringComparison.Ordinal) && l.Contains($"named: \"{appName}\"", StringComparison.Ordinal));
        bool Own(string type) => MacPower.OwnAssertions().Any(a => a.Type == type && a.Name == appName);

        var display = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventDisplaySleep);
        MacPowerSaveBlockerBackend.Flush();
        check("start(prevent-display-sleep) takes Chromium's NoDisplaySleepAssertion, named with the app, as pmset lists it",
            Listed("NoDisplaySleepAssertion") && Own("NoDisplaySleepAssertion") && PowerSaveBlocker.IsStarted(display), Run("/usr/bin/pmset", "-g assertions"));

        var app = PowerSaveBlocker.Start(PowerSaveBlockerType.PreventAppSuspension);
        MacPowerSaveBlockerBackend.Flush();
        check("a second, weaker blocker leaves the one display assertion in place", Own("NoDisplaySleepAssertion") && !Own("NoIdleSleepAssertion"),
            string.Join(",", MacPower.OwnAssertions()));

        PowerSaveBlocker.Stop(display);
        MacPowerSaveBlockerBackend.Flush();
        check("stopping the display blocker swaps to NoIdleSleepAssertion", Listed("NoIdleSleepAssertion") && !Listed("NoDisplaySleepAssertion"),
            Run("/usr/bin/pmset", "-g assertions"));

        check("stop() returns true for a running blocker and false after", PowerSaveBlocker.Stop(app) && !PowerSaveBlocker.Stop(app) && !PowerSaveBlocker.IsStarted(app));
        MacPowerSaveBlockerBackend.Flush();
        check("with no blocker left, pmset lists no Riftway assertion", !Listed("NoIdleSleepAssertion") && !Listed("NoDisplaySleepAssertion") && MacPower.OwnAssertions().Count == 0,
            Run("/usr/bin/pmset", "-g assertions"));
        await Task.Delay(step / 10);
    }

    private static async Task IdleAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        await Task.Delay(TimeSpan.FromSeconds(2.2));
        var before = PowerMonitor.GetSystemIdleTime();
        // A mouse move to where the pointer already is counts as input without moving anything.
        var current = CGEventCreate(0);
        var at = CGEventGetLocation(current);
        CFRelease(current);
        var move = CGEventCreateMouseEvent(0, 5, at, 0);
        CGEventPost(0, move);
        CFRelease(move);
        await Task.Delay(200);
        var after = PowerMonitor.GetSystemIdleTime();
        check("getSystemIdleTime() counts up without input and resets after a posted CGEvent (needs Accessibility for the post)",
            before >= 2 && after == 0, $"{before} -> {after}");
        check("getSystemIdleState() is active right after input", PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Active);
        check("getSystemIdleState() rejects a threshold of 0", Throws<ArgumentOutOfRangeException>(() => PowerMonitor.GetSystemIdleState(0)));

        // The system's distributed notifications, delivered to Riftway's observer only: posting them would reach every app.
        CefThread.InvokeOnUi(() => { MacPower.DeliverDistributed("com.apple.screensaver.didstart"); return true; });
        check("a running screen saver makes the idle state locked", Eventually(() => PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Locked, step));
        CefThread.InvokeOnUi(() => { MacPower.DeliverDistributed("com.apple.screensaver.didstop"); return true; });
        check("and stopping it makes it active again", Eventually(() => PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Active, step));
    }

    private static async Task EventsAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        EventHandler locked = (_, _) => log.Enqueue("lock"), unlocked = (_, _) => log.Enqueue("unlock");
        EventHandler active = (_, _) => log.Enqueue("active"), resigned = (_, _) => log.Enqueue("resign");
        PowerMonitor.ScreenLocked += locked;
        PowerMonitor.ScreenUnlocked += unlocked;
        PowerMonitor.UserDidBecomeActive += active;
        PowerMonitor.UserDidResignActive += resigned;
        try
        {
            CefThread.InvokeOnUi(() => { MacPower.DeliverDistributed("com.apple.screenIsLocked"); return true; });
            check("com.apple.screenIsLocked raises lock-screen and makes the idle state locked",
                Eventually(() => log.Contains("lock") && PowerMonitor.GetSystemIdleState(60) == SystemIdleState.Locked, step), string.Join(",", log));
            CefThread.InvokeOnUi(() => { MacPower.DeliverDistributed("com.apple.screenIsUnlocked"); return true; });
            check("com.apple.screenIsUnlocked raises unlock-screen", Eventually(() => log.Contains("unlock") && PowerMonitor.GetSystemIdleState(60) != SystemIdleState.Locked, step), string.Join(",", log));

            CefThread.InvokeOnUi(() => { PostWorkspace("NSWorkspaceSessionDidResignActiveNotification"); PostWorkspace("NSWorkspaceSessionDidBecomeActiveNotification"); return true; });
            check("the session notifications raise user-did-resign-active and user-did-become-active",
                Eventually(() => string.Join(",", log).EndsWith("resign,active", StringComparison.Ordinal), step), string.Join(",", log));

            // A held shutdown: Electron's willPowerOff asks the handlers, and terminate: is then ignored.
            var asked = 0;
            EventHandler<CancelEventArgs> hold = (_, e) => { asked++; e.Cancel = true; };
            PowerMonitor.Shutdown += hold;
            CefThread.InvokeOnUi(() => { PostWorkspace("NSWorkspaceWillPowerOffNotification"); return true; });
            CefThread.InvokeOnUi(() => { Terminate(); return true; });
            await Task.Delay(step / 4);
            check("NSWorkspaceWillPowerOffNotification raises shutdown, and preventDefault makes the next terminate: do nothing",
                asked == 1 && RiftwayApp.CurrentOrNull?.IsQuitting == false, asked);
            PowerMonitor.Shutdown -= hold;
        }
        finally
        {
            PowerMonitor.ScreenLocked -= locked;
            PowerMonitor.ScreenUnlocked -= unlocked;
            PowerMonitor.UserDidBecomeActive -= active;
            PowerMonitor.UserDidResignActive -= resigned;
        }
    }

    private static void ThermalAndBattery(MessageBoxTest.CheckFn check)
    {
        var raw = Run("/usr/bin/osascript", "-l JavaScript -e \"$.NSProcessInfo.processInfo.thermalState\"").Trim();
        string[] names = ["Nominal", "Fair", "Serious", "Critical"];
        var expected = int.TryParse(raw, out var n) && n is >= 0 and <= 3 ? names[n] : "?";
        check("getCurrentThermalState() is NSProcessInfo's thermal state as another process reads it", PowerMonitor.GetCurrentThermalState().ToString() == expected,
            $"{PowerMonitor.GetCurrentThermalState()} vs {raw}");

        var batt = Run("/usr/bin/pmset", "-g batt");
        check("isOnBatteryPower() agrees with pmset's power source", PowerMonitor.IsOnBatteryPower() == batt.Contains("'Battery Power'", StringComparison.Ordinal), batt);
        check("onBatteryPower is isOnBatteryPower()", PowerMonitor.OnBatteryPower == PowerMonitor.IsOnBatteryPower());
    }

    private static string Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static bool Throws<T>(Action act) where T : Exception
    {
        try { act(); }
        catch (T) { return true; }
        return false;
    }

    private static nint Sel(string name) => sel_registerName(name);

    private unsafe static void PostWorkspace(string name)
    {
        var center = Send(Send(objc_getClass("NSWorkspace"), Sel("sharedWorkspace")), Sel("notificationCenter"));
        var nsName = SendPtr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), name);
        ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(center, Sel("postNotificationName:object:"), nsName, 0);
    }

    private unsafe static void Terminate()
        => ((delegate* unmanaged<nint, nint, nint, void>)s_msgSend)(Send(objc_getClass("NSApplication"), Sel("sharedApplication")), Sel("terminate:"), 0);

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    private unsafe static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private unsafe static nint SendPtr(nint receiver, nint selector, string utf8)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(utf8 + "\0");
        fixed (byte* p = bytes) return ((delegate* unmanaged<nint, nint, byte*, nint>)s_msgSend)(receiver, selector, p);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X, Y;
    }

    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CoreGraphics)] private static extern nint CGEventCreate(nint source);
    [DllImport(CoreGraphics)] private static extern CGPoint CGEventGetLocation(nint e);
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateMouseEvent(nint source, uint type, CGPoint position, uint button);
    [DllImport(CoreGraphics)] private static extern void CGEventPost(uint tap, nint e);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint cf);
}
