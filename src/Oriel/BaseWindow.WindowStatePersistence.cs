using System.Text.Json.Nodes;
using Oriel.Cef;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

public partial record BaseWindowOptions
{
    /// <summary>Identifies the window to Oriel, for <see cref="WindowStatePersistence"/>; not its title. Unique among
    /// open windows, and free again once the window is closed. Electron's <c>name</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Keeps the window's bounds and display mode across restarts, under its <see cref="Name"/> (nothing is kept
    /// without one); <c>true</c> keeps both. Automatically off where no real display is attached. While on with
    /// <see cref="WindowStatePersistence.DisplayMode"/>, <see cref="FullScreen"/> and <see cref="Kiosk"/> are ignored,
    /// as in Electron. Electron's <c>windowStatePersistence</c> (experimental).</summary>
    public WindowStatePersistence? WindowStatePersistence { get; init; }
}

public unsafe partial class BaseWindow
{
    // Names of windows not yet destroyed (Electron's WindowList), guarded by s_openGate.
    private static readonly HashSet<string> s_names = new(StringComparer.Ordinal);
    private static readonly TimeSpan s_saveDelay = TimeSpan.FromMilliseconds(200);

    // Electron's window_state_persistence_enabled_: the option, and a name to save under.
    private bool _persist;
    // Electron's restore_display_mode_ is set by the option even without a name, and still drops fullscreen/kiosk.
    private bool _restoreDisplayMode;
    private bool _restoreSavedBounds;
    // Electron's is_being_restored_ and awaiting_restore_display_mode_transition_.
    private bool _beingRestored;
    private bool _awaitingRestoreTransition;
    // Electron's restore_display_mode_callback_: the saved entry, applied on the first show.
    private JsonObject? _pendingDisplayMode;
    private int _saveGeneration;
    private bool _fullscreenTransitioning;

    /// <summary>The window's <see cref="BaseWindowOptions.Name"/>, or null.</summary>
    public string? Name => _options.Name is { Length: > 0 } name ? name : null;

    /// <summary>Raised after the saved bounds and display mode were applied to a new window (the display mode lands on
    /// its first show). Electron's <c>persisted-state-restored</c> event.</summary>
    public event EventHandler? PersistedStateRestored;

