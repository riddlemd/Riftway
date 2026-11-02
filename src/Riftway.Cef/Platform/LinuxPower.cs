using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Electron's <c>PowerObserverLinux</c>: logind's <c>PrepareForSleep</c> and <c>PrepareForShutdown</c> on the
/// system bus, with the delay inhibitors that give the app time to react.</summary>
/// <remarks>A sleep delay lock is taken once logind is on the bus, released after <c>suspend</c> is raised and taken
/// again before <c>resume</c>. A shutdown delay lock is held while a shutdown handler is set; it is released after the
/// handler unless the handler asks to keep it, which delays shutdown until logind's <c>InhibitDelayMaxSec</c>.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxLogind
{
    public const string Service = "org.freedesktop.login1";
    public const string ObjectPath = "/org/freedesktop/login1";
    public const string Manager = "org.freedesktop.login1.Manager";

    private readonly LinuxDBus _bus;
    private readonly string _who;
    private readonly Action _suspend, _resume;
    // Bus thread only.
    private LinuxFd? _sleepLock, _shutdownLock;
    private Func<bool>? _shouldShutdown;
    private bool _waitingForService;

    /// <param name="who">The inhibitor's owner: the executable's base name, as Electron's <c>GetExecutableBaseName</c>.</param>
    /// <param name="suspend">Runs before the sleep lock is released.</param>
    /// <param name="resume">Runs after the sleep lock is taken again.</param>
    public LinuxLogind(string who, Action suspend, Action resume)
    {
        (_who, _suspend, _resume) = (who, suspend, resume);
        _bus = LinuxDBus.Open(system: true, "Riftway D-Bus logind");
        _bus.Signal += OnSignal;
        _bus.Post(Connect);
    }

    /// <summary>Electron's <c>SetShutdownHandler</c>: a handler takes the shutdown delay lock, none releases it. The
    /// handler runs on the bus thread and returns whether shutdown may go on.</summary>
    public void SetShutdownHandler(Func<bool>? handler) => _bus.Post(() =>
    {
        if (handler is not null && _shouldShutdown is null) BlockShutdown();
        else if (handler is null && _shouldShutdown is not null) _shutdownLock?.Dispose();
        _shouldShutdown = handler;
    });

    /// <summary>Runs <paramref name="probe"/> on the bus thread after everything posted before it; for tests.</summary>
    public void Post(Action probe) => _bus.Post(probe);

    private void Connect()
    {
        foreach (var member in (string[])["PrepareForShutdown", "PrepareForSleep"])
            _bus.AddMatch($"type='signal',sender='{Service}',interface='{Manager}',member='{member}',path='{ObjectPath}'");
        // dbus::ObjectProxy::WaitForServiceToBeAvailable: now if logind has an owner, else when it gets one.
        if (_bus.NameHasOwner(Service) == true)
        {
            BlockSleep();
            return;
        }
        _waitingForService = _bus.AddMatch($"type='signal',sender='org.freedesktop.DBus',interface='org.freedesktop.DBus',member='NameOwnerChanged',arg0='{Service}'");
    }

    private void OnSignal(LinuxDBusSignal signal)
    {
        if (signal is { Interface: "org.freedesktop.DBus", Member: "NameOwnerChanged", Args: [string name, _, string owner] })
        {
            if (_waitingForService && name == Service && owner.Length > 0)
            {
                _waitingForService = false;
                BlockSleep();
            }
            return;
        }
        if (signal.Interface != Manager || signal.Path != ObjectPath) return;
        // dbus::MessageReader::PopBool: the first argument must be a boolean; anything after it is ignored.
        if (signal.Args is not [bool value, ..]) return;
        if (signal.Member == "PrepareForSleep")
        {
            if (value)
            {
                _suspend();
                _sleepLock?.Dispose();
            }
            else
            {
                BlockSleep();
                _resume();
            }
        }
        else if (signal.Member == "PrepareForShutdown" && value && (_shouldShutdown is null || _shouldShutdown()))
        {
            _shutdownLock?.Dispose();
        }
    }

    // A failed call leaves the lock as it was; a new fd replaces (and closes) the old, as base::ScopedFD does.
    private void BlockSleep()
    {
        if (Inhibit("sleep", "Application cleanup before suspend") is not { } fd) return;
        _sleepLock?.Dispose();
        _sleepLock = fd;
    }

    private void BlockShutdown()
    {
        if (_shutdownLock is { IsValid: true }) return;
        _shutdownLock = Inhibit("shutdown", "Ensure a clean shutdown");
    }

    // The reply's fd is the lock: logind waits until it is closed, up to InhibitDelayMaxSec.
    private LinuxFd? Inhibit(string what, string why)
        => _bus.Call(Service, ObjectPath, Manager, "Inhibit", what, _who, why, "delay") is [LinuxFd fd, ..] ? fd : null;
}

