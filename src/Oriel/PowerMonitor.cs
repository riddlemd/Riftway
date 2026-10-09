using System.ComponentModel;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Whether the user is at the machine. Electron's return value of <c>getSystemIdleState()</c>.</summary>
public enum SystemIdleState
{
    /// <summary>Electron's <c>active</c>.</summary>
    Active,

    /// <summary>No input for the threshold. Electron's <c>idle</c>.</summary>
    Idle,

    /// <summary>The screen is locked. Electron's <c>locked</c>.</summary>
    Locked,

    /// <summary>Electron's <c>unknown</c>.</summary>
    Unknown,
}

/// <summary>How hot the system runs. Electron's return value of <c>getCurrentThermalState()</c> and the
/// <c>state</c> of <c>'thermal-state-change'</c>.</summary>
public enum ThermalState
{
    /// <summary>Electron's <c>unknown</c>; always the answer off macOS.</summary>
    Unknown,

    /// <summary>Electron's <c>nominal</c>.</summary>
    Nominal,

    /// <summary>Electron's <c>fair</c>.</summary>
    Fair,

    /// <summary>Electron's <c>serious</c>.</summary>
    Serious,

    /// <summary>Electron's <c>critical</c>.</summary>
    Critical,
}

/// <summary>The thermal state changed. Electron's <c>'thermal-state-change'</c> data.</summary>
public sealed class ThermalStateChangedEventArgs(ThermalState state) : EventArgs
{
    /// <summary>Electron's <c>state</c>.</summary>
    public ThermalState State { get; } = state;
}

/// <summary>The CPU speed limit changed. Electron's <c>'speed-limit-change'</c> data.</summary>
public sealed class SpeedLimitChangedEventArgs(int limit) : EventArgs
{
    /// <summary>The percentage of full speed the system allows, 0 to 100. Electron's <c>limit</c>.</summary>
    public int Limit { get; } = limit;
}

/// <summary>Power and session changes: sleep, wake, power source, screen lock, idle time. Electron's <c>powerMonitor</c>.</summary>
/// <remarks>Use it once the app is ready; events are raised on the UI thread. On Linux, as in Electron, only suspend,
/// resume and shutdown are raised (from logind), and the machine never counts as on battery. On macOS, as Chromium's
/// <c>PowerMonitorDeviceSource</c> and Electron's <c>MacLockMonitor</c>: IOKit sleep and wake, the
/// <c>IOPMPowerSource</c> battery, <c>NSProcessInfo</c> thermal state, the CPU speed limit, the screen lock and the
/// session's activity, all started by the first handler of any event.</remarks>
public static class PowerMonitor
{
    private static EventHandler? s_suspended, s_resumed, s_onAc, s_onBattery, s_locked, s_unlocked, s_becameActive, s_resignedActive;
    private static EventHandler<ThermalStateChangedEventArgs>? s_thermal;
    private static EventHandler<CancelEventArgs>? s_shutdown;
    private static EventHandler<SpeedLimitChangedEventArgs>? s_speedLimit;
    private static bool? s_onBatteryLast;
    private static Timer? s_speedLimitTimer;
    private static readonly SpeedLimitFilter s_speedFilter = new();
    private static int s_speedLimitLast = SpeedLimitFilter.Max;

    /// <summary>The system is about to sleep. Electron's <c>'suspend'</c>.</summary>
    public static event EventHandler? Suspended { add => Add(ref s_suspended, value); remove => s_suspended -= value; }

    /// <summary>The system woke. Electron's <c>'resume'</c>.</summary>
    public static event EventHandler? Resumed { add => Add(ref s_resumed, value); remove => s_resumed -= value; }

    /// <summary>The machine switched to mains power. Never raised on Linux, where Chromium has no power source reader.
    /// Electron's <c>'on-ac'</c>.</summary>
    public static event EventHandler? OnAc { add => Add(ref s_onAc, value); remove => s_onAc -= value; }

