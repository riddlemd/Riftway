using System.Drawing;
using System.Text.Json;
using Riftway.Cef.Interop;

namespace Riftway.Tests;

public sealed class WebContentsInputTests
{
    [Theory]
    [InlineData(InputEventType.Undefined, "undefined")]
    [InlineData(InputEventType.RawKeyDown, "rawKeyDown")]
    [InlineData(InputEventType.Char, "char")]
    [InlineData(InputEventType.MouseWheel, "mouseWheel")]
    [InlineData(InputEventType.ContextMenu, "contextMenu")]
    [InlineData(InputEventType.GestureScrollBegin, "gestureScrollBegin")]
    [InlineData(InputEventType.PointerCausedUaAction, "pointerCausedUaAction")]
    public void ElectronName_IsLowerCamelCase(InputEventType type, string name) => Assert.Equal(name, type.ElectronName());

    [Fact]
    public void ElectronNames_FollowElectronsOrder()
        => Assert.Equal(["alt", "capslock", "control", "isautorepeat", "iskeypad", "left", "leftbuttondown", "meta", "middlebuttondown", "numlock", "right", "rightbuttondown", "shift"],
            (InputModifiers.Shift | InputModifiers.Control | InputModifiers.Alt | InputModifiers.Meta | InputModifiers.IsKeypad | InputModifiers.IsAutoRepeat
                | InputModifiers.CapsLock | InputModifiers.NumLock | InputModifiers.Left | InputModifiers.Right | InputModifiers.LeftButtonDown
                | InputModifiers.MiddleButtonDown | InputModifiers.RightButtonDown).ElectronNames());

    [Fact]
    public void ElectronNames_OnlyTheFlagsSet() => Assert.Equal(["control", "shift"], (InputModifiers.Shift | InputModifiers.Control).ElectronNames());

    [Fact]
    public void FromElectronNames_IgnoresUnknownNames()
        => Assert.Equal(InputModifiers.Meta | InputModifiers.MiddleButtonDown, InputEventTypes.FromElectronNames(["meta", "middlebuttondown", "cmd"]));

    [Theory]
    [InlineData(cef_event_flags_t.EVENTFLAG_SHIFT_DOWN, InputModifiers.Shift)]
    [InlineData(cef_event_flags_t.EVENTFLAG_CONTROL_DOWN, InputModifiers.Control)]
    [InlineData(cef_event_flags_t.EVENTFLAG_ALT_DOWN, InputModifiers.Alt)]
    [InlineData(cef_event_flags_t.EVENTFLAG_COMMAND_DOWN, InputModifiers.Meta)]
    [InlineData(cef_event_flags_t.EVENTFLAG_IS_KEY_PAD, InputModifiers.IsKeypad)]
    [InlineData(cef_event_flags_t.EVENTFLAG_IS_REPEAT, InputModifiers.IsAutoRepeat)]
    [InlineData(cef_event_flags_t.EVENTFLAG_CAPS_LOCK_ON, InputModifiers.CapsLock)]
    [InlineData(cef_event_flags_t.EVENTFLAG_NUM_LOCK_ON, InputModifiers.NumLock)]
    [InlineData(cef_event_flags_t.EVENTFLAG_IS_LEFT, InputModifiers.Left)]
    [InlineData(cef_event_flags_t.EVENTFLAG_IS_RIGHT, InputModifiers.Right)]
    [InlineData(cef_event_flags_t.EVENTFLAG_LEFT_MOUSE_BUTTON, InputModifiers.LeftButtonDown)]
    [InlineData(cef_event_flags_t.EVENTFLAG_MIDDLE_MOUSE_BUTTON, InputModifiers.MiddleButtonDown)]
    [InlineData(cef_event_flags_t.EVENTFLAG_RIGHT_MOUSE_BUTTON, InputModifiers.RightButtonDown)]
    [InlineData(cef_event_flags_t.EVENTFLAG_ALTGR_DOWN, InputModifiers.None)]
    public void ModifiersOf_MapsEachCefFlag(cef_event_flags_t flag, InputModifiers modifiers)
        => Assert.Equal(modifiers, KeyInputMapper.ModifiersOf((uint)flag));