/// <summary>Chromium's <c>DBusScreenSaverWatcher</c> (ui/base/idle/idle_linux.cc): the first screen saver service on the
/// session bus that answers <c>GetActive</c> gives the lock state, kept current by its <c>ActiveChanged</c> signal.</summary>
/// <remarks>Started at the first question, which still gets "unknown" while the services are tried, as in Chromium.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxScreenSaverWatcher
{
    private static readonly Lazy<LinuxScreenSaverWatcher> s_instance = new(() => new LinuxScreenSaverWatcher());
    private readonly LinuxDBus _bus = LinuxSessionBus.Instance;
    private int _current = -1;
    private volatile int _state;

    /// <summary>The watcher, started on first use.</summary>
    public static LinuxScreenSaverWatcher Instance => s_instance.Value;

    /// <summary>True locked, false unlocked, null unknown (no working service, or not found yet).</summary>
    public bool? Locked => _state switch { 1 => true, 2 => false, _ => null };

    private LinuxScreenSaverWatcher()
    {
        _bus.Signal += OnSignal;
        _bus.Post(Find);
    }

    // TryCurrentService: a missing name, a refused match or a failing GetActive moves on to the next service.
    private void Find()
    {
        for (var i = 0; i < LinuxScreenLock.Services.Length; i++)
        {
            var (service, path, iface) = LinuxScreenLock.Services[i];
            if (_bus.NameHasOwner(service) != true) continue;
            if (!_bus.AddMatch($"type='signal',sender='{service}',interface='{iface}',member='ActiveChanged',path='{path}'")) continue;
            _current = i;
            if (_bus.Call(service, path, iface, "GetActive") is { } reply && LinuxScreenLock.LockStateOf(reply) is { } active)
            {
                _state = active ? 1 : 2;
                return;
            }
        }
        _current = -1;
    }

    private void OnSignal(LinuxDBusSignal signal)
    {
        if (_current < 0 || signal.Member != "ActiveChanged" || signal.Interface != LinuxScreenLock.Services[_current].Interface) return;
        if (LinuxScreenLock.LockStateOf(signal.Args) is { } active) _state = active ? 1 : 2;
    }
}

/// <summary>Chromium's tables for <c>DBusScreenSaverWatcher</c>.</summary>
public static class LinuxScreenLock
{
    /// <summary><c>kServices</c>, in Chromium's order: name, object path, interface.</summary>
    public static readonly (string Service, string Path, string Interface)[] Services =
    [
        ("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver"),
        ("org.cinnamon.ScreenSaver", "/org/cinnamon/ScreenSaver", "org.cinnamon.ScreenSaver"),
        ("org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver"),
        ("org.mate.ScreenSaver", "/org/mate/ScreenSaver", "org.mate.ScreenSaver"),
        ("org.xfce.ScreenSaver", "/org/xfce/ScreenSaver", "org.xfce.ScreenSaver"),
    ];

    /// <summary>Chromium's <c>UpdateLockState</c>: a single boolean is the state; anything else is not an answer.</summary>
    public static bool? LockStateOf(object?[] args) => args is [bool active] ? active : null;
}

/// <summary>The session bus connection Chromium's <c>GetSharedSessionBus</c> stands for: the screen saver watcher and
/// the power save blockers share it.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxSessionBus
{
    private static readonly Lazy<LinuxDBus> s_bus = new(() => LinuxDBus.Open(system: false, "Riftway D-Bus session"));

    public static LinuxDBus Instance => s_bus.Value;
}

/// <summary>The D-Bus inhibit APIs of Chromium's <c>power_save_blocker_linux.cc</c>.</summary>
public enum LinuxInhibitApi
{
    /// <summary><c>org.gnome.SessionManager</c>, tried first for every type.</summary>
    Gnome,

    /// <summary><c>org.freedesktop.PowerManagement</c>, the fallback for app suspension.</summary>
    FreedesktopPower,

    /// <summary><c>org.freedesktop.ScreenSaver</c>, the fallback for display sleep.</summary>
    FreedesktopScreenSaver,
}