    /// <summary>The machine switched to battery. Never raised on Linux, where Chromium has no power source reader.
    /// Electron's <c>'on-battery'</c>.</summary>
    public static event EventHandler? OnBattery { add => Add(ref s_onBattery, value); remove => s_onBattery -= value; }

    /// <summary>The system is shutting down; set <see cref="CancelEventArgs.Cancel"/> (Electron's
    /// <c>event.preventDefault()</c>) to hold the shutdown back: on Linux for as long as logind allows, on macOS by
    /// ignoring the <c>terminate:</c> that follows. Never raised on Windows, as in Electron. Electron's
    /// <c>'shutdown'</c>.</summary>
    /// <remarks>On Linux, while there is a handler, Oriel holds a logind shutdown delay lock, as Electron does. On macOS it
    /// is raised on <c>NSWorkspaceWillPowerOffNotification</c>, and, as in Electron, once one shutdown has been held back
    /// <c>terminate:</c> is ignored for the rest of the process.</remarks>
    public static event EventHandler<CancelEventArgs>? Shutdown
    {
        add
        {
            if (OperatingSystem.IsMacOS())
            {
                MacPowerMonitor.Start();
                s_shutdown += value;
                return;
            }
            if (OperatingSystem.IsWindows()) SystemHost.Start();
            s_shutdown += value;
            if (OperatingSystem.IsLinux()) LinuxPowerMonitor.Start().SetListeningForShutdown(s_shutdown is not null);
        }
        remove
        {
            s_shutdown -= value;
            if (OperatingSystem.IsLinux()) LinuxPowerMonitor.Start().SetListeningForShutdown(s_shutdown is not null);
        }
    }

    /// <summary>The screen was locked. Windows and macOS; never raised on Linux, as in Electron. Electron's
    /// <c>'lock-screen'</c>.</summary>
    public static event EventHandler? ScreenLocked { add => Add(ref s_locked, value); remove => s_locked -= value; }

    /// <summary>The screen was unlocked. Windows and macOS; never raised on Linux, as in Electron. Electron's
    /// <c>'unlock-screen'</c>.</summary>
    public static event EventHandler? ScreenUnlocked { add => Add(ref s_unlocked, value); remove => s_unlocked -= value; }

    /// <summary>The user's session became active (fast user switching back). macOS only. Electron's
    /// <c>'user-did-become-active'</c>.</summary>
    public static event EventHandler? UserDidBecomeActive { add => Add(ref s_becameActive, value); remove => s_becameActive -= value; }

    /// <summary>The user's session became inactive (fast user switching away). macOS only. Electron's
    /// <c>'user-did-resign-active'</c>.</summary>
    public static event EventHandler? UserDidResignActive { add => Add(ref s_resignedActive, value); remove => s_resignedActive -= value; }

    /// <summary>The thermal state changed. macOS only: Chromium has no thermal source on Windows or Linux. Electron's
    /// <c>'thermal-state-change'</c>.</summary>
    public static event EventHandler<ThermalStateChangedEventArgs>? ThermalStateChanged
    {
        add
        {
            if (OperatingSystem.IsMacOS()) MacPowerMonitor.Start();
            else if (OperatingSystem.IsLinux()) LinuxPowerMonitor.Start();
            else if (OperatingSystem.IsWindows()) SystemHost.Start();
            else throw NotYet();
            s_thermal += value;
        }
        remove => s_thermal -= value;
    }

    /// <summary>The CPU speed limit changed: on Windows sampled once a second, on macOS when IOKit announces a CPU power
    /// change. Never raised on Linux. Electron's <c>'speed-limit-change'</c>.</summary>
    public static event EventHandler<SpeedLimitChangedEventArgs>? SpeedLimitChanged
    {
        add
        {
            if (OperatingSystem.IsMacOS())
            {
                MacPowerMonitor.Start();
                s_speedLimit += value;
                return;
            }
            if (OperatingSystem.IsLinux())
            {
                LinuxPowerMonitor.Start();
                s_speedLimit += value;
                return;
            }
            if (!OperatingSystem.IsWindows()) throw NotYet();
            SystemHost.Start();
            s_speedLimit += value;
            s_speedLimitTimer ??= new Timer(static _ => { if (OperatingSystem.IsWindows()) SampleSpeedLimit(); }, null, 1000, 1000);
        }
        remove => s_speedLimit -= value;
    }

