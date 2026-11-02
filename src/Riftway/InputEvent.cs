using Riftway.Cef.Interop;

namespace Riftway;

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

    /// <summary>Caps Lock is on; Electron's <c>capslock</c>.</summary>
    CapsLock = 1 << 6,

    /// <summary>Num Lock is on; Electron's <c>numlock</c>.</summary>
    NumLock = 1 << 7,

    /// <summary>The key is the left one of a pair, such as left Shift; Electron's <c>left</c>.</summary>
    Left = 1 << 8,

    /// <summary>The key is the right one of a pair; Electron's <c>right</c>.</summary>
    Right = 1 << 9,

    /// <summary>The primary mouse button is held; Electron's <c>leftbuttondown</c>.</summary>
    LeftButtonDown = 1 << 10,

    /// <summary>The middle mouse button is held; Electron's <c>middlebuttondown</c>.</summary>
    MiddleButtonDown = 1 << 11,

    /// <summary>The secondary mouse button is held; Electron's <c>rightbuttondown</c>.</summary>
    RightButtonDown = 1 << 12,
}

/// <summary>The kind of a <see cref="WebInputEvent"/>; Electron's <c>InputEvent.type</c>, whose name is this one's in
/// lower camel case (<see cref="InputEventTypes.ElectronName"/>).</summary>
public enum InputEventType
{
    /// <summary>Electron's <c>undefined</c>.</summary>
    Undefined,

    /// <summary>A mouse button went down.</summary>
    MouseDown,

    /// <summary>A mouse button went up.</summary>
    MouseUp,

    /// <summary>The pointer moved, or entered the page.</summary>
    MouseMove,

    /// <summary>The pointer entered the page (only ever sent by <see cref="WebContents.SendInputEvent(MouseInputEvent)"/> in Electron).</summary>
    MouseEnter,

    /// <summary>The pointer left the page.</summary>
    MouseLeave,

    /// <summary>A context-menu request.</summary>
    ContextMenu,

    /// <summary>The wheel turned.</summary>
    MouseWheel,

    /// <summary>A key went down, before any text it types.</summary>
    RawKeyDown,

    /// <summary>A key went down and types text.</summary>
    KeyDown,

    /// <summary>A key went up.</summary>
    KeyUp,

    /// <summary>A character was typed.</summary>
    Char,

#pragma warning disable CS1591 // Chromium's gesture, touch and pointer kinds; named as Electron names them.
    GestureBegin,
    GestureEnd,
    GestureScrollBegin,
    GestureScrollEnd,
    GestureScrollUpdate,
    GestureFlingStart,
    GestureFlingCancel,
    GesturePinchBegin,
    GesturePinchEnd,
    GesturePinchUpdate,
    GestureTapDown,
    GestureShowPress,
    GestureTap,
    GestureTapCancel,
    GestureShortPress,
    GestureLongPress,
    GestureLongTap,
    GestureTwoFingerTap,
    GestureTapUnconfirmed,
    GestureDoubleTap,
    TouchStart,
    TouchMove,
    TouchEnd,
    TouchCancel,
    TouchScrollStarted,
    PointerDown,
    PointerUp,
    PointerMove,
    PointerRawUpdate,
    PointerCancel,
    PointerCausedUaAction,
#pragma warning restore CS1591
}

