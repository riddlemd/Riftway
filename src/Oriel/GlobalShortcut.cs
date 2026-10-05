using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Keyboard shortcuts that work while the app is in the background. Electron's <c>globalShortcut</c>.</summary>
/// <remarks>Windows only for now: macOS and Linux throw <see cref="PlatformNotSupportedException"/>. Use it once the app is
/// ready; callbacks run on the UI thread.</remarks>
public static class GlobalShortcut
{
    private static readonly IGlobalShortcutBackend s_backend = OperatingSystem.IsWindows() ? new WindowsGlobalShortcutBackend() : new UnsupportedGlobalShortcutBackend();

    /// <summary>Calls <paramref name="callback"/> whenever <paramref name="accelerator"/> is pressed, in any app. False if the
    /// shortcut is taken, by another app or already by this one. Electron's <c>globalShortcut.register()</c>.</summary>
    /// <exception cref="ArgumentException">Not a valid accelerator.</exception>
    public static bool Register(string accelerator, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return s_backend.Register(GlobalShortcuts.Key(accelerator), callback);
    }

    /// <summary>Registers each accelerator with the same callback; those that are taken are skipped. Electron's
    /// <c>globalShortcut.registerAll()</c>.</summary>
    public static void RegisterAll(IEnumerable<string> accelerators, Action callback)
    {
        ArgumentNullException.ThrowIfNull(accelerators);
        foreach (var accelerator in accelerators) Register(accelerator, callback);
    }

    /// <summary>Whether this app registered <paramref name="accelerator"/>. Electron's <c>globalShortcut.isRegistered()</c>.</summary>
    public static bool IsRegistered(string accelerator) => s_backend.IsRegistered(GlobalShortcuts.Key(accelerator));

    /// <summary>Releases <paramref name="accelerator"/>. Electron's <c>globalShortcut.unregister()</c>.</summary>
    public static void Unregister(string accelerator) => s_backend.Unregister(GlobalShortcuts.Key(accelerator));

    /// <summary>Releases every shortcut this app registered. Electron's <c>globalShortcut.unregisterAll()</c>.</summary>
    public static void UnregisterAll() => s_backend.UnregisterAll();
}

/// <summary>A global shortcut as Windows registers it: <c>MOD_</c> flags and a virtual-key code.</summary>
internal readonly record struct HotKey(uint Modifiers, uint VirtualKey);

internal static class GlobalShortcuts
{
    /// <summary>The hotkey for an accelerator; equal combinations written differently give the same key, as Electron
    /// compares parsed accelerators. <c>MOD_NOREPEAT</c> makes a held key fire once.</summary>
    internal static HotKey Key(string accelerator)
    {
        ArgumentNullException.ThrowIfNull(accelerator);
        var parsed = Accelerator.Parse(accelerator, OperatingSystem.IsMacOS());
        return new HotKey(Modifiers(parsed.Modifiers) | 0x4000 /* MOD_NOREPEAT */, (uint)parsed.KeyCode);
    }

    /// <summary>Win32's <c>MOD_ALT</c>, <c>MOD_CONTROL</c>, <c>MOD_SHIFT</c>, <c>MOD_WIN</c>; Command is the Windows key.</summary>
    internal static uint Modifiers(AcceleratorModifiers m)
        => (m.HasFlag(AcceleratorModifiers.Alt) ? 0x1u : 0) | (m.HasFlag(AcceleratorModifiers.Control) ? 0x2u : 0)
            | (m.HasFlag(AcceleratorModifiers.Shift) ? 0x4u : 0) | (m.HasFlag(AcceleratorModifiers.Command) ? 0x8u : 0);
}

/// <summary>One OS's global shortcuts.</summary>
internal interface IGlobalShortcutBackend
{
    bool Register(HotKey key, Action callback);
    bool IsRegistered(HotKey key);
    void Unregister(HotKey key);
    void UnregisterAll();
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsGlobalShortcutBackend : IGlobalShortcutBackend
{
    private readonly Dictionary<HotKey, (int Id, Action Callback)> _registered = [];
    private int _nextId;

    public WindowsGlobalShortcutBackend() => SystemHost.Shortcuts = this;

    public bool Register(HotKey key, Action callback) => CefThread.InvokeOnUi(() =>
    {
        SystemHost.Start();
        // Application hotkey ids are 0x0000–0xBFFF.
        var id = ++_nextId & 0xBFFF;
        if (!WindowsSystem.RegisterHotKey(id, key.Modifiers, key.VirtualKey)) return false;
        _registered[key] = (id, callback);
        return true;
    });

    public bool IsRegistered(HotKey key) => CefThread.InvokeOnUi(() => _registered.ContainsKey(key));

    public void Unregister(HotKey key) => CefThread.InvokeOnUi(() =>
    {
        if (_registered.Remove(key, out var entry)) WindowsSystem.UnregisterHotKey(entry.Id);
        return true;
    });

    public void UnregisterAll() => CefThread.InvokeOnUi(() =>
    {
        foreach (var (id, _) in _registered.Values) WindowsSystem.UnregisterHotKey(id);
        _registered.Clear();
        return true;
    });

    /// <summary>A hotkey fired, on the UI thread.</summary>
    internal void Fired(int id)
    {
        foreach (var (_, entry) in _registered)
            if (entry.Id == id) entry.Callback();
    }
}

internal sealed class UnsupportedGlobalShortcutBackend : IGlobalShortcutBackend
{
    private static PlatformNotSupportedException NotYet() => new("globalShortcut is not implemented on this OS yet.");

    public bool Register(HotKey key, Action callback) => throw NotYet();
    public bool IsRegistered(HotKey key) => throw NotYet();
    public void Unregister(HotKey key) => throw NotYet();
    public void UnregisterAll() => throw NotYet();
}

/// <summary>The Windows host window behind <see cref="GlobalShortcut"/> and <see cref="PowerMonitor"/>, started once on the
/// UI thread.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class SystemHost
{
    private static bool s_started;

    internal static WindowsGlobalShortcutBackend? Shortcuts { get; set; }

    internal static void Start() => CefThread.InvokeOnUi(() =>
    {
        if (s_started) return true;
        s_started = true;
        WindowsSystem.Start(id => Shortcuts?.Fired(id), PowerMonitor.OnPowerBroadcast, PowerMonitor.OnSessionChange);
        return true;
    });
}