    /// <summary>Whether the machine runs on battery; always false on Linux, where Chromium's power monitor source is a stub
    /// that reports external power. Electron's <c>powerMonitor.isOnBatteryPower()</c>.</summary>
    public static bool IsOnBatteryPower()
        => OperatingSystem.IsWindows() ? WindowsSystem.OnBattery() ?? false
            : OperatingSystem.IsLinux() ? false
            : OperatingSystem.IsMacOS() ? MacPowerMonitor.BatteryStatus() == BatteryPowerStatus.Battery
            : throw NotYet();

    /// <summary>The system's thermal state; <see cref="ThermalState.Unknown"/> off macOS, as Chromium. Electron's
    /// <c>powerMonitor.getCurrentThermalState()</c>.</summary>
    public static ThermalState GetCurrentThermalState()
        => OperatingSystem.IsMacOS() ? PowerRules.ThermalStateOf(MacPower.ThermalState())
            : OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? ThermalState.Unknown
            : throw NotYet();

    /// <summary>Electron's <c>powerMonitor.onBatteryPower</c>.</summary>
    public static bool OnBatteryPower => IsOnBatteryPower();

    /// <summary>Seconds since the last keyboard or mouse input. Electron's <c>powerMonitor.getSystemIdleTime()</c>.</summary>
    /// <remarks>On Linux, the X screen saver extension's idle time; 0 when it cannot be read. On macOS,
    /// <c>CGEventSourceSecondsSinceLastEventType</c> for any input in the combined session.</remarks>
    public static int GetSystemIdleTime()
        => OperatingSystem.IsWindows() ? WindowsSystem.IdleSeconds() : OperatingSystem.IsLinux() ? X11System.IdleSeconds()
            : OperatingSystem.IsMacOS() ? MacPower.IdleSeconds() : throw NotYet();

    /// <summary>Locked, idle after <paramref name="idleThreshold"/> seconds without input, or active. Electron's
    /// <c>powerMonitor.getSystemIdleState()</c>.</summary>
    /// <remarks>On Linux "locked" comes from the desktop's screen saver service on the session bus, or, until one has
    /// answered, from the X screen saver, as Chromium's <c>CheckIdleStateIsLocked</c>. On macOS "locked" means the screen
    /// saver runs or the screen is locked, as Chromium's screen monitor has heard since the app became ready.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleThreshold"/> is not above 0, where Electron throws
    /// a <c>TypeError</c>.</exception>
    public static SystemIdleState GetSystemIdleState(int idleThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(idleThreshold);
        if (OperatingSystem.IsWindows()) return PowerRules.IdleState(WindowsSystem.IdleSeconds(), idleThreshold, WindowsSystem.IsLocked());
        if (OperatingSystem.IsMacOS()) return PowerRules.IdleState(MacPower.IdleSeconds(), idleThreshold, MacPower.IdleLocked);
        if (!OperatingSystem.IsLinux()) throw NotYet();
        var locked = PowerRules.LinuxLocked(LinuxScreenSaverWatcher.Instance.Locked, X11System.ScreenSaverActive);
        return PowerRules.IdleState(locked ? 0 : X11System.IdleSeconds(), idleThreshold, locked);
    }

    private static PlatformNotSupportedException NotYet() => new("powerMonitor is not implemented on this OS yet.");

