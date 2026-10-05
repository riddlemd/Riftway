using System.ComponentModel;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Arguments for <see cref="WebContents.RenderProcessGone"/>.</summary>
/// <param name="Reason">Why the process went: <c>abnormal-exit</c>, <c>killed</c>, <c>crashed</c>, <c>oom</c>,
/// <c>launch-failed</c> or <c>integrity-failure</c> (Electron's <c>details.reason</c>; <c>clean-exit</c> never
/// arrives, see drift C13).</param>
/// <param name="ExitCode">The process's exit code. Electron's <c>details.exitCode</c>.</param>
public sealed record RenderProcessGoneEventArgs(string Reason, int ExitCode);

/// <summary>Arguments for <see cref="WebContents.PageFaviconUpdated"/>.</summary>
/// <param name="Favicons">The page's favicon URLs. Electron's <c>favicons</c>.</param>
public sealed record PageFaviconUpdatedEventArgs(IReadOnlyList<string> Favicons);

/// <summary>A key event as <see cref="WebContents.BeforeInputEvent"/> sees it: Electron's <c>Input</c> object.</summary>
/// <param name="Type">Down or up. Electron's <c>type</c> (<c>keyDown</c> / <c>keyUp</c>).</param>
/// <param name="Key">The DOM <c>KeyboardEvent.key</c> value.</param>
/// <param name="Code">The DOM <c>KeyboardEvent.code</c> value, assuming a US layout (drift C16).</param>
/// <param name="IsAutoRepeat">The key is held and repeating.</param>
/// <param name="IsComposing">Always false: CEF does not say (drift C16).</param>
/// <param name="Location">0 standard, 1 left, 2 right, 3 numeric keypad; the DOM <c>location</c>.</param>
/// <param name="Modifiers">The modifiers held, including <see cref="InputModifiers.IsKeypad"/> and <see cref="InputModifiers.IsAutoRepeat"/>.</param>
public sealed record KeyInput(KeyboardInputEventType Type, string Key, string Code, bool IsAutoRepeat, bool IsComposing, int Location, InputModifiers Modifiers)
{
    /// <summary>Shift is held.</summary>
    public bool Shift => Modifiers.HasFlag(InputModifiers.Shift);

    /// <summary>Control is held.</summary>
    public bool Control => Modifiers.HasFlag(InputModifiers.Control);

    /// <summary>Alt is held.</summary>
    public bool Alt => Modifiers.HasFlag(InputModifiers.Alt);

    /// <summary>Meta (Command on macOS) is held.</summary>
    public bool Meta => Modifiers.HasFlag(InputModifiers.Meta);
}

/// <summary>Arguments for <see cref="WebContents.BeforeInputEvent"/>. Setting <see cref="CancelEventArgs.Cancel"/>
/// (Electron's <c>event.preventDefault()</c>) keeps the key from the page and the menu accelerators.</summary>
public sealed class BeforeInputEventArgs : CancelEventArgs
{
    internal BeforeInputEventArgs(KeyInput input) => Input = input;

    /// <summary>The key event.</summary>
    public KeyInput Input { get; }
}

/// <summary>The cross-cutting parts of <see cref="WebContents"/>: identity, process health, favicons and key interception.</summary>
public sealed unsafe partial class WebContents
{
    private static readonly Lock s_registryGate = new();
    private static readonly List<WebContents> s_live = [];
    private static int s_nextId;
    private bool _unresponsive;

    /// <summary>A number unique among this process's pages, from 1 and never reused. Electron's <c>webContents.id</c>.</summary>
    public int Id { get; private set; }

    /// <summary>All pages that are not destroyed. Electron's <c>webContents.getAllWebContents()</c>.</summary>
    public static IReadOnlyList<WebContents> GetAllWebContents()
    {
        lock (s_registryGate) return [.. s_live];
    }

    /// <summary>The page with <paramref name="id"/>, or null. Electron's <c>webContents.fromId()</c>.</summary>
    public static WebContents? FromId(int id)
    {
        lock (s_registryGate) return s_live.Find(w => w.Id == id);
    }

    /// <summary>The focused window's page, or null. Electron's <c>webContents.getFocusedWebContents()</c>.</summary>
    public static WebContents? GetFocusedWebContents() => BrowserWindow.GetFocusedWindow()?.WebContents;

    /// <summary>The page's renderer process ended unexpectedly. Electron's <c>render-process-gone</c>.</summary>
    public event EventHandler<RenderProcessGoneEventArgs>? RenderProcessGone;

    /// <summary>The page stopped answering input. Electron's <c>unresponsive</c>.</summary>
    public event EventHandler? Unresponsive;

