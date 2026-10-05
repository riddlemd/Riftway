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

/// <summary>The CPU speed limit changed. Electron's <c>'speed-limit-change'</c> data.</summary>
public sealed class SpeedLimitChangedEventArgs(int limit) : EventArgs
{
    /// <summary>The percentage of full speed the system allows, 0 to 100. Electron's <c>limit</c>.</summary>
    public int Limit { get; } = limit;
}

/// <summary>Power and session changes: sleep, wake, power source, screen lock, idle time. Electron's <c>powerMonitor</c>.</summary>
/// <remarks>Windows only for now: macOS and Linux throw <see cref="PlatformNotSupportedException"/>. Use it once the app is
/// ready; events are raised on the UI thread.</remarks>
public static class PowerMonitor
{
    private static EventHandler? s_suspended, s_resumed, s_onAc, s_onBattery, s_locked, s_unlocked;
    private static EventHandler<SpeedLimitChangedEventArgs>? s_speedLimit;
    private static bool? s_onBatteryLast;
    private static Timer? s_speedLimitTimer;
    private static readonly SpeedLimitFilter s_speedFilter = new();
    private static int s_speedLimitLast = SpeedLimitFilter.Max;

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

    /// <summary>The CPU speed limit changed, sampled once a second. Windows only; never raised on macOS or Linux. Electron's
    /// <c>'speed-limit-change'</c>.</summary>
    public static event EventHandler<SpeedLimitChangedEventArgs>? SpeedLimitChanged
    {
        add
        {
            if (!OperatingSystem.IsWindows()) throw NotYet();
            SystemHost.Start();
            s_speedLimit += value;
            s_speedLimitTimer ??= new Timer(static _ => { if (OperatingSystem.IsWindows()) SampleSpeedLimit(); }, null, 1000, 1000);
        }
        remove => s_speedLimit -= value;
    }

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

    /// <summary>Electron's order: locked first, then idle once the input has been quiet for the threshold.</summary>
    internal static SystemIdleState IdleState(int idleSeconds, int threshold, bool locked)
        => locked ? SystemIdleState.Locked : idleSeconds >= threshold ? SystemIdleState.Idle : SystemIdleState.Active;
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