/// <summary>Electron's names for <see cref="InputEventType"/> and <see cref="InputModifiers"/>.</summary>
public static class InputEventTypes
{
    /// <summary>Electron's <c>type</c> string: <c>rawKeyDown</c>, <c>mouseWheel</c>, <c>gestureScrollBegin</c>, ...</summary>
    public static string ElectronName(this InputEventType type)
    {
        var name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    // Electron's table, in its (alphabetical) order; aliases such as ctrl and cmd are accepted there but never returned.
    private static readonly (string Name, InputModifiers Flag)[] s_modifierNames =
    [
        ("alt", InputModifiers.Alt), ("capslock", InputModifiers.CapsLock), ("control", InputModifiers.Control),
        ("isautorepeat", InputModifiers.IsAutoRepeat), ("iskeypad", InputModifiers.IsKeypad), ("left", InputModifiers.Left),
        ("leftbuttondown", InputModifiers.LeftButtonDown), ("meta", InputModifiers.Meta),
        ("middlebuttondown", InputModifiers.MiddleButtonDown), ("numlock", InputModifiers.NumLock), ("right", InputModifiers.Right),
        ("rightbuttondown", InputModifiers.RightButtonDown), ("shift", InputModifiers.Shift),
    ];

    /// <summary>Electron's <c>modifiers</c> array for <paramref name="modifiers"/>, in Electron's order.</summary>
    public static IReadOnlyList<string> ElectronNames(this InputModifiers modifiers)
        => [.. s_modifierNames.Where(m => modifiers.HasFlag(m.Flag)).Select(m => m.Name)];

    /// <summary>The flags for Electron's modifier names; unknown names are ignored.</summary>
    internal static InputModifiers FromElectronNames(IEnumerable<string> names)
    {
        var modifiers = InputModifiers.None;
        foreach (var name in names)
            foreach (var (known, flag) in s_modifierNames)
                if (known == name) modifiers |= flag;
        return modifiers;
    }
}

/// <summary>An input event the page was sent; Electron's <c>InputEvent</c>, as <c>input-event</c> reports it. Keyboard
/// and mouse events are the derived <see cref="WebKeyboardEvent"/> and <see cref="WebMouseEvent"/>; any other kind
/// (<see cref="InputEventType.MouseWheel"/>) carries only its type and modifiers, as in Electron.</summary>
/// <param name="Type">The kind of event.</param>
public record WebInputEvent(InputEventType Type)
{
    /// <summary>The modifiers and flags held. Electron's <c>modifiers</c>; always none for a <see cref="WebMouseEvent"/>,
    /// which Electron reports without them.</summary>
    public InputModifiers Modifiers { get; init; }
}

/// <summary>A key event the page was sent: <see cref="InputEventType.RawKeyDown"/>, <see cref="InputEventType.KeyDown"/>,
/// <see cref="InputEventType.KeyUp"/> or <see cref="InputEventType.Char"/>. Electron's keyboard <c>InputEvent</c>.</summary>
/// <param name="Type">The kind of event.</param>
/// <param name="Key">The DOM <c>KeyboardEvent.key</c> value (drift C16).</param>
/// <param name="Code">The DOM <c>KeyboardEvent.code</c> value (drift C16).</param>
/// <param name="Location">0 standard, 1 left, 2 right, 3 numeric keypad; the DOM <c>location</c>.</param>
public sealed record WebKeyboardEvent(InputEventType Type, string Key, string Code, int Location) : WebInputEvent(Type)
{
    /// <summary>The key is held and repeating.</summary>
    public bool IsAutoRepeat => Modifiers.HasFlag(InputModifiers.IsAutoRepeat);

    /// <summary>Always false: CEF does not say (drift C16).</summary>
    public bool IsComposing => false;

    /// <summary>Shift is held.</summary>
    public bool Shift => Modifiers.HasFlag(InputModifiers.Shift);

    /// <summary>Control is held.</summary>
    public bool Control => Modifiers.HasFlag(InputModifiers.Control);

    /// <summary>Alt is held.</summary>
    public bool Alt => Modifiers.HasFlag(InputModifiers.Alt);