    /// <summary>The page answers input again after <see cref="Unresponsive"/>. Electron's <c>responsive</c>.</summary>
    public event EventHandler? Responsive;

    /// <summary>The page's favicon URLs changed. Electron's <c>page-favicon-updated</c>.</summary>
    public event EventHandler<PageFaviconUpdatedEventArgs>? PageFaviconUpdated;

    /// <summary>A key went down or up, before the page and the menu accelerators see it; cancel to swallow it.
    /// Electron's <c>before-input-event</c>. Raised on the UI thread, so the decision must be made synchronously.</summary>
    public event EventHandler<BeforeInputEventArgs>? BeforeInputEvent;

    private void RegisterLifecycle()
    {
        lock (s_registryGate)
        {
            Id = ++s_nextId;
            s_live.Add(this);
        }
    }

    private void UnregisterLifecycle()
    {
        lock (s_registryGate) s_live.Remove(this);
    }

    private void OnRenderProcessTerminated(cef_termination_status_t status, int exitCode)
        => RenderProcessGone?.Invoke(this, new RenderProcessGoneEventArgs(RenderProcessGoneReason.Of(status), exitCode));

    private void OnRenderProcessUnresponsive()
    {
        // CEF repeats the report each time the wait expires; Electron raises one event per hang.
        if (_unresponsive) return;
        _unresponsive = true;
        Unresponsive?.Invoke(this, EventArgs.Empty);
    }

    private void OnRenderProcessResponsive()
    {
        if (!_unresponsive) return;
        _unresponsive = false;
        Responsive?.Invoke(this, EventArgs.Empty);
    }

    private void OnFaviconUrlsChanged(IReadOnlyList<string> urls) => PageFaviconUpdated?.Invoke(this, new PageFaviconUpdatedEventArgs(urls));

    /// <summary>A key on its way to the page; true when a <see cref="BeforeInputEvent"/> listener cancelled it.</summary>
    internal bool OnPreKeyEvent(cef_key_event_t* key)
    {
        if (BeforeInputEvent is not { } handler) return false;
        if (KeyInputMapper.Map(key->type, key->modifiers, key->windows_key_code, key->character, key->unmodified_character) is not { } input) return false;
        var args = new BeforeInputEventArgs(input);
        handler(this, args);
        return args.Cancel;
    }
}

/// <summary>Maps CEF's termination status to Electron's <c>render-process-gone</c> reason strings.</summary>
internal static class RenderProcessGoneReason
{
    internal static string Of(cef_termination_status_t status) => status switch
    {
        cef_termination_status_t.TS_PROCESS_WAS_KILLED => "killed",
        cef_termination_status_t.TS_PROCESS_CRASHED => "crashed",
        cef_termination_status_t.TS_PROCESS_OOM => "oom",
        cef_termination_status_t.TS_LAUNCH_FAILED => "launch-failed",
        cef_termination_status_t.TS_INTEGRITY_FAILURE => "integrity-failure",
        _ => "abnormal-exit",
    };
}

/// <summary>Derives Electron's <c>Input</c> fields from CEF's key event, which carries Windows key codes and characters
/// rather than DOM <c>key</c> / <c>code</c> values.</summary>
internal static class KeyInputMapper
{
    private static readonly Dictionary<int, (string Key, string Code)> s_named = new()
    {
        [8] = ("Backspace", "Backspace"), [9] = ("Tab", "Tab"), [13] = ("Enter", "Enter"), [16] = ("Shift", "ShiftLeft"),
        [17] = ("Control", "ControlLeft"), [18] = ("Alt", "AltLeft"), [19] = ("Pause", "Pause"), [20] = ("CapsLock", "CapsLock"),
        [27] = ("Escape", "Escape"), [32] = (" ", "Space"), [33] = ("PageUp", "PageUp"), [34] = ("PageDown", "PageDown"),
        [35] = ("End", "End"), [36] = ("Home", "Home"), [37] = ("ArrowLeft", "ArrowLeft"), [38] = ("ArrowUp", "ArrowUp"),
        [39] = ("ArrowRight", "ArrowRight"), [40] = ("ArrowDown", "ArrowDown"), [44] = ("PrintScreen", "PrintScreen"),
        [45] = ("Insert", "Insert"), [46] = ("Delete", "Delete"), [91] = ("Meta", "MetaLeft"), [92] = ("Meta", "MetaRight"),
        [93] = ("ContextMenu", "ContextMenu"), [144] = ("NumLock", "NumLock"), [145] = ("ScrollLock", "ScrollLock"),
        [160] = ("Shift", "ShiftLeft"), [161] = ("Shift", "ShiftRight"), [162] = ("Control", "ControlLeft"),
        [163] = ("Control", "ControlRight"), [164] = ("Alt", "AltLeft"), [165] = ("Alt", "AltRight"),
    };

