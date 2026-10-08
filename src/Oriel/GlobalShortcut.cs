using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Keyboard shortcuts that work while the app is in the background. Electron's <c>globalShortcut</c>.</summary>
/// <remarks>Windows and Linux (X11 key grabs) for now: macOS throws <see cref="PlatformNotSupportedException"/>. Use it once
/// the app is ready; callbacks run on the UI thread.</remarks>
public static class GlobalShortcut
{
    private static readonly IGlobalShortcutBackend s_backend = OperatingSystem.IsWindows() ? new WindowsGlobalShortcutBackend()
        : OperatingSystem.IsLinux() ? new LinuxGlobalShortcutBackend() : new UnsupportedGlobalShortcutBackend();

    /// <summary>Calls <paramref name="callback"/> whenever <paramref name="accelerator"/> is pressed, in any app. False if the
    /// shortcut is taken, by another app or already by this one, or while shortcuts are suspended. Electron's
    /// <c>globalShortcut.register()</c>.</summary>
    /// <remarks>An <c>AltGr</c> shortcut is registered without AltGr and never runs its callback, as in Chromium
    /// (DRIFT J5).</remarks>
    /// <exception cref="ArgumentException">Not a valid accelerator.</exception>
    public static bool Register(string accelerator, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return s_backend.Register(GlobalShortcuts.Key(accelerator), callback);
    }

    /// <summary>Registers each accelerator with the same callback. If one cannot be registered, those this call registered
    /// are unregistered again and the result is false. Electron's <c>globalShortcut.registerAll()</c>.</summary>
    /// <exception cref="ArgumentException">One of them is not a valid accelerator; nothing is registered then.</exception>
    public static bool RegisterAll(IEnumerable<string> accelerators, Action callback)
    {
        ArgumentNullException.ThrowIfNull(accelerators);
        ArgumentNullException.ThrowIfNull(callback);
        return GlobalShortcuts.RegisterAll(s_backend, [.. accelerators.Select(GlobalShortcuts.Key)], callback);
    }

    /// <summary>Whether this app registered <paramref name="accelerator"/>, suspended or not. Electron's
    /// <c>globalShortcut.isRegistered()</c>.</summary>
    public static bool IsRegistered(string accelerator) => s_backend.IsRegistered(GlobalShortcuts.Key(accelerator));

    /// <summary>Releases <paramref name="accelerator"/>. Electron's <c>globalShortcut.unregister()</c>.</summary>
    public static void Unregister(string accelerator) => s_backend.Unregister(GlobalShortcuts.Key(accelerator));

    /// <summary>Releases every shortcut this app registered. Electron's <c>globalShortcut.unregisterAll()</c>.</summary>
    public static void UnregisterAll() => s_backend.UnregisterAll();

    /// <summary>Suspends or resumes every global shortcut: while suspended the OS registrations are released (so the keys
    /// reach other apps) and <see cref="Register"/> fails; resuming registers them again. Electron's
    /// <c>globalShortcut.setSuspended()</c>.</summary>
    public static void SetSuspended(bool suspended) => s_backend.SetSuspended(suspended);

    /// <summary>Whether global shortcuts are suspended. Electron's <c>globalShortcut.isSuspended()</c>.</summary>
    public static bool IsSuspended() => s_backend.IsSuspended();
}

/// <summary>A parsed global shortcut: Win32 <c>MOD_</c> flags, a virtual-key code, and AltGr, which is part of the
/// shortcut's identity (as <c>EF_ALTGR_DOWN</c> is in <c>ui::Accelerator</c>) but never reaches the OS.</summary>
internal readonly record struct HotKey(uint Modifiers, uint VirtualKey, bool AltGr = false);

internal static class GlobalShortcuts
{
    /// <summary>The hotkey for an accelerator; equal combinations written differently give the same key, as Electron
    /// compares parsed accelerators. No <c>MOD_NOREPEAT</c>: like Chromium, a held key repeats.</summary>
    internal static HotKey Key(string accelerator)
    {
        ArgumentNullException.ThrowIfNull(accelerator);
        var parsed = Accelerator.Parse(accelerator, OperatingSystem.IsMacOS());
        return new HotKey(Modifiers(parsed.Modifiers), (uint)parsed.KeyCode, parsed.Modifiers.HasFlag(AcceleratorModifiers.AltGr));
    }