    /// <summary>Forgets the saved state of the window named <paramref name="name"/>; logs a warning when the name is
    /// empty or nothing is saved under it. Electron's <c>BaseWindow.clearPersistedState()</c>.</summary>
    /// <exception cref="InvalidOperationException">CEF is not running.</exception>
    public static void ClearPersistedState(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            Console.Error.WriteLine("[Oriel] Cannot clear persisted window state: window name is empty");
            return;
        }
        CefThread.InvokeOnUi(() =>
        {
            var states = WindowStateStore.Read();
            if (states.Remove(name)) WindowStateStore.Write(states);
            else Console.Error.WriteLine($"[Oriel] Window state '{name}' not found, nothing to clear");
            return true;
        });
    }

    // Electron's IsWindowNameValid and the persistence half of the NativeWindow constructor.
    private void ClaimName()
    {
        if (Name is { } name)
            lock (s_openGate)
                if (!s_names.Add(name))
                    throw new ArgumentException($"Window name '{name}' is already in use. Window names must be unique.");

        if (_options.WindowStatePersistence is not { } persistence) return;
        _restoreSavedBounds = persistence.Bounds;
        _restoreDisplayMode = persistence.DisplayMode;
        if (Name is null)
        {
            Console.Error.WriteLine("[Oriel] Window state persistence enabled but no window name provided. Window state will not be persisted.");
            return;
        }
        _persist = true;
        // Subscribed first, so the guard lifts before the app's handlers run, as Electron's Notify* does.
        Maximized += OnRestoreTransitionDone;
        EnteredFullScreen += OnRestoreTransitionDone;
        Unmaximized += DebouncedSave;
        LeftFullScreen += DebouncedSave;
        Moved += DebouncedSave;
        Resized += DebouncedSave;
    }

    private protected void ReleaseName()
    {
        if (Name is { } name)
            lock (s_openGate) s_names.Remove(name);
    }

    private void OnRestoreTransitionDone(object? sender, EventArgs e)
    {
        if (_awaitingRestoreTransition)
        {
            _awaitingRestoreTransition = false;
            _beingRestored = false;
        }
        DebouncedSave(sender, e);
    }

    private void DebouncedSave(object? sender, EventArgs e)
    {
        var generation = ++_saveGeneration;
        CefThread.PostUiDelayed(() =>
        {
            if (generation == _saveGeneration && _window is not null) SaveWindowState(_window);
        }, s_saveDelay);
    }

    // Electron's FlushWindowState, run once a close is approved and the bounds are still real.
    private void FlushWindowState(cef_window_t* w)
    {
        if (!_persist) return;
        if (_pendingDisplayMode is not null)
        {
            _pendingDisplayMode = null;
            _beingRestored = false;
            _awaitingRestoreTransition = false;
        }
        _saveGeneration++;
        SaveWindowState(w);
    }

    // Electron's NativeWindow::SaveWindowState.
    private void SaveWindowState(cef_window_t* w)
    {
        if (!_persist || _beingRestored || _fullscreenTransitioning) return;
        var bounds = OuterBoundsOn(w);
        if (bounds.Width == 0 || bounds.Height == 0)
        {
            Console.Error.WriteLine("[Oriel] Window state not saved - window bounds are invalid");
            return;
        }
        var display = Screen.GetDisplayMatching(bounds);
        if (WindowStateRules.IsInvalidDisplay(display.Id, display.Bounds))
        {
            Console.Error.WriteLine("[Oriel] Window state not saved - no physical display attached or current display has invalid bounds");
            return;
        }
        var states = WindowStateStore.Read();
        var name = Name!;
        var saved = WindowStateRules.BoundsToSave(bounds, IsNormal(w), states[name] as JsonObject);
        var fullscreen = w->is_fullscreen(w) != 0;
        states[name] = WindowStateRules.ToJson(saved, IsMaximizedOn(w), fullscreen,
            OperatingSystem.IsMacOS() ? _kiosk : fullscreen, display.WorkArea);
        WindowStateStore.Write(states);
    }

    // Electron's NativeWindow::RestoreWindowState: bounds now, the display mode on the first show.
    private void RestoreWindowState(cef_window_t* w)
    {
        if (!_persist || WindowStateStore.Read()[Name!] is not JsonObject entry) return;
        if (WindowStateRules.Parse(entry) is not { } saved)
        {
            Console.Error.WriteLine("[Oriel] Window state not restored - corrupted values found");
            return;
        }
        var displays = Screen.GetAllDisplays();
        var primary = Screen.GetPrimaryDisplay();
        var index = WindowStateRules.PickDisplay(saved.Bounds, [.. displays.Select(d => d.WorkArea)], -1);
        var display = index < 0 ? primary : displays[index];
        if (WindowStateRules.IsInvalidDisplay(display.Id, display.Bounds))
        {
            Console.Error.WriteLine("[Oriel] Window state not restored - no physical display attached or current display has invalid bounds");
            return;
        }

        _beingRestored = true;
        if (_restoreSavedBounds)
        {
            if (WindowStateRules.RestoreBounds(saved.Bounds, saved.WorkArea, display.WorkArea, OperatingSystem.IsMacOS()) is { } bounds)
                ApplyOuterBounds(w, bounds, animate: false);
            else Console.Error.WriteLine("[Oriel] Window bounds not restored - values are invalid");
        }
        if (_restoreDisplayMode) _pendingDisplayMode = entry;
        else _beingRestored = false;
        CefThread.PostUi(() => PersistedStateRestored?.Invoke(this, EventArgs.Empty));
    }

    // Electron's FlushPendingDisplayMode, run by Show and ShowInactive before the window appears: kiosk, then full
    // screen, then maximized.
    private void FlushPendingDisplayMode(cef_window_t* w)
    {
        if (_pendingDisplayMode is not { } entry) return;
        _pendingDisplayMode = null;
        if (WindowStateRules.Flag(entry, "kiosk"))
        {
            _awaitingRestoreTransition = true;
            _kiosk = true;
            EnterFullScreenOn(w);
        }
        else if (WindowStateRules.Flag(entry, "fullscreen"))
        {
            _awaitingRestoreTransition = true;
            EnterFullScreenOn(w);
        }
        else if (WindowStateRules.Flag(entry, "maximized"))
        {
            _awaitingRestoreTransition = true;
            MaximizeOn(w);
        }
        else _beingRestored = false;
    }

    private void EnterFullScreenOn(cef_window_t* w)
    {
        if (UsesSimpleFullScreen && OperatingSystem.IsMacOS()) MacWindow.SetSimpleFullScreen((nint)w->get_window_handle(w), true);
        else
        {
            _sizeState?.SetFullScreen(true);
            w->set_fullscreen(w, 1);
        }
    }
}
