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
/// <remarks>Windows only for now: macOS and Linux throw <see cref="PlatformNotSupportedException"/>.</remarks>
public static class PowerSaveBlocker
{
    private static readonly IPowerSaveBlockerBackend s_backend = OperatingSystem.IsWindows() ? new WindowsPowerSaveBlockerBackend() : new UnsupportedPowerSaveBlockerBackend();

    /// <summary>Starts blocking and returns the blocker's id. Electron's <c>powerSaveBlocker.start()</c>.</summary>
    /// <exception cref="InvalidOperationException">The system refused the request.</exception>
    public static int Start(PowerSaveBlockerType type) => s_backend.Start(type);

    /// <summary>Stops the blocker; false if there was none. Electron's <c>powerSaveBlocker.stop()</c>.</summary>
    public static bool Stop(int id) => s_backend.Stop(id);

    /// <summary>Whether the blocker is running. Electron's <c>powerSaveBlocker.isStarted()</c>.</summary>
    public static bool IsStarted(int id) => s_backend.IsStarted(id);
}

internal static class PowerSaveBlockers
{
    /// <summary>Win32's <c>POWER_REQUEST_TYPE</c> for a blocker: <c>PowerRequestDisplayRequired</c> for the screen,
    /// <c>PowerRequestSystemRequired</c> for the app.</summary>
    internal static int RequestType(PowerSaveBlockerType type) => type == PowerSaveBlockerType.PreventDisplaySleep ? 0 : 1;

    /// <summary>The reason Windows shows for the request in <c>powercfg /requests</c>.</summary>
    internal static string Reason(PowerSaveBlockerType type)
        => type == PowerSaveBlockerType.PreventDisplaySleep ? "Oriel: prevent-display-sleep" : "Oriel: prevent-app-suspension";
}

/// <summary>One OS's power save blockers.</summary>
internal interface IPowerSaveBlockerBackend
{
    int Start(PowerSaveBlockerType type);
    bool Stop(int id);
    bool IsStarted(int id);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsPowerSaveBlockerBackend : IPowerSaveBlockerBackend
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, (nint Request, int Type)> _requests = [];
    private int _nextId;

    public int Start(PowerSaveBlockerType type)
    {
        var requestType = PowerSaveBlockers.RequestType(type);
        var request = WindowsSystem.StartPowerRequest(requestType, PowerSaveBlockers.Reason(type));
        if (request == 0) throw new InvalidOperationException("Windows refused the power request.");
        lock (_gate)
        {
            var id = _nextId++;
            _requests[id] = (request, requestType);
            return id;
        }
    }

    public bool Stop(int id)
    {
        (nint Request, int Type) entry;
        lock (_gate)
            if (!_requests.Remove(id, out entry)) return false;
        WindowsSystem.StopPowerRequest(entry.Request, entry.Type);
        return true;
    }

    public bool IsStarted(int id)
    {
        lock (_gate) return _requests.ContainsKey(id);
    }
}

internal sealed class UnsupportedPowerSaveBlockerBackend : IPowerSaveBlockerBackend
{
    private static PlatformNotSupportedException NotYet() => new("powerSaveBlocker is not implemented on this OS yet.");

    public int Start(PowerSaveBlockerType type) => throw NotYet();
    public bool Stop(int id) => throw NotYet();
    public bool IsStarted(int id) => throw NotYet();
}
