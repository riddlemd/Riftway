using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What a blocker keeps awake. Electron's <c>type</c> for <c>powerSaveBlocker.start()</c>.</summary>
public enum PowerSaveBlockerType
{
    /// <summary>Keeps the system awake but lets the screen sleep. Electron's <c>prevent-app-suspension</c>.</summary>
    PreventAppSuspension,

    /// <summary>Keeps the screen on, and so the system. Electron's <c>prevent-display-sleep</c>.</summary>
    PreventDisplaySleep,
}

/// <summary>Keeps the system or screen from going to sleep. Electron's <c>powerSaveBlocker</c>.</summary>
/// <remarks>Windows power requests, Linux D-Bus inhibitors and macOS IOKit power assertions, as Chromium's
/// <c>PowerSaveBlocker</c>.</remarks>
public static class PowerSaveBlocker
{
    private static readonly IPowerSaveBlockerBackend s_backend = OperatingSystem.IsWindows() ? new WindowsPowerSaveBlockerBackend()
        : OperatingSystem.IsLinux() ? new LinuxPowerSaveBlockerBackend()
        : OperatingSystem.IsMacOS() ? new MacPowerSaveBlockerBackend()
        : new UnsupportedPowerSaveBlockerBackend();

    /// <summary>Starts blocking and returns the blocker's id. Electron's <c>powerSaveBlocker.start()</c>.</summary>
    /// <remarks>Like Electron it returns an id even if the system refuses the request.</remarks>
    public static int Start(PowerSaveBlockerType type) => s_backend.Start(type);

    /// <summary>Stops the blocker; false if there was none. Electron's <c>powerSaveBlocker.stop()</c>.</summary>
    public static bool Stop(int id) => s_backend.Stop(id);

    /// <summary>Whether the blocker is running. Electron's <c>powerSaveBlocker.isStarted()</c>.</summary>
    public static bool IsStarted(int id) => s_backend.IsStarted(id);
}

internal static class PowerSaveBlockers
{
    /// <summary>The one type that takes effect for the active blockers: display beats app suspension, as in Electron's
    /// <c>UpdatePowerSaveBlocker</c>; null when none is active.</summary>
    internal static PowerSaveBlockerType? Effective(IEnumerable<PowerSaveBlockerType> active)
    {
        PowerSaveBlockerType? result = null;
        foreach (var type in active)
        {
            if (type == PowerSaveBlockerType.PreventDisplaySleep) return type;
            result = type;
        }
        return result;
    }

    /// <summary>The <c>POWER_REQUEST_TYPE</c>s Chromium's <c>power_save_blocker_win.cc</c> sets: <c>PowerRequestDisplayRequired</c>
    /// (0) for the screen, plus <c>PowerRequestSystemRequired</c> (1) below Windows 11, which sleeps the system only after the
    /// display; <c>PowerRequestExecutionRequired</c> (3) for app suspension.</summary>
    internal static int[] RequestTypes(PowerSaveBlockerType type, bool windows11OrLater)
        => type == PowerSaveBlockerType.PreventDisplaySleep ? (windows11OrLater ? [0] : [0, 1]) : [3];

    /// <summary>The IOKit assertion type Chromium's <c>power_save_blocker_mac.cc</c> takes for <paramref name="type"/>
    /// (QA1340): <c>kIOPMAssertionTypeNoDisplaySleep</c> for the screen, <c>kIOPMAssertionTypeNoIdleSleep</c> for app
    /// suspension.</summary>
    internal static string MacAssertionType(PowerSaveBlockerType type)
        => type == PowerSaveBlockerType.PreventDisplaySleep ? "NoDisplaySleepAssertion" : "NoIdleSleepAssertion";

    /// <summary>The reason Windows shows in <c>powercfg /requests</c>: Electron's <c>ELECTRON_PRODUCT_NAME</c>, here the
    /// executable's product name, else the app's name.</summary>
    internal static string Reason(string? fileProductName, string? appName)
        => !string.IsNullOrWhiteSpace(fileProductName) ? fileProductName : !string.IsNullOrWhiteSpace(appName) ? appName : "Oriel";

    /// <summary>The executable's product name, when its version resource has one.</summary>
    internal static string? ProductName()
    {
        try { return Environment.ProcessPath is { } path ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName : null; }
        catch (IOException) { return null; }
    }
}

/// <summary>One OS's power save blockers.</summary>
internal interface IPowerSaveBlockerBackend
{
    int Start(PowerSaveBlockerType type);
    bool Stop(int id);
    bool IsStarted(int id);
}

/// <summary>One wake lock for the whole process at the highest active type, re-evaluated whenever a blocker starts or stops,
/// as Electron does.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsPowerSaveBlockerBackend : IPowerSaveBlockerBackend
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, PowerSaveBlockerType> _blockers = [];
    private readonly List<(nint Request, int Type)> _held = [];
    private PowerSaveBlockerType? _heldType;
    private int _nextId;

    public int Start(PowerSaveBlockerType type)
    {
        lock (_gate)
        {
            // Electron hands out the id whether or not the OS grants the request.
            var id = _nextId++;
            _blockers[id] = type;
            Update();
            return id;
        }
    }

    public bool Stop(int id)
    {
        lock (_gate)
        {
            var removed = _blockers.Remove(id);
            Update();
            return removed;
        }
    }

    public bool IsStarted(int id)
    {
        lock (_gate) return _blockers.ContainsKey(id);
    }

    private void Update()
    {
        var effective = PowerSaveBlockers.Effective(_blockers.Values);
        if (effective == _heldType) return;
        foreach (var (request, type) in _held) WindowsSystem.StopPowerRequest(request, type);
        _held.Clear();
        _heldType = effective;
        if (effective is not { } now) return;
        var reason = PowerSaveBlockers.Reason(PowerSaveBlockers.ProductName(), OrielApp.CurrentOrNull?.Name);
        foreach (var type in PowerSaveBlockers.RequestTypes(now, Environment.OSVersion.Version.Build >= 22000))
            if (WindowsSystem.StartPowerRequest(type, reason) is var request and not 0) _held.Add((request, type));
    }
}

