namespace Oriel;

/// <summary>The kind of a <see cref="KeyboardInputEvent"/>; Electron's <c>type</c> values for keyboard input.</summary>
public enum KeyboardInputEventType
{
    /// <summary>A key went down; Electron's <c>keyDown</c>.</summary>
    KeyDown,

    /// <summary>A key went up; Electron's <c>keyUp</c>.</summary>
    KeyUp,

    /// <summary>A character was typed; Electron's <c>char</c>.</summary>
    Char,
}

/// <summary>Modifier keys and flags; Electron's <c>InputEvent.modifiers</c>.</summary>
[Flags]
public enum InputModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1 << 0,

    /// <summary>Control (Electron's <c>control</c> / <c>ctrl</c>).</summary>
    Control = 1 << 1,

    /// <summary>Alt / Option.</summary>
    Alt = 1 << 2,

    /// <summary>Meta: Command on macOS, Windows key elsewhere (Electron's <c>meta</c> / <c>command</c> / <c>cmd</c>).</summary>
    Meta = 1 << 3,

    /// <summary>The key is on the numeric keypad.</summary>
    IsKeypad = 1 << 4,

    /// <summary>The event repeats a held key.</summary>
    IsAutoRepeat = 1 << 5,
}

/// <summary>A key event for <see cref="WebContents.SendInputEvent(KeyboardInputEvent)"/>; Electron's <c>KeyboardInputEvent</c>.</summary>
/// <param name="Type">Down, up or a typed character.</param>
/// <param name="KeyCode">An Electron accelerator key name: <c>A</c>–<c>Z</c>, <c>0</c>–<c>9</c>, <c>F1</c>–<c>F24</c>,
/// <c>Space</c>, <c>Enter</c>, <c>Tab</c>, <c>Backspace</c>, <c>Delete</c>, <c>Escape</c>, <c>Up</c>, <c>Down</c>,
/// <c>Left</c>, <c>Right</c>, <c>Home</c>, <c>End</c>, <c>PageUp</c>, <c>PageDown</c>, <c>Insert</c>, and the modifier keys
/// <c>Shift</c>, <c>Control</c>, <c>Alt</c>, <c>Meta</c>; for
/// <see cref="KeyboardInputEventType.Char"/>, any single character.</param>
public sealed record KeyboardInputEvent(KeyboardInputEventType Type, string KeyCode)
{
    /// <summary>Modifier keys held during the event.</summary>
    public InputModifiers Modifiers { get; init; }
}

/// <summary>The kind of a <see cref="MouseInputEvent"/>; Electron's <c>type</c> values for mouse input.</summary>
public enum MouseInputEventType
{
    /// <summary>A button went down; Electron's <c>mouseDown</c>.</summary>
    MouseDown,

    /// <summary>A button went up; Electron's <c>mouseUp</c>.</summary>
    MouseUp,

    /// <summary>The pointer moved; Electron's <c>mouseMove</c>.</summary>
    MouseMove,
}

/// <summary>A mouse button; Electron's <c>button</c> values.</summary>
public enum MouseButton
{
    /// <summary>The primary button.</summary>
    Left,

    /// <summary>The middle button or wheel.</summary>
    Middle,

    /// <summary>The secondary button, which opens context menus.</summary>
    Right,
}

/// <summary>A mouse event for <see cref="WebContents.SendInputEvent(MouseInputEvent)"/>; Electron's <c>MouseInputEvent</c>.</summary>
/// <param name="Type">Down, up or move.</param>
/// <param name="X">Horizontal position in the page, in DIPs.</param>
/// <param name="Y">Vertical position in the page, in DIPs.</param>
public sealed record MouseInputEvent(MouseInputEventType Type, int X, int Y)
{
    /// <summary>The button pressed or released; ignored for moves.</summary>
    public MouseButton Button { get; init; } = MouseButton.Left;

    /// <summary>How many clicks in a row this is: 2 for a double click.</summary>
    public int ClickCount { get; init; } = 1;

    /// <summary>Modifier keys held during the event.</summary>
    public InputModifiers Modifiers { get; init; }
}

/// <summary>Maps Electron key names to the DevTools <c>Input.dispatchKeyEvent</c> fields.</summary>
internal static class KeyMap
{
    internal readonly record struct Key(string KeyValue, string Code, int VirtualKeyCode);

    private static readonly Dictionary<string, Key> s_named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = new(" ", "Space", 32),
        ["Enter"] = new("Enter", "Enter", 13),
        ["Return"] = new("Enter", "Enter", 13),
        ["Tab"] = new("Tab", "Tab", 9),
        ["Backspace"] = new("Backspace", "Backspace", 8),
        ["Delete"] = new("Delete", "Delete", 46),
        ["Escape"] = new("Escape", "Escape", 27),
        ["Esc"] = new("Escape", "Escape", 27),
        ["Up"] = new("ArrowUp", "ArrowUp", 38),
        ["Down"] = new("ArrowDown", "ArrowDown", 40),
        ["Left"] = new("ArrowLeft", "ArrowLeft", 37),
        ["Right"] = new("ArrowRight", "ArrowRight", 39),
        ["Home"] = new("Home", "Home", 36),
        ["End"] = new("End", "End", 35),
        ["PageUp"] = new("PageUp", "PageUp", 33),
        ["PageDown"] = new("PageDown", "PageDown", 34),
        ["Insert"] = new("Insert", "Insert", 45),
        ["Shift"] = new("Shift", "ShiftLeft", 16),
        ["Control"] = new("Control", "ControlLeft", 17),
        ["Alt"] = new("Alt", "AltLeft", 18),
        ["Meta"] = new("Meta", "MetaLeft", 91),
    };

    /// <exception cref="ArgumentException">Not a key name this map knows.</exception>
    internal static Key Resolve(string name, bool shift)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (s_named.TryGetValue(name, out var key)) return key;
        if (name.Length == 1 && char.IsAsciiLetter(name[0]))
        {
            var upper = char.ToUpperInvariant(name[0]);
            return new(shift ? upper.ToString() : char.ToLowerInvariant(upper).ToString(), $"Key{upper}", upper);
        }
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) return new(name, $"Digit{name}", name[0]);
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24)
            return new($"F{n}", $"F{n}", 111 + n);
        throw new ArgumentException($"'{name}' is not a supported key name.", nameof(name));
    }

    /// <summary>DevTools' modifier bits: Alt 1, Ctrl 2, Meta 4, Shift 8.</summary>
    internal static int DevToolsModifiers(InputModifiers modifiers)
        => (modifiers.HasFlag(InputModifiers.Alt) ? 1 : 0) | (modifiers.HasFlag(InputModifiers.Control) ? 2 : 0)
            | (modifiers.HasFlag(InputModifiers.Meta) ? 4 : 0) | (modifiers.HasFlag(InputModifiers.Shift) ? 8 : 0);
}