    [Theory]
    [InlineData(cef_key_event_type_t.KEYEVENT_RAWKEYDOWN, InputEventType.RawKeyDown)]
    [InlineData(cef_key_event_type_t.KEYEVENT_KEYDOWN, InputEventType.KeyDown)]
    [InlineData(cef_key_event_type_t.KEYEVENT_KEYUP, InputEventType.KeyUp)]
    [InlineData(cef_key_event_type_t.KEYEVENT_CHAR, InputEventType.Char)]
    public void Keyboard_ReportsEveryKeyType(cef_key_event_type_t type, InputEventType expected)
        => Assert.Equal(expected, KeyInputMapper.Keyboard(type, 0, 'A', 'a', 'a').Type);

    [Fact]
    public void Keyboard_CarriesKeyCodeLocationAndModifiers()
    {
        var e = KeyInputMapper.Keyboard(cef_key_event_type_t.KEYEVENT_RAWKEYDOWN,
            (uint)(cef_event_flags_t.EVENTFLAG_SHIFT_DOWN | cef_event_flags_t.EVENTFLAG_IS_RIGHT | cef_event_flags_t.EVENTFLAG_CAPS_LOCK_ON), 16, '\0', '\0');
        Assert.Equal(("Shift", "ShiftRight", 2), (e.Key, e.Code, e.Location));
        Assert.Equal(InputModifiers.Shift | InputModifiers.Right | InputModifiers.CapsLock, e.Modifiers);
        Assert.True(e is { Shift: true, Control: false, Alt: false, Meta: false, IsAutoRepeat: false, IsComposing: false });
    }

    // A char event's key code is the character: 'a' is 97, which as a key code would be Numpad1.
    [Theory]
    [InlineData('a', 'a', 0u, "a", "KeyA", 0)]
    [InlineData('A', 'a', (uint)cef_event_flags_t.EVENTFLAG_SHIFT_DOWN, "A", "KeyA", 0)]
    [InlineData('1', '1', 0u, "1", "Digit1", 0)]
    [InlineData('!', '1', (uint)cef_event_flags_t.EVENTFLAG_SHIFT_DOWN, "!", "Digit1", 0)]
    [InlineData('!', '!', (uint)cef_event_flags_t.EVENTFLAG_SHIFT_DOWN, "!", "Digit1", 0)]
    [InlineData('\u0001', 'a', (uint)cef_event_flags_t.EVENTFLAG_CONTROL_DOWN, "a", "KeyA", 0)]
    [InlineData('\r', '\r', 0u, "Enter", "Enter", 0)]
    [InlineData('\r', '\r', (uint)cef_event_flags_t.EVENTFLAG_IS_KEY_PAD, "Enter", "NumpadEnter", 3)]
    [InlineData('5', '5', (uint)cef_event_flags_t.EVENTFLAG_IS_KEY_PAD, "5", "Numpad5", 3)]
    [InlineData('+', '+', (uint)cef_event_flags_t.EVENTFLAG_IS_KEY_PAD, "+", "NumpadAdd", 3)]
    [InlineData('+', '=', (uint)cef_event_flags_t.EVENTFLAG_SHIFT_DOWN, "+", "Equal", 0)]
    [InlineData(' ', ' ', 0u, " ", "Space", 0)]
    [InlineData('é', 'é', 0u, "é", "", 0)]
    [InlineData('\u0000', '\u0000', 0u, "Unidentified", "", 0)]
    public void Keyboard_CharUsesTheCharacter(char character, char unmodified, uint flags, string key, string code, int location)
    {
        var e = KeyInputMapper.Keyboard(cef_key_event_type_t.KEYEVENT_CHAR, flags, character, character, unmodified);
        Assert.Equal((key, code, location), (e.Key, e.Code, e.Location));
    }

    [Fact]
    public void Keyboard_CharIgnoresTheVirtualKeyLocation()
        => Assert.Equal(0, KeyInputMapper.Keyboard(cef_key_event_type_t.KEYEVENT_CHAR, 0, 91, '[', '[').Location);

