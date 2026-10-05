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

/// <summary>Power and session changes: sleep, wake, power source, screen lock, idle time. Electron's <c>powerMonitor</c>.</summary>
/// <remarks>Windows only for now: macOS and Linux throw <see cref="PlatformNotSupportedException"/>. Use it once the app is
/// ready; events are raised on the UI thread.</remarks>
public static class PowerMonitor
{
    private static EventHandler? s_suspended, s_resumed, s_onAc, s_onBattery, s_locked, s_unlocked;
    private static bool? s_onBatteryLast;

    /// <summary>The system is about to sleep. Electron's <c>'suspend'</c>.</summary>
    public static event EventHandler? Suspended { add => Add(ref s_suspended, value); remove => s_suspended -= value; }

    /// <summary>The system woke. Electron's <c>'resume'</c>.</summary>
    public static event EventHandler? Resumed { add => Add(ref s_resumed, value); remove => s_resumed -= value; }

    /// <summary>The machine switched to mains power. Electron's <c>'on-ac'</c>.</summary>
    public static event EventHandler? OnAc { add => Add(ref s_onAc, value); remove => s_onAc -= value; }

    /// <summary>The machine switched to battery. Electron's <c>'on-battery'</c>.</summary>
    public static event EventHandler? OnBattery { add => Add(ref s_onBattery, value); remove => s_onBattery -= value; }

    /// <summary>The screen was locked. Electron's <c>'lock-screen'</c>.</summary>
    public static event EventHandler? ScreenLocked { add => Add(ref s_locked, value); remove => s_locked -= value; }

    /// <summary>The screen was unlocked. Electron's <c>'unlock-screen'</c>.</summary>
    public static event EventHandler? ScreenUnlocked { add => Add(ref s_unlocked, value); remove => s_unlocked -= value; }

    /// <summary>Whether the machine runs on battery. Electron's <c>powerMonitor.isOnBatteryPower()</c>.</summary>
    public static bool IsOnBatteryPower() => OperatingSystem.IsWindows() ? WindowsSystem.OnBattery() ?? false : throw NotYet();

    /// <summary>Electron's <c>powerMonitor.onBatteryPower</c>.</summary>
    public static bool OnBatteryPower => IsOnBatteryPower();

    /// <summary>Seconds since the last keyboard or mouse input. Electron's <c>powerMonitor.getSystemIdleTime()</c>.</summary>
    public static int GetSystemIdleTime() => OperatingSystem.IsWindows() ? WindowsSystem.IdleSeconds() : throw NotYet();

    /// <summary>Locked, idle after <paramref name="idleThreshold"/> seconds without input, or active. Electron's
    /// <c>powerMonitor.getSystemIdleState()</c>.</summary>
    public static SystemIdleState GetSystemIdleState(int idleThreshold)
        => OperatingSystem.IsWindows() ? PowerRules.IdleState(WindowsSystem.IdleSeconds(), idleThreshold, WindowsSystem.IsLocked()) : throw NotYet();

    private static PlatformNotSupportedException NotYet() => new("powerMonitor is not implemented on this OS yet.");

    private static void Add(ref EventHandler? field, EventHandler? handler)
    {
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

    private static void Raise(PowerEvent e)
    {
        var handler = e switch
        {
            PowerEvent.Suspend => s_suspended,
            PowerEvent.Resume => s_resumed,
            PowerEvent.OnAc => s_onAc,
            PowerEvent.OnBattery => s_onBattery,
            PowerEvent.Lock => s_locked,
            PowerEvent.Unlock => s_unlocked,
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

    /// <summary>Electron's order: locked first, then idle once the input has been quiet for the threshold.</summary>
    internal static SystemIdleState IdleState(int idleSeconds, int threshold, bool locked)
        => locked ? SystemIdleState.Locked : idleSeconds >= threshold ? SystemIdleState.Idle : SystemIdleState.Active;
}