    private static void Add(ref EventHandler? field, EventHandler? handler)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacPowerMonitor.Start();
            field += handler;
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            LinuxPowerMonitor.Start();
            field += handler;
            return;
        }
        if (!OperatingSystem.IsWindows()) throw NotYet();
        SystemHost.Start();
        s_onBatteryLast ??= WindowsSystem.OnBattery();
        field += handler;
    }

    /// <summary>A <c>PBT_</c> power event from the host window, on the UI thread.</summary>
    internal static void OnPowerBroadcast(uint pbt)
    {
        var onBattery = OperatingSystem.IsWindows() ? WindowsSystem.OnBattery() : null;
        var e = PowerRules.PowerEventOf(pbt, s_onBatteryLast, onBattery);
        if (pbt == 0x000A) s_onBatteryLast = onBattery;
        Raise(e);
    }

    /// <summary>A <c>WTS_</c> session event from the host window, on the UI thread.</summary>
    internal static void OnSessionChange(uint wts) => Raise(PowerRules.SessionEventOf(wts));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SampleSpeedLimit()
    {
        int limit;
        lock (s_speedFilter)
        {
            var states = WindowsSystem.ProcessorIdleStates();
            // A failed read counts as no throttling, as in Chromium.
            limit = s_speedFilter.Next(WindowsSystem.CpuIdleness(), states is null ? 0f : PowerRules.Throttling(states));
            if (limit == s_speedLimitLast) return;
            s_speedLimitLast = limit;
        }
        try { CefThread.PostUi(() => s_speedLimit?.Invoke(null, new SpeedLimitChangedEventArgs(limit))); }
        // CEF is shutting down.
        catch (InvalidOperationException) { }
    }

    /// <summary>logind's suspend or resume, raised on the UI thread before the bus thread goes on (the sleep lock waits).</summary>
    internal static void RaiseOnUi(PowerEvent e)
    {
        try { CefThread.InvokeOnUi(() => { Raise(e); return true; }); }
        // CEF is shutting down.
        catch (InvalidOperationException) { }
    }

    /// <summary>Electron's <c>ShouldShutdown</c>: raises <see cref="Shutdown"/> on the UI thread and returns whether no
    /// handler held it back.</summary>
    internal static bool ShouldShutdown()
    {
        try
        {
            return CefThread.InvokeOnUi(() =>
            {
                var args = new CancelEventArgs();
                s_shutdown?.Invoke(null, args);
                return !args.Cancel;
            });
        }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>The thermal state changed; UI thread.</summary>
    internal static void RaiseThermal(ThermalState state) => s_thermal?.Invoke(null, new ThermalStateChangedEventArgs(state));

    /// <summary>The speed limit changed; UI thread.</summary>
    internal static void RaiseSpeedLimit(int limit) => s_speedLimit?.Invoke(null, new SpeedLimitChangedEventArgs(limit));

    internal static void Raise(PowerEvent e)
    {
        var handler = e switch
        {
            PowerEvent.Suspend => s_suspended,
            PowerEvent.Resume => s_resumed,
            PowerEvent.OnAc => s_onAc,
            PowerEvent.OnBattery => s_onBattery,
            PowerEvent.Lock => s_locked,
            PowerEvent.Unlock => s_unlocked,
            PowerEvent.UserDidBecomeActive => s_becameActive,
            PowerEvent.UserDidResignActive => s_resignedActive,
            _ => null,
        };
        handler?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>A power or session change, OS-neutral.</summary>
internal enum PowerEvent
{
    None,
    Suspend,
    Resume,
    OnAc,
    OnBattery,
    Lock,
    Unlock,
    UserDidBecomeActive,
    UserDidResignActive,
}

/// <summary>Chromium's <c>PowerStateObserver::BatteryPowerStatus</c>.</summary>
internal enum BatteryPowerStatus
{
    Unknown,
    Battery,
    External,
}

internal static class PowerRules
{
    /// <summary>Chromium's Windows rule: <c>PBT_APMSUSPEND</c> is suspend, <c>PBT_APMRESUMEAUTOMATIC</c> (sent on every wake)
    /// is resume, and <c>PBT_APMPOWERSTATUSCHANGE</c> is on-ac or on-battery only when the power source really changed.</summary>
    internal static PowerEvent PowerEventOf(uint pbt, bool? wasOnBattery, bool? onBattery) => pbt switch
    {
        0x0004 => PowerEvent.Suspend,
        0x0012 => PowerEvent.Resume,
        0x000A when onBattery is { } now && now != wasOnBattery => now ? PowerEvent.OnBattery : PowerEvent.OnAc,
        _ => PowerEvent.None,
    };

    /// <summary><c>WTS_SESSION_LOCK</c> and <c>WTS_SESSION_UNLOCK</c>.</summary>
    internal static PowerEvent SessionEventOf(uint wts) => wts switch
    {
        0x7 => PowerEvent.Lock,
        0x8 => PowerEvent.Unlock,
        _ => PowerEvent.None,
    };

    /// <summary>Chromium's throttling level in 0..1: the mean over the CPUs of how far each is from its deepest idle state,
    /// <c>(MaxIdleState - CurrentIdleState) / MaxIdleState</c>.</summary>
    internal static float Throttling(ReadOnlySpan<(uint Max, uint Current)> cpus)
    {
        if (cpus.IsEmpty) return 0f;
        var total = 0f;
        // Unsigned like the ULONG fields; a CPU without idle states (Max 0) would divide by zero, so it counts as idle.
        foreach (var (max, current) in cpus) if (max != 0) total += (float)unchecked(max - current) / max;
        return total / cpus.Length;
    }

    /// <summary>Chromium's Linux <c>CheckIdleStateIsLocked</c>: the D-Bus screen saver's state once known, else whether the
    /// X screen saver is active (read only then).</summary>
    internal static bool LinuxLocked(bool? dbusLocked, Func<bool> x11ScreenSaverActive) => dbusLocked ?? x11ScreenSaverActive();

    /// <summary>Chromium's <c>NSProcessInfoThermalStateToDeviceThermalState</c>; a value AppKit does not define (Chromium's
    /// <c>NOTREACHED</c>) counts as unknown.</summary>
    internal static ThermalState ThermalStateOf(long nsProcessInfoThermalState) => nsProcessInfoThermalState switch
    {
        0 => ThermalState.Nominal,
        1 => ThermalState.Fair,
        2 => ThermalState.Serious,
        3 => ThermalState.Critical,
        _ => ThermalState.Unknown,
    };

    /// <summary>Chromium's <c>PowerMonitorDeviceSource::SystemPowerEventCallback</c>: will-sleep is suspend, will-power-on
    /// is resume; can-sleep and the rest raise nothing.</summary>
    internal static PowerEvent MacSystemPowerEventOf(uint message) => message switch
    {
        0xE0000280 => PowerEvent.Suspend,
        0xE0000320 => PowerEvent.Resume,
        _ => PowerEvent.None,
    };

    /// <summary>Chromium's <c>BatteryLevelProviderMac</c> and <c>OnBatteryStateReceived</c>: no <c>IOPMPowerSource</c>
    /// means no batteries, which counts as external power; a battery whose properties are missing is unknown.</summary>
    internal static BatteryPowerStatus MacBatteryStatus(MacBatteryReading r)
    {
        if (!r.HasService) return BatteryPowerStatus.External;
        if (!r.PropertiesRead || r.BatteryInstalled != true || r.ExternalConnected is not { } external || !r.HasCapacities || !r.HasVoltage)
            return BatteryPowerStatus.Unknown;
        return external ? BatteryPowerStatus.External : BatteryPowerStatus.Battery;
    }

    /// <summary>Chromium's <c>NotifyPowerStateChange</c> (only a change notifies) and Electron's
    /// <c>OnBatteryPowerStatusChange</c> (unknown is ignored).</summary>
    internal static PowerEvent BatteryEventOf(BatteryPowerStatus was, BatteryPowerStatus now)
        => now == was ? PowerEvent.None : now switch
        {
            BatteryPowerStatus.Battery => PowerEvent.OnBattery,
            BatteryPowerStatus.External => PowerEvent.OnAc,
            _ => PowerEvent.None,
        };

    /// <summary>Electron's <c>MacLockMonitor</c> events.</summary>
    internal static PowerEvent MacSessionEventOf(MacSessionNotification n) => n switch
    {
        MacSessionNotification.ScreenLocked => PowerEvent.Lock,
        MacSessionNotification.ScreenUnlocked => PowerEvent.Unlock,
        MacSessionNotification.SessionDidBecomeActive => PowerEvent.UserDidBecomeActive,
        MacSessionNotification.SessionDidResignActive => PowerEvent.UserDidResignActive,
        _ => PowerEvent.None,
    };

    /// <summary>Electron's order: locked first, then idle once the input has been quiet for the threshold.</summary>
    internal static SystemIdleState IdleState(int idleSeconds, int threshold, bool locked)
        => locked ? SystemIdleState.Locked : idleSeconds >= threshold ? SystemIdleState.Idle : SystemIdleState.Active;
}

/// <summary>Chromium's <c>PowerMonitor::NotifySuspend</c> and <c>NotifyResume</c>: a suspend while suspended, or a resume
/// while awake, notifies nothing.</summary>
internal sealed class SuspendFilter
{
    private bool _suspended;

    /// <summary>Whether <paramref name="e"/> is raised; anything but suspend and resume passes.</summary>
    internal bool Accept(PowerEvent e)
    {
        if (e is not (PowerEvent.Suspend or PowerEvent.Resume)) return true;
        var suspend = e == PowerEvent.Suspend;
        if (suspend == _suspended) return false;
        _suspended = suspend;
        return true;
    }
}

/// <summary>Chromium's <c>SpeedLimitObserverWin</c> smoothing: a 10-sample moving average that stays at 100 until it is full.</summary>
internal sealed class SpeedLimitFilter
{
    internal const int Max = 100;
    private const int WindowSize = 10;
    private readonly Queue<int> _samples = new();

    /// <summary>The speed limit for one second's sample; <paramref name="idleness"/> is null when it could not be read.</summary>
    internal int Next(int? idleness, float throttling)
    {
        if (idleness is not { } idle) return Max;
        // Busy-free or barely throttled readings are noise.
        if (idle > 90 || throttling < 0.1f)
        {
            _samples.Clear();
            return Max;
        }
        // Chromium's "back at max, reset" branch is unreachable: throttling >= 0.1 caps the limit at 90.
        _samples.Enqueue((int)MathF.Ceiling(Max * (1.0f - throttling)));
        if (_samples.Count > WindowSize) _samples.Dequeue();
        return _samples.Count < WindowSize ? Max : (int)(_samples.Sum(static x => (long)x) / WindowSize);
    }
}

/// <summary>Electron's Linux <c>PowerMonitor</c>: its <c>PowerObserverLinux</c>, created with the first listener.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class LinuxPowerMonitor
{
    private static readonly Lazy<LinuxLogind> s_logind = new(() => new LinuxLogind(
        Path.GetFileName(ProcessArgs.LinuxProgram() ?? Environment.ProcessPath ?? "oriel"),
        () => PowerMonitor.RaiseOnUi(PowerEvent.Suspend),
        () => PowerMonitor.RaiseOnUi(PowerEvent.Resume)));

    internal static LinuxLogind Start() => s_logind.Value;

    /// <summary>Electron's <c>setListeningForShutdown</c>, which its JS calls as shutdown listeners come and go.</summary>
    internal static void SetListeningForShutdown(this LinuxLogind logind, bool listening)
        => logind.SetShutdownHandler(listening ? PowerMonitor.ShouldShutdown : null);
}