    /// <summary>Meta (Command on macOS) is held.</summary>
    public bool Meta => Modifiers.HasFlag(InputModifiers.Meta);
}

/// <summary>A mouse event the page was sent; Electron's mouse <c>InputEvent</c>.</summary>
/// <param name="Type">The kind of event.</param>
/// <param name="X">Horizontal position in the page's view, in DIPs.</param>
/// <param name="Y">Vertical position in the page's view, in DIPs.</param>
public sealed record WebMouseEvent(InputEventType Type, double X, double Y) : WebInputEvent(Type)
{
    /// <summary>Horizontal position on the screen, in DIPs. Electron's <c>globalX</c>.</summary>
    public double GlobalX { get; init; }

    /// <summary>Vertical position on the screen, in DIPs. Electron's <c>globalY</c>.</summary>
    public double GlobalY { get; init; }

    /// <summary>Horizontal movement since the last mouse event. Electron's <c>movementX</c>.</summary>
    public int MovementX { get; init; }

    /// <summary>Vertical movement since the last mouse event. Electron's <c>movementY</c>.</summary>
    public int MovementY { get; init; }

    /// <summary>The button pressed or released; for a move, the button held, or <see cref="MouseButton.None"/>.
    /// Electron's <c>button</c>.</summary>
    public MouseButton Button { get; init; }

    /// <summary>How many clicks in a row a press or release is; 0 for a move. Electron's <c>clickCount</c>.</summary>
    public int ClickCount { get; init; }
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

    /// <summary>The pointer entered the page; Electron's <c>mouseEnter</c>.</summary>
    MouseEnter,

    /// <summary>The pointer left the page; Electron's <c>mouseLeave</c>.</summary>
    MouseLeave,

    /// <summary>A context-menu request (a right click); Electron's <c>contextMenu</c>.</summary>
    ContextMenu,

    /// <summary>The wheel turned, by <see cref="MouseInputEvent.DeltaX"/> and <see cref="MouseInputEvent.DeltaY"/>;
    /// Electron's <c>mouseWheel</c>.</summary>
    MouseWheel,
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

    /// <summary>No button: a move with none held. Reported by <see cref="WebContents.InputEvent"/> only; sent as
    /// <see cref="Left"/>, as Electron does.</summary>
    None,

    /// <summary>The back button. Reported only; sent as <see cref="Left"/>.</summary>
    Back,

    /// <summary>The forward button. Reported only; sent as <see cref="Left"/>.</summary>
    Forward,

    /// <summary>A pen's eraser. Reported only; sent as <see cref="Left"/>.</summary>
    Eraser,
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

    /// <summary>For <see cref="MouseInputEventType.MouseWheel"/>: pixels to scroll sideways; positive scrolls left, as
    /// Electron's (Chromium's) <c>deltaX</c>.</summary>
    public int DeltaX { get; init; }

    /// <summary>For <see cref="MouseInputEventType.MouseWheel"/>: pixels to scroll; positive scrolls up, as Electron's
    /// (Chromium's) <c>deltaY</c>.</summary>
    public int DeltaY { get; init; }
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
    internal static uint CefModifiers(InputModifiers modifiers)
        => (modifiers.HasFlag(InputModifiers.Shift) ? (uint)cef_event_flags_t.EVENTFLAG_SHIFT_DOWN : 0)
            | (modifiers.HasFlag(InputModifiers.Control) ? (uint)cef_event_flags_t.EVENTFLAG_CONTROL_DOWN : 0)
            | (modifiers.HasFlag(InputModifiers.Alt) ? (uint)cef_event_flags_t.EVENTFLAG_ALT_DOWN : 0)
            | (modifiers.HasFlag(InputModifiers.Meta) ? (uint)cef_event_flags_t.EVENTFLAG_COMMAND_DOWN : 0);

    internal static int DevToolsModifiers(InputModifiers modifiers)
        => (modifiers.HasFlag(InputModifiers.Alt) ? 1 : 0) | (modifiers.HasFlag(InputModifiers.Control) ? 2 : 0)
            | (modifiers.HasFlag(InputModifiers.Meta) ? 4 : 0) | (modifiers.HasFlag(InputModifiers.Shift) ? 8 : 0);
}