    // US-layout punctuation; other layouts put other keys on these virtual codes.
    private static readonly Dictionary<int, string> s_punctuation = new()
    {
        [186] = "Semicolon", [187] = "Equal", [188] = "Comma", [189] = "Minus", [190] = "Period", [191] = "Slash",
        [192] = "Backquote", [219] = "BracketLeft", [220] = "Backslash", [221] = "BracketRight", [222] = "Quote",
    };

    private static readonly Dictionary<int, string> s_keypadOperators = new()
    {
        [106] = "Multiply", [107] = "Add", [109] = "Subtract", [110] = "Decimal", [111] = "Divide",
    };

    /// <summary>Null for events Electron does not report: typed characters.</summary>
    internal static KeyInput? Map(cef_key_event_type_t type, uint cefModifiers, int vk, char character, char unmodified)
    {
        var kind = type switch
        {
            cef_key_event_type_t.KEYEVENT_RAWKEYDOWN or cef_key_event_type_t.KEYEVENT_KEYDOWN => KeyboardInputEventType.KeyDown,
            cef_key_event_type_t.KEYEVENT_KEYUP => KeyboardInputEventType.KeyUp,
            _ => (KeyboardInputEventType?)null,
        };
        if (kind is not { } eventType) return null;

        var modifiers = InputModifiers.None;
        if ((cefModifiers & (uint)cef_event_flags_t.EVENTFLAG_SHIFT_DOWN) != 0) modifiers |= InputModifiers.Shift;
        if ((cefModifiers & (uint)cef_event_flags_t.EVENTFLAG_CONTROL_DOWN) != 0) modifiers |= InputModifiers.Control;
        if ((cefModifiers & (uint)cef_event_flags_t.EVENTFLAG_ALT_DOWN) != 0) modifiers |= InputModifiers.Alt;
        if ((cefModifiers & (uint)cef_event_flags_t.EVENTFLAG_COMMAND_DOWN) != 0) modifiers |= InputModifiers.Meta;
        if ((cefModifiers & (uint)cef_event_flags_t.EVENTFLAG_IS_KEY_PAD) != 0) modifiers |= InputModifiers.IsKeypad;
        var right = (cefModifiers & (uint)cef_event_flags_t.EVENTFLAG_IS_RIGHT) != 0;
        var left = (cefModifiers & (uint)cef_event_flags_t.EVENTFLAG_IS_LEFT) != 0;
        var repeat = (cefModifiers & (uint)cef_event_flags_t.EVENTFLAG_IS_REPEAT) != 0;
        if (repeat) modifiers |= InputModifiers.IsAutoRepeat;
        var keypad = modifiers.HasFlag(InputModifiers.IsKeypad);
        var shift = modifiers.HasFlag(InputModifiers.Shift);

        string key, code;
        if (vk is >= 'A' and <= 'Z')
        {
            key = ((char)(shift ? vk : vk + 32)).ToString();
            code = $"Key{(char)vk}";
        }
        else if (vk is >= '0' and <= '9')
        {
            key = character >= ' ' ? character.ToString() : ((char)vk).ToString();
            code = $"Digit{(char)vk}";
        }
        else if (vk is >= 96 and <= 105)
        {
            key = ((char)('0' + vk - 96)).ToString();
            code = $"Numpad{vk - 96}";
        }
        else if (s_keypadOperators.TryGetValue(vk, out var op))
        {
            key = op switch { "Multiply" => "*", "Add" => "+", "Subtract" => "-", "Decimal" => ".", _ => "/" };
            code = $"Numpad{op}";
        }
        else if (vk is >= 112 and <= 135)
        {
            key = code = $"F{vk - 111}";
        }
        else if (s_named.TryGetValue(vk, out var named))
        {
            (key, code) = named;
            if (vk == 13 && keypad) code = "NumpadEnter";
            // CEF reports the generic modifier key with a side flag.
            if (right && vk is 16 or 17 or 18 or 91) code = code.Replace("Left", "Right");
        }
        else
        {
            code = s_punctuation.GetValueOrDefault(vk, "");
            key = character >= ' ' ? character.ToString() : unmodified >= ' ' ? unmodified.ToString() : "Unidentified";
        }

        var location = keypad ? 3
            : right ? 2
            : left ? 1
            : vk is 160 or 162 or 164 or 16 or 17 or 18 or 91 ? 1
            : vk is 161 or 163 or 165 or 92 ? 2
            : 0;
        return new KeyInput(eventType, key, code, repeat, false, location, modifiers);
    }
}
