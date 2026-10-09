using System.Runtime.Versioning;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Electron's macOS <c>PowerMonitor</c>: Chromium's <c>PowerMonitorDeviceSource</c> (IOKit sleep and wake, the
/// <c>IOPMPowerSource</c> battery, <c>ThermalStateObserverMac</c>) feeding <c>base::PowerMonitor</c>'s change filters, plus
/// Electron's <c>MacLockMonitor</c> and shutdown handler. Started by the first handler of any event, as Electron creates
/// its native monitor on the first listener.</summary>
[SupportedOSPlatform("macos")]
internal static class MacPowerMonitor
{
    private static readonly Lock s_gate = new();
    private static readonly SuspendFilter s_suspend = new();
    private static bool s_started;
    private static BatteryPowerStatus s_battery;
    private static ThermalState s_thermal;
    private static int s_speedLimit;

    /// <summary>Chromium's <c>PowerMonitor</c> state is seeded at browser start, before any listener, so the current
    /// battery, thermal state and speed limit become the baseline here rather than events. UI thread.</summary>
    internal static void Start()
    {
        if (s_started) return;
        s_started = true;
        lock (s_gate)
        {
            s_battery = PowerRules.MacBatteryStatus(MacPower.ReadBattery());
            s_thermal = PowerRules.ThermalStateOf(MacPower.ThermalState());
            s_speedLimit = MacPower.CpuSpeedLimit() ?? SpeedLimitFilter.Max;
        }

        // Chromium's power events reach observers through posted tasks, so the handlers run after IOKit is answered.
        MacPower.RegisterForSystemPower(message =>
        {
            var e = PowerRules.MacSystemPowerEventOf(message);
            if (e == PowerEvent.None) return;
            bool accepted;
            lock (s_gate) accepted = s_suspend.Accept(e);
            if (accepted) Post(() => PowerMonitor.Raise(e));
        });
        MacPower.WatchPowerSource(OnPowerSourceChanged);
        MacPower.ObserveThermalState(OnThermalStateChanged);
        MacPower.ObserveCpuPower(OnCpuPowerChanged);
        MacPower.ObserveSession(n => PowerMonitor.Raise(PowerRules.MacSessionEventOf(n)));
        MacApplication.ShutdownHandler = PowerMonitor.ShouldShutdown;
    }

    /// <summary>Chromium's cached <c>battery_power_status_</c> once started; a fresh read before.</summary>
    internal static BatteryPowerStatus BatteryStatus()
    {
        lock (s_gate) if (s_started) return s_battery;
        return PowerRules.MacBatteryStatus(MacPower.ReadBattery());
    }

    private static void OnPowerSourceChanged()
    {
        var now = PowerRules.MacBatteryStatus(MacPower.ReadBattery());
        PowerEvent e;
        lock (s_gate)
        {
            e = PowerRules.BatteryEventOf(s_battery, now);
            s_battery = now;
        }
        if (e != PowerEvent.None) Post(() => PowerMonitor.Raise(e));
    }

    // NSProcessInfo posts on the thread whose change it reports; the state is read there, as Chromium does.
    private static void OnThermalStateChanged()
    {
        var now = PowerRules.ThermalStateOf(MacPower.ThermalState());
        lock (s_gate)
        {
            if (now == s_thermal) return;
            s_thermal = now;
        }
        Post(() => PowerMonitor.RaiseThermal(now));
    }

    // IOPMCopyCPUPowerStatus is a blocking MIG call; Chromium runs it on the thread pool and replies to the main thread.
    private static void OnCpuPowerChanged() => Task.Run(() =>
    {
        var limit = MacPower.CpuSpeedLimit() ?? SpeedLimitFilter.Max;
        lock (s_gate)
        {
            if (limit == s_speedLimit) return;
            s_speedLimit = limit;
        }
        Post(() => PowerMonitor.RaiseSpeedLimit(limit));
    });

    private static void Post(Action action)
    {
        try { CefThread.PostUi(action); }
        // CEF is shutting down.
        catch (InvalidOperationException) { }
    }
}