    /// <summary>Win32's <c>MOD_ALT</c>, <c>MOD_CONTROL</c>, <c>MOD_SHIFT</c>, <c>MOD_WIN</c>; Command is the Windows key (Chromium drops it
    /// and registers the bare key; see DRIFT J2). AltGr has no flag: Chromium's listeners drop it.</summary>
    internal static uint Modifiers(AcceleratorModifiers m)
        => (m.HasFlag(AcceleratorModifiers.Alt) ? 0x1u : 0) | (m.HasFlag(AcceleratorModifiers.Control) ? 0x2u : 0)
            | (m.HasFlag(AcceleratorModifiers.Shift) ? 0x4u : 0) | (m.HasFlag(AcceleratorModifiers.Command) ? 0x8u : 0);

    /// <summary>The X modifier mask for a hotkey's <c>MOD_</c> flags: Shift, Control, Alt as Mod1, and the Windows key as
    /// Mod4, which Chromium leaves out (DRIFT J3).</summary>
    internal static ushort X11Modifiers(uint mod)
        => X11Keys.Modifiers(alt: (mod & 0x1) != 0, control: (mod & 0x2) != 0, shift: (mod & 0x4) != 0, super: (mod & 0x8) != 0);

    /// <summary>Electron's <c>GlobalShortcut::RegisterAll</c>: registers in order and, at the first failure, unregisters
    /// only those this call registered.</summary>
    internal static bool RegisterAll(IGlobalShortcutBackend backend, IReadOnlyList<HotKey> keys, Action callback)
    {
        var registered = new List<HotKey>();
        foreach (var key in keys)
        {
            if (!backend.Register(key, callback))
            {
                foreach (var done in registered) backend.Unregister(done);
                return false;
            }
            registered.Add(key);
        }
        return true;
    }
}

/// <summary>One OS's global shortcuts.</summary>
internal interface IGlobalShortcutBackend
{
    bool Register(HotKey key, Action callback);
    bool IsRegistered(HotKey key);
    void Unregister(HotKey key);
    void UnregisterAll();
    void SetSuspended(bool suspended);
    bool IsSuspended();
}

/// <summary>Chromium's <c>GlobalAcceleratorListener</c> with Electron's callback map: one registration per shortcut,
/// suspension that releases and retakes the OS registrations, and dispatch by the shortcut the OS reports.</summary>
/// <remarks>State is touched on the UI thread only (<see cref="OnUi{T}"/>).</remarks>
internal abstract class HotKeyListener : IGlobalShortcutBackend
{
    private readonly Dictionary<HotKey, Action> _callbacks = [];
    private bool _suspended;

    /// <summary>The OS registration (<c>StartListeningForAccelerator</c>); false when the OS refuses it.</summary>
    protected abstract bool StartListening(HotKey key);

    /// <summary>Releases the OS registration (<c>StopListeningForAccelerator</c>).</summary>
    protected abstract void StopListening(HotKey key);

    protected virtual T OnUi<T>(Func<T> func) => CefThread.InvokeOnUi(func);

    public bool Register(HotKey key, Action callback) => OnUi(() =>
    {
        if (_suspended || _callbacks.ContainsKey(key) || !StartListening(key)) return false;
        _callbacks[key] = callback;
        return true;
    });

    public bool IsRegistered(HotKey key) => OnUi(() => _callbacks.ContainsKey(key));

    public void Unregister(HotKey key) => OnUi(() =>
    {
        if (!_callbacks.ContainsKey(key)) return false;
        // A suspended shortcut holds no OS registration to release.
        if (!_suspended) StopListening(key);
        _callbacks.Remove(key);
        return true;
    });

    public void UnregisterAll() => OnUi(() =>
    {
        foreach (var key in _callbacks.Keys.ToList()) Unregister(key);
        return true;
    });