/// <summary>Electron's single wake lock on Linux: Chromium's <c>WakeLock</c> holding one <c>PowerSaveBlocker</c> at the
/// highest active type, swapped for a new one when the type changes.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class LinuxPowerSaveBlockerBackend : IPowerSaveBlockerBackend
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, PowerSaveBlockerType> _blockers = [];
    private LinuxInhibitor? _held;
    private PowerSaveBlockerType? _heldType;
    private int _nextId;

    public int Start(PowerSaveBlockerType type)
    {
        lock (_gate)
        {
            var id = _nextId++;
            _blockers[id] = type;
            Update();
            return id;
        }
    }

    public bool Stop(int id)
    {
        lock (_gate)
        {
            var removed = _blockers.Remove(id);
            Update();
            return removed;
        }
    }

    public bool IsStarted(int id)
    {
        lock (_gate) return _blockers.ContainsKey(id);
    }

    private void Update()
    {
        var effective = PowerSaveBlockers.Effective(_blockers.Values);
        if (effective == _heldType) return;
        var old = _held;
        var next = effective is { } type
            ? new LinuxInhibitor(type == PowerSaveBlockerType.PreventDisplaySleep, ProcessArgs.LinuxProgram() ?? "",
                PowerSaveBlockers.Reason(PowerSaveBlockers.ProductName(), OrielApp.CurrentOrNull?.Name))
            : null;
        (_held, _heldType) = (next, effective);
        // SwapWakeLock's order: the new blocker's Init starts before the old one's CleanUp, and its Inhibit call goes out
        // after the old Uninhibit, once NameHasOwner has answered.
        LinuxInhibitor.Bus.Post(() =>
        {
            next?.SuspendScreenSaver();
            old?.CleanUp();
            next?.ApplyBlock();
        });
    }
}

/// <summary>Electron's single wake lock on macOS: Chromium's <c>WakeLock</c> holding one <c>PowerSaveBlocker</c> (an IOKit
/// assertion named with the product name) at the highest active type, swapped for a new one when the type changes.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class MacPowerSaveBlockerBackend : IPowerSaveBlockerBackend
{
    // Chromium's g_power_thread: IOPMAssertionCreate is a synchronous MIG call to configd, kept off the UI thread.
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> s_power = StartPowerThread();

    private readonly Lock _gate = new();
    private readonly Dictionary<int, PowerSaveBlockerType> _blockers = [];
    private Assertion? _held;
    private PowerSaveBlockerType? _heldType;
    private int _nextId;

    private sealed class Assertion
    {
        internal uint Id;
    }

    public int Start(PowerSaveBlockerType type)
    {
        lock (_gate)
        {
            var id = _nextId++;
            _blockers[id] = type;
            Update();
            return id;
        }
    }

    public bool Stop(int id)
    {
        lock (_gate)
        {
            var removed = _blockers.Remove(id);
            Update();
            return removed;
        }
    }

    public bool IsStarted(int id)
    {
        lock (_gate) return _blockers.ContainsKey(id);
    }

    private void Update()
    {
        var effective = PowerSaveBlockers.Effective(_blockers.Values);
        if (effective == _heldType) return;
        var old = _held;
        var next = effective is null ? null : new Assertion();
        (_held, _heldType) = (next, effective);
        // SwapWakeLock's order on one serial thread: the new assertion is taken before the old one is released.
        if (effective is { } type)
        {
            var reason = PowerSaveBlockers.Reason(PowerSaveBlockers.ProductName(), OrielApp.CurrentOrNull?.Name);
            s_power.Add(() => next!.Id = MacPower.CreateAssertion(PowerSaveBlockers.MacAssertionType(type), reason));
        }
        if (old is not null) s_power.Add(() => MacPower.ReleaseAssertion(old.Id));
    }

    /// <summary>Waits until everything queued on the power thread has run; for tests.</summary>
    internal static void Flush()
    {
        using var done = new ManualResetEventSlim();
        s_power.Add(done.Set);
        done.Wait();
    }

    private static System.Collections.Concurrent.BlockingCollection<Action> StartPowerThread()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<Action>();
        new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "PowerSaveBlocker" }.Start();
        return queue;
    }
}

internal sealed class UnsupportedPowerSaveBlockerBackend : IPowerSaveBlockerBackend
{
    private static PlatformNotSupportedException NotYet() => new("powerSaveBlocker is not implemented on this OS yet.");

    public int Start(PowerSaveBlockerType type) => throw NotYet();
    public bool Stop(int id) => throw NotYet();
    public bool IsStarted(int id) => throw NotYet();
}
