using Riftway.Cef.Interop;

namespace Riftway.Tests;

public sealed class WebContentsLifecycleTests
{
    [Theory]
    [InlineData(cef_termination_status_t.TS_ABNORMAL_TERMINATION, "abnormal-exit")]
    [InlineData(cef_termination_status_t.TS_PROCESS_WAS_KILLED, "killed")]
    [InlineData(cef_termination_status_t.TS_PROCESS_CRASHED, "crashed")]
    [InlineData(cef_termination_status_t.TS_PROCESS_OOM, "oom")]
    [InlineData(cef_termination_status_t.TS_LAUNCH_FAILED, "launch-failed")]
    [InlineData(cef_termination_status_t.TS_INTEGRITY_FAILURE, "integrity-failure")]
    public void RenderProcessGoneReason_MapsEveryStatus(cef_termination_status_t status, string reason)
        => Assert.Equal(reason, RenderProcessGoneReason.Of(status));

    private const cef_key_event_type_t Down = cef_key_event_type_t.KEYEVENT_RAWKEYDOWN;
    private const uint Shift = (uint)cef_event_flags_t.EVENTFLAG_SHIFT_DOWN;

    [Fact]
    public void Map_SkipsTypedCharacters() => Assert.Null(KeyInputMapper.Map(cef_key_event_type_t.KEYEVENT_CHAR, 0, 'A', 'a', 'a'));

    [Fact]
    public void Map_ReportsKeyDownAndKeyUp()
    {
        Assert.Equal(KeyboardInputEventType.KeyDown, KeyInputMapper.Map(Down, 0, 'A', 'a', 'a')!.Type);
        Assert.Equal(KeyboardInputEventType.KeyUp, KeyInputMapper.Map(cef_key_event_type_t.KEYEVENT_KEYUP, 0, 'A', 'a', 'a')!.Type);
    }

    [Theory]
    [InlineData('A', 0u, "a", "KeyA")]
    [InlineData('A', Shift, "A", "KeyA")]
    [InlineData('7', 0u, "7", "Digit7")]
    [InlineData(13, 0u, "Enter", "Enter")]
    [InlineData(37, 0u, "ArrowLeft", "ArrowLeft")]
    [InlineData(112, 0u, "F1", "F1")]
    [InlineData(135, 0u, "F24", "F24")]
    [InlineData(32, 0u, " ", "Space")]
    [InlineData(17, 0u, "Control", "ControlLeft")]
    [InlineData(96, 0u, "0", "Numpad0")]
    [InlineData(107, 0u, "+", "NumpadAdd")]
    [InlineData(189, 0u, "Unidentified", "Minus")]
    public void Map_DerivesKeyAndCode(int vk, uint modifiers, string key, string code)
    {
        var input = KeyInputMapper.Map(Down, modifiers, vk, '\0', '\0')!;
        Assert.Equal((key, code), (input.Key, input.Code));
    }

    [Fact]
    public void Map_PrefersTheTypedCharacterForUnnamedKeys()
        => Assert.Equal("_", KeyInputMapper.Map(Down, Shift, 189, '_', '-')!.Key);

    [Fact]
    public void Map_ReadsModifiersRepeatAndLocation()
    {
        var input = KeyInputMapper.Map(Down, Shift | (uint)cef_event_flags_t.EVENTFLAG_CONTROL_DOWN | (uint)cef_event_flags_t.EVENTFLAG_ALT_DOWN
            | (uint)cef_event_flags_t.EVENTFLAG_COMMAND_DOWN | (uint)cef_event_flags_t.EVENTFLAG_IS_REPEAT, 'A', 'a', 'a')!;
        Assert.True(input is { Shift: true, Control: true, Alt: true, Meta: true, IsAutoRepeat: true, IsComposing: false, Location: 0 });
        Assert.Equal(3, KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_KEY_PAD, 97, '1', '1')!.Location);
        Assert.Equal(InputModifiers.IsKeypad, KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_KEY_PAD, 97, '1', '1')!.Modifiers);
        Assert.Equal(InputModifiers.IsAutoRepeat, KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_REPEAT, 'A', 'a', 'a')!.Modifiers);
        Assert.Equal(2, KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_RIGHT, 16, '\0', '\0')!.Location);
        Assert.Equal("ShiftRight", KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_RIGHT, 16, '\0', '\0')!.Code);
        Assert.Equal(1, KeyInputMapper.Map(Down, 0, 162, '\0', '\0')!.Location);
    }

    [Fact]
    public void Map_KeypadEnterHasItsOwnCode()
        => Assert.Equal("NumpadEnter", KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_KEY_PAD, 13, '\r', '\r')!.Code);

    [Fact]
    public void Map_UsesTheTypedCharacterWhenItIsPrintable()
    {
        Assert.Equal("&", KeyInputMapper.Map(Down, Shift, '7', '&', '7')!.Key);
        Assert.Equal("-", KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_CONTROL_DOWN, 189, '\u001f', '-')!.Key);
        Assert.Equal(" ", KeyInputMapper.Map(Down, 0, 255, ' ', '\0')!.Key);
        Assert.Equal(" ", KeyInputMapper.Map(Down, 0, '7', ' ', '7')!.Key);
        Assert.Equal(" ", KeyInputMapper.Map(Down, 0, 255, '\u001f', ' ')!.Key);
        Assert.Equal("Unidentified", KeyInputMapper.Map(Down, 0, 255, '\u001f', '\0')!.Key);
        Assert.Equal("7", KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_CONTROL_DOWN, '7', '\u001f', '7')!.Key);
    }

    [Theory]
    [InlineData(106, "*", "NumpadMultiply")]
    [InlineData(107, "+", "NumpadAdd")]
    [InlineData(109, "-", "NumpadSubtract")]
    [InlineData(110, ".", "NumpadDecimal")]
    [InlineData(111, "/", "NumpadDivide")]
    [InlineData(105, "9", "Numpad9")]
    [InlineData(135, "F24", "F24")]
    [InlineData(91, "Meta", "MetaLeft")]
    [InlineData(92, "Meta", "MetaRight")]
    public void Map_KeypadAndFunctionKeys(int vk, string key, string code)
    {
        var input = KeyInputMapper.Map(Down, 0, vk, '\0', '\0')!;
        Assert.Equal((key, code), (input.Key, input.Code));
    }

    [Fact]
    public void Map_Locations()
    {
        Assert.Equal(2, KeyInputMapper.Map(Down, 0, 163, '\0', '\0')!.Location);
        Assert.Equal(1, KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_LEFT, 18, '\0', '\0')!.Location);
        Assert.Equal("AltRight", KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_RIGHT, 18, '\0', '\0')!.Code);
        Assert.Equal("MetaRight", KeyInputMapper.Map(Down, (uint)cef_event_flags_t.EVENTFLAG_IS_RIGHT, 91, '\0', '\0')!.Code);
        Assert.Equal(0, KeyInputMapper.Map(Down, 0, 'B', 'b', 'b')!.Location);
        Assert.Equal(2, KeyInputMapper.Map(Down, 0, 92, '\0', '\0')!.Location);
        Assert.Equal(2, KeyInputMapper.Map(Down, 0, 161, '\0', '\0')!.Location);
        Assert.Equal(2, KeyInputMapper.Map(Down, 0, 165, '\0', '\0')!.Location);
        Assert.Equal(1, KeyInputMapper.Map(Down, 0, 160, '\0', '\0')!.Location);
        Assert.Equal(1, KeyInputMapper.Map(Down, 0, 91, '\0', '\0')!.Location);
    }
}
