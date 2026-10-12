using System.ComponentModel;
using Oriel.Cef;
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

    /// <summary>The page with keyboard focus in the focused window, a window's own page or a view's, or null. Electron's
    /// <c>webContents.getFocusedWebContents()</c>.</summary>
    public static WebContents? GetFocusedWebContents() => CefThread.InvokeOnUi(()
        => BaseWindow.GetFocusedWindow()?.HostedPages().FirstOrDefault(page => page.ViewHasFocus()));

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

    // Electron's page forwards to the app from a listener added before any of the app's, so the app hears first.
    private void OnRenderProcessTerminated(cef_termination_status_t status, int exitCode)
    {
        var details = new RenderProcessGoneEventArgs(RenderProcessGoneReason.Of(status), exitCode);
        OrielApp.CurrentOrNull?.RaiseRenderProcessGone(this, details);
        RenderProcessGone?.Invoke(this, details);
    }

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
        if (BeforeInputEvent is { } handler
            && KeyInputMapper.Map(key->type, key->modifiers, key->windows_key_code, key->character, key->unmodified_character) is { } input)
        {
            var args = new BeforeInputEventArgs(input);
            handler(this, args);
            // Chromium forwards nothing for a key its delegate handled, so input-event never sees it.
            if (args.Cancel) return true;
        }
        if (_inputEvent is { } observer)
        {
            var keyboard = KeyInputMapper.Keyboard(key->type, key->modifiers, key->windows_key_code, key->character, key->unmodified_character);
            try { observer(this, keyboard); }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] A input-event handler failed: {e}"); }
        }
        return false;
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
        var e = Keyboard(type, cefModifiers, vk, character, unmodified);
        return new KeyInput(eventType, e.Key, e.Code, e.IsAutoRepeat, false, e.Location, e.Modifiers);
    }

    /// <summary>Electron's modifier flags from CEF's, which carry the same Blink bits.</summary>
    internal static InputModifiers ModifiersOf(uint cef)
    {
        var modifiers = InputModifiers.None;
        foreach (var (flag, mapped) in s_flags)
            if ((cef & (uint)flag) != 0) modifiers |= mapped;
        return modifiers;
    }

    private static readonly (cef_event_flags_t, InputModifiers)[] s_flags =
    [
        (cef_event_flags_t.EVENTFLAG_SHIFT_DOWN, InputModifiers.Shift), (cef_event_flags_t.EVENTFLAG_CONTROL_DOWN, InputModifiers.Control),
        (cef_event_flags_t.EVENTFLAG_ALT_DOWN, InputModifiers.Alt), (cef_event_flags_t.EVENTFLAG_COMMAND_DOWN, InputModifiers.Meta),
        (cef_event_flags_t.EVENTFLAG_IS_KEY_PAD, InputModifiers.IsKeypad), (cef_event_flags_t.EVENTFLAG_IS_REPEAT, InputModifiers.IsAutoRepeat),
        (cef_event_flags_t.EVENTFLAG_CAPS_LOCK_ON, InputModifiers.CapsLock), (cef_event_flags_t.EVENTFLAG_NUM_LOCK_ON, InputModifiers.NumLock),
        (cef_event_flags_t.EVENTFLAG_IS_LEFT, InputModifiers.Left), (cef_event_flags_t.EVENTFLAG_IS_RIGHT, InputModifiers.Right),
        (cef_event_flags_t.EVENTFLAG_LEFT_MOUSE_BUTTON, InputModifiers.LeftButtonDown),
        (cef_event_flags_t.EVENTFLAG_MIDDLE_MOUSE_BUTTON, InputModifiers.MiddleButtonDown),
        (cef_event_flags_t.EVENTFLAG_RIGHT_MOUSE_BUTTON, InputModifiers.RightButtonDown),
    ];

    /// <summary>Electron's keyboard <c>InputEvent</c> for any of CEF's four key event types.</summary>
    internal static WebKeyboardEvent Keyboard(cef_key_event_type_t type, uint cefModifiers, int vk, char character, char unmodified)
    {
        var eventType = type switch
        {
            cef_key_event_type_t.KEYEVENT_RAWKEYDOWN => InputEventType.RawKeyDown,
            cef_key_event_type_t.KEYEVENT_KEYDOWN => InputEventType.KeyDown,
            cef_key_event_type_t.KEYEVENT_KEYUP => InputEventType.KeyUp,
            _ => InputEventType.Char,
        };
        var modifiers = ModifiersOf(cefModifiers);
        var right = modifiers.HasFlag(InputModifiers.Right);
        var left = modifiers.HasFlag(InputModifiers.Left);
        var keypad = modifiers.HasFlag(InputModifiers.IsKeypad);
        var shift = modifiers.HasFlag(InputModifiers.Shift);

        string key, code;
        // A char event's key code is its character, not the key's.
        if (eventType == InputEventType.Char)
        {
            (key, code) = CharKeyAndCode(character, unmodified, keypad);
        }
        else if (vk is >= 'A' and <= 'Z')
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
            : eventType == InputEventType.Char ? 0
            : vk is 160 or 162 or 164 or 16 or 17 or 18 or 91 ? 1
            : vk is 161 or 163 or 165 or 92 ? 2
            : 0;
        return new WebKeyboardEvent(eventType, key, code, location) { Modifiers = modifiers };
    }

    private static readonly Dictionary<char, string> s_controlKeys = new()
    {
        ['\r'] = "Enter", ['\t'] = "Tab", ['\b'] = "Backspace", ['\u001b'] = "Escape",
    };

    // US layout, unshifted and shifted, as the virtual-key mapping above assumes.
    private static readonly Dictionary<char, string> s_characterCodes = new()
    {
        [' '] = "Space", [';'] = "Semicolon", [':'] = "Semicolon", ['='] = "Equal", ['+'] = "Equal", [','] = "Comma", ['<'] = "Comma",
        ['-'] = "Minus", ['_'] = "Minus", ['.'] = "Period", ['>'] = "Period", ['/'] = "Slash", ['?'] = "Slash", ['`'] = "Backquote",
        ['~'] = "Backquote", ['['] = "BracketLeft", ['{'] = "BracketLeft", ['\\'] = "Backslash", ['|'] = "Backslash",
        [']'] = "BracketRight", ['}'] = "BracketRight", ['\''] = "Quote", ['"'] = "Quote", ['!'] = "Digit1", ['@'] = "Digit2",
        ['#'] = "Digit3", ['$'] = "Digit4", ['%'] = "Digit5", ['^'] = "Digit6", ['&'] = "Digit7", ['*'] = "Digit8", ['('] = "Digit9",
        [')'] = "Digit0", ['\r'] = "Enter", ['\t'] = "Tab", ['\b'] = "Backspace", ['\u001b'] = "Escape",
    };

    private static readonly Dictionary<char, string> s_keypadCharacters = new()
    {
        ['*'] = "NumpadMultiply", ['+'] = "NumpadAdd", ['-'] = "NumpadSubtract", ['.'] = "NumpadDecimal", ['/'] = "NumpadDivide", ['\r'] = "NumpadEnter",
    };

    /// <summary>A typed character's DOM key, and the US-layout code of the key that types it.</summary>
    internal static (string Key, string Code) CharKeyAndCode(char character, char unmodified, bool keypad)
    {
        var key = character >= ' ' ? character.ToString()
            : s_controlKeys.TryGetValue(character, out var control) ? control
            : unmodified >= ' ' ? unmodified.ToString()
            : "Unidentified";
        // Control combinations type control characters; the unmodified one names the key.
        var source = unmodified >= ' ' || s_controlKeys.ContainsKey(unmodified) ? unmodified : character;
        var code = keypad && char.IsAsciiDigit(source) ? $"Numpad{source}"
            : keypad && s_keypadCharacters.TryGetValue(source, out var pad) ? pad
            : char.IsAsciiLetter(source) ? $"Key{char.ToUpperInvariant(source)}"
            : char.IsAsciiDigit(source) ? $"Digit{source}"
            : s_characterCodes.GetValueOrDefault(source, "");
        return (key, code);
    }

    /// <summary>The char event <see cref="WebContents.SendInputEvent(KeyboardInputEvent)"/> sends, as Electron's
    /// converter builds it from the key name: a letter's case follows Shift, and a shifted symbol adds Shift.</summary>
    internal static WebKeyboardEvent SentChar(KeyboardInputEvent sent)
    {
        var modifiers = sent.Modifiers;
        if ("~!@#$%^&*()_+{}|:\"<>?".Contains(sent.KeyCode[0], StringComparison.Ordinal)) modifiers |= InputModifiers.Shift;
        var (key, code) = CharKeyAndCode(sent.KeyCode[0], sent.KeyCode[0], modifiers.HasFlag(InputModifiers.IsKeypad));
        if (char.IsAsciiLetter(key[0])) key = modifiers.HasFlag(InputModifiers.Shift) ? key.ToUpperInvariant() : key.ToLowerInvariant();
        return new WebKeyboardEvent(InputEventType.Char, key, code, modifiers.HasFlag(InputModifiers.IsKeypad) ? 3 : 0) { Modifiers = modifiers };
    }
}