    [Theory]
    [InlineData("a", InputModifiers.None, "a", "KeyA", 0)]
    [InlineData("a", InputModifiers.Shift, "A", "KeyA", 0)]
    [InlineData("A", InputModifiers.None, "a", "KeyA", 0)]
    [InlineData("7", InputModifiers.IsKeypad, "7", "Numpad7", 3)]
    [InlineData("-", InputModifiers.None, "-", "Minus", 0)]
    public void SentChar_FollowsElectronsConverter(string sent, InputModifiers modifiers, string key, string code, int location)
    {
        var e = KeyInputMapper.SentChar(new KeyboardInputEvent(KeyboardInputEventType.Char, sent) { Modifiers = modifiers });
        Assert.Equal((InputEventType.Char, key, code, location, modifiers), (e.Type, e.Key, e.Code, e.Location, e.Modifiers));
    }

    [Fact]
    public void SentChar_AShiftedSymbolAddsShift()
    {
        var e = KeyInputMapper.SentChar(new KeyboardInputEvent(KeyboardInputEventType.Char, "!") { Modifiers = InputModifiers.Control });
        Assert.Equal(("!", "Digit1", InputModifiers.Control | InputModifiers.Shift), (e.Key, e.Code, e.Modifiers));
    }

    [Fact]
    public void Map_StillSkipsCharactersForBeforeInputEvent()
        => Assert.Null(KeyInputMapper.Map(cef_key_event_type_t.KEYEVENT_CHAR, 0, 'A', 'a', 'a'));

    private static JsonElement[] Args(string json) => [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone())];

    [Fact]
    public void PagePointer_MouseFields()
    {
        var e = Assert.IsType<WebMouseEvent>(PagePointerEvent.Parse(Args("""["mouseDown", 10, 20, 110, 220, 3, -4, "right", 2]"""), 1));
        Assert.Equal(new WebMouseEvent(InputEventType.MouseDown, 10, 20)
        {
            GlobalX = 110, GlobalY = 220, MovementX = 3, MovementY = -4, Button = MouseButton.Right, ClickCount = 2,
        }, e);
    }

    [Fact]
    public void PagePointer_PositionsScaleWithZoomButScreenOnesDoNot()
    {
        var e = Assert.IsType<WebMouseEvent>(PagePointerEvent.Parse(Args("""["mouseMove", 10, 20, 110, 220, 0, 0, "none", 0]"""), 1.5));
        Assert.Equal((15d, 30d, 110d, 220d, MouseButton.None), (e.X, e.Y, e.GlobalX, e.GlobalY, e.Button));
    }

    [Theory]
    [InlineData("mouseUp", InputEventType.MouseUp)]
    [InlineData("mouseLeave", InputEventType.MouseLeave)]
    [InlineData("mouseEnter", InputEventType.MouseEnter)]
    [InlineData("contextMenu", InputEventType.ContextMenu)]
    public void PagePointer_MouseTypesAreMouseEvents(string name, InputEventType type)
        => Assert.Equal(type, Assert.IsType<WebMouseEvent>(PagePointerEvent.Parse(Args($$"""["{{name}}", 0, 0, 0, 0, 0, 0, "left", 0]"""), 1)).Type);

    [Fact]
    public void PagePointer_WheelIsAPlainInputEventWithModifiers()
    {
        var e = PagePointerEvent.Parse(Args("""["mouseWheel", ["control", "leftbuttondown", "shift"]]"""), 2);
        Assert.Equal(typeof(WebInputEvent), e.GetType());
        Assert.Equal(new WebInputEvent(InputEventType.MouseWheel) { Modifiers = InputModifiers.Control | InputModifiers.LeftButtonDown | InputModifiers.Shift }, e);
    }

    [Theory]
    [InlineData(300, 150, 1, 1, 300, 150)]
    [InlineData(300, 150, 1.5, 1.5, 450, 225)]
    [InlineData(300, 150, 2, 1, 300, 150)]
    [InlineData(100.4, 50.6, 1, 1, 100, 51)]
    [InlineData(100.25, 50.75, 2, 1, 100, 51)]
    [InlineData(100.2, 0, 3, 1.5, 150, 0)]
    public void PreferredSize_RoundsToDevicePixelsThenFloorsToDips(double width, double height, double ratio, double zoom, int dipWidth, int dipHeight)
        => Assert.Equal(new Size(dipWidth, dipHeight), PreferredSizeMath.ToDips(width, height, ratio, zoom));

    [Fact]
    public void PreferredSize_ToleratesTheRatiosFloatError()
        => Assert.Equal(new Size(240, 120), PreferredSizeMath.ToDips(200, 100, 1.2 * 1.75, 1.2));
}