    /// <summary><c>SetShortcutHandlingSuspended</c>: releases every registration, or takes each again; a key another app
    /// took meanwhile stays registered here without firing, as Chromium ignores the result.</summary>
    public void SetSuspended(bool suspended) => OnUi(() =>
    {
        if (_suspended == suspended) return true;
        _suspended = suspended;
        foreach (var key in _callbacks.Keys.ToList())
        {
            if (suspended) StopListening(key);
            else _ = StartListening(key);
        }
        return true;
    });

    public bool IsSuspended() => OnUi(() => _suspended);

    /// <summary><c>NotifyKeyPressed</c>, on the UI thread: the OS reports the shortcut without AltGr, so an AltGr shortcut
    /// is never found (Electron then fails a <c>CHECK</c>; DRIFT J5).</summary>
    protected void Pressed(HotKey reported)
    {
        if (_callbacks.TryGetValue(reported with { AltGr = false }, out var callback)) callback();
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsGlobalShortcutBackend : HotKeyListener
{
    // UI thread only.
    private readonly Dictionary<HotKey, int> _ids = [];
    private int _nextId;

    public WindowsGlobalShortcutBackend() => SystemHost.Shortcuts = this;

    // Chromium's GlobalAcceleratorListenerWin: RegisterHotKey with Shift, Control and Alt; AltGr never reaches it.
    protected override bool StartListening(HotKey key)
    {
        SystemHost.Start();
        // Application hotkey ids are 0x0000–0xBFFF.
        var id = ++_nextId & 0xBFFF;
        if (!WindowsSystem.RegisterHotKey(id, key.Modifiers, key.VirtualKey)) return false;
        _ids[key] = id;
        return true;
    }

    protected override void StopListening(HotKey key)
    {
        if (_ids.Remove(key, out var id)) WindowsSystem.UnregisterHotKey(id);
    }

    /// <summary>A hotkey fired, on the UI thread.</summary>
    internal void Fired(int id)
    {
        foreach (var (key, keyId) in _ids)
        {
            if (keyId != id) continue;
            Pressed(key);
            return;
        }
    }
}

/// <summary>Chromium's <c>XGlobalShortcutListener</c>, which Electron uses on X11: <c>XGrabKey</c> on the root window
/// for each lock-modifier variant, failing when another client holds any of them. Like Chromium, a held key's
/// auto-repeated presses each run the callback.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class LinuxGlobalShortcutBackend : HotKeyListener
{
    // UI thread only. AltGr shortcuts grab the same key as their plain twin.
    private readonly Dictionary<HotKey, (byte Keycode, ushort Modifiers)> _grabs = [];

    public LinuxGlobalShortcutBackend() => X11System.KeyPressed += OnKeyPressed;

    protected override bool StartListening(HotKey key)
    {
        var modifiers = GlobalShortcuts.X11Modifiers(key.Modifiers);
        if (X11System.GrabKey(X11Keys.KeysymOf((int)key.VirtualKey), modifiers) is not { } keycode) return false;
        _grabs[key] = (keycode, modifiers);
        return true;
    }

    protected override void StopListening(HotKey key)
    {
        if (_grabs.Remove(key, out var grab)) X11System.UngrabKey(grab.Keycode, grab.Modifiers);
    }

    // X event thread. A press that arrives after Unregister or SetSuspended finds no grab, as Chromium's lookup does.
    private void OnKeyPressed(byte keycode, ushort state)
    {
        var modifiers = X11Keys.Relevant(state);
        try
        {
            CefThread.PostUi(() =>
            {
                foreach (var (key, grab) in _grabs)
                {
                    if (grab.Keycode != keycode || grab.Modifiers != modifiers) continue;
                    Pressed(key);
                    return;
                }
            });
        }
        // CEF is shutting down.
        catch (InvalidOperationException) { }
    }
}

internal sealed class UnsupportedGlobalShortcutBackend : IGlobalShortcutBackend
{
    private static PlatformNotSupportedException NotYet() => new("globalShortcut is not implemented on this OS yet.");

    public bool Register(HotKey key, Action callback) => throw NotYet();
    public bool IsRegistered(HotKey key) => throw NotYet();
    public void Unregister(HotKey key) => throw NotYet();
    public void UnregisterAll() => throw NotYet();
    public void SetSuspended(bool suspended) => throw NotYet();
    public bool IsSuspended() => throw NotYet();
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