/// <summary>Chromium's tables for <c>power_save_blocker_linux.cc</c>.</summary>
public static class LinuxInhibit
{
    /// <summary><c>kServiceInfos</c>: service name, interface, object path.</summary>
    public static (string Service, string Interface, string Path) ServiceOf(LinuxInhibitApi api) => api switch
    {
        LinuxInhibitApi.Gnome => ("org.gnome.SessionManager", "org.gnome.SessionManager", "/org/gnome/SessionManager"),
        LinuxInhibitApi.FreedesktopPower => ("org.freedesktop.PowerManagement", "org.freedesktop.PowerManagement.Inhibit", "/org/freedesktop/PowerManagement/Inhibit"),
        _ => ("org.freedesktop.ScreenSaver", "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver"),
    };

    /// <summary><c>FallbackDBusApiForWakeLockType</c>.</summary>
    public static LinuxInhibitApi FallbackOf(bool display) => display ? LinuxInhibitApi.FreedesktopScreenSaver : LinuxInhibitApi.FreedesktopPower;

    /// <summary><c>WakeLockTypeToGnomeInhibitFlag</c>: suspend-session (4) for app suspension, mark-session-idle (8) for
    /// display sleep.</summary>
    public static uint GnomeFlags(bool display) => display ? 8u : 4u;

    /// <summary><c>GetUninhibitMethodName</c>: GNOME spells it <c>Uninhibit</c>, freedesktop <c>UnInhibit</c>.</summary>
    public static string UninhibitMethodOf(LinuxInhibitApi api) => api == LinuxInhibitApi.Gnome ? "Uninhibit" : "UnInhibit";

    /// <summary><c>Inhibit</c>'s arguments: GNOME takes (app_id, toplevel_xid 0, reason, flags), freedesktop
    /// (app_id, reason).</summary>
    public static object[] InhibitArgs(LinuxInhibitApi api, string appId, string reason, bool display)
        => api == LinuxInhibitApi.Gnome ? [appId, 0u, reason, GnomeFlags(display)] : [appId, reason];
}

/// <summary>One Chromium <c>PowerSaveBlocker::Delegate</c> on Linux: a D-Bus inhibit (GNOME first, else the freedesktop
/// API for its type) plus a suspension of the X screen saver, for display sleep and app suspension alike.</summary>
/// <remarks>Its methods run on the session bus thread, through <see cref="LinuxSessionBus"/>.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxInhibitor(bool display, string appId, string reason)
{
    private readonly LinuxDBus _bus = LinuxSessionBus.Instance;
    private readonly List<(LinuxInhibitApi Api, uint Cookie)> _cookies = [];
    private readonly Dictionary<LinuxInhibitApi, bool> _available = [];
    private bool _suspending;

    /// <summary>The bus all calls go through; work for it must be posted there.</summary>
    public static LinuxDBus Bus => LinuxSessionBus.Instance;

    /// <summary>The screen saver half of <c>Init</c>: <c>SetScreenSaverSuspended(true)</c>.</summary>
    public void SuspendScreenSaver() => _suspending = X11System.SuspendScreenSaver(true);

    /// <summary><c>ApplyBlock</c>: GNOME's session manager first, falling back when it is missing or refuses.</summary>
    public void ApplyBlock() => DoInhibitCall(LinuxInhibitApi.Gnome);

    /// <summary><c>CleanUp</c>: uninhibits every cookie and lifts the screen saver suspension.</summary>
    public void CleanUp()
    {
        foreach (var (api, cookie) in _cookies)
        {
            var (service, iface, path) = LinuxInhibit.ServiceOf(api);
            _ = _bus.Call(service, path, iface, LinuxInhibit.UninhibitMethodOf(api), cookie);
        }
        _cookies.Clear();
        if (_suspending) X11System.SuspendScreenSaver(false);
        _suspending = false;
    }

    private void DoInhibitCall(LinuxInhibitApi api)
    {
        var (service, iface, path) = LinuxInhibit.ServiceOf(api);
        if (!_available.TryGetValue(api, out var available)) _available[api] = available = _bus.NameHasOwner(service) ?? false;
        if (!available)
        {
            if (api == LinuxInhibitApi.Gnome) DoInhibitCall(LinuxInhibit.FallbackOf(display));
            return;
        }
        if (_bus.Call(service, path, iface, "Inhibit", LinuxInhibit.InhibitArgs(api, appId, reason, display)) is [uint cookie, ..])
            _cookies.Add((api, cookie));
        else if (api == LinuxInhibitApi.Gnome)
            DoInhibitCall(LinuxInhibit.FallbackOf(display));
    }
}
