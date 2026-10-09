namespace Oriel.Tests;

using M = AcceleratorModifiers;

public sealed class AcceleratorTests
{
    [Theory]
    [InlineData("CmdOrCtrl+Z", true, 'Z', "z", (int)(M.Command))]
    [InlineData("CmdOrCtrl+Z", false, 'Z', "z", (int)(M.Control))]
    [InlineData("CommandOrControl+Shift+s", false, 'S', "S", (int)(M.Control | M.Shift))]
    [InlineData("Alt+Command+I", true, 'I', "i", (int)(M.Alt | M.Command))]
    [InlineData("Option+Ctrl+1", false, '1', "1", (int)(M.Alt | M.Control))]
    [InlineData("Super+Q", false, 'Q', "q", (int)(M.Command))]
    [InlineData("Meta+Q", true, 'Q', "q", (int)(M.Command))]
    [InlineData("F11", false, 0x7A, "\uF70E", (int)(M.None))]
    [InlineData("f1", true, 0x70, "\uF704", (int)(M.None))]
    [InlineData("F24", false, 0x87, "\uF71B", (int)(M.None))]
    [InlineData("CmdOrCtrl+Plus", false, 0xBB, "+", (int)(M.Control | M.Shift))]
    [InlineData("CmdOrCtrl+-", true, 0xBD, "-", (int)(M.Command))]
    [InlineData("Ctrl+,", false, 0xBC, ",", (int)(M.Control))]
    [InlineData("Shift+Delete", false, 0x2E, "\uF728", (int)(M.Shift))]
    [InlineData("Backspace", true, 0x08, "\b", (int)(M.None))]
    [InlineData("Escape", false, 0x1B, "\u001B", (int)(M.None))]
    [InlineData("Ctrl+Up", false, 0x26, "\uF700", (int)(M.Control))]
    [InlineData("Return", false, 0x0D, "\r", (int)(M.None))]
    [InlineData("MediaPlayPause", true, 0xB3, "", (int)(M.None))]
    [InlineData("medianexttrack", false, 0xB0, "", (int)(M.None))]
    [InlineData("MediaPreviousTrack", true, 0xB1, "", (int)(M.None))]
    [InlineData("Shift+MediaStop", true, 0xB2, "", (int)(M.Shift))]
    [InlineData("VolumeMute", false, 0xAD, "", (int)(M.None))]
    [InlineData("VolumeDown", true, 0xAE, "", (int)(M.None))]
    [InlineData("VolumeUp", true, 0xAF, "", (int)(M.None))]
    public void Parse_ResolvesKeyAndModifiers(string text, bool macOS, int keyCode, string macKey, int modifiers)
        => Assert.Equal(new Accelerator(keyCode, macKey, (M)modifiers), Accelerator.Parse(text, macOS));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("A+Hyper")]
    [InlineData("F25")]
    [InlineData("num10")]
    [InlineData("Ctrl+é")]
    [InlineData("CmdOrCtrl++")]
    [InlineData("++")]
    [InlineData("é+A")]
    [InlineData("Ctrl+\u212A")]
    public void Parse_RejectsInvalidAccelerators(string text) => Assert.ThrowsAny<ArgumentException>(() => Accelerator.Parse(text, false));

    [Theory]
    [InlineData("A+B", 'B')]
    [InlineData("Hyper+A", 'A')]
    [InlineData("Ctrl+a+z", 'Z')]
    public void Parse_LastNonModifierTokenWins(string text, int keyCode) => Assert.Equal(keyCode, Accelerator.Parse(text, false).KeyCode);

    [Theory]
    [InlineData("num0", 0x60, "0")]
    [InlineData("num1", 0x61, "1")]
    [InlineData("num2", 0x62, "2")]
    [InlineData("num3", 0x63, "3")]
    [InlineData("num4", 0x64, "4")]
    [InlineData("num5", 0x65, "5")]
    [InlineData("num6", 0x66, "6")]
    [InlineData("num7", 0x67, "7")]
    [InlineData("num8", 0x68, "8")]
    [InlineData("num9", 0x69, "9")]
    [InlineData("numdec", 0x6E, ".")]
    [InlineData("numadd", 0x6B, "+")]
    [InlineData("numsub", 0x6D, "-")]
    [InlineData("nummult", 0x6A, "*")]
    [InlineData("numdiv", 0x6F, "/")]
    [InlineData("numlock", 0x90, "")]
    [InlineData("capslock", 0x14, "")]
    [InlineData("scrolllock", 0x91, "")]
    [InlineData("printscreen", 0x2C, "")]
    [InlineData("NumAdd", 0x6B, "+")]
    [InlineData("CapsLock", 0x14, "")]
    [InlineData("PrintScreen", 0x2C, "")]
    public void Parse_NumpadAndLockKeys(string key, int keyCode, string macKey)
        => Assert.Equal(new Accelerator(keyCode, macKey, M.Control), Accelerator.Parse("Ctrl+" + key, false));

    [Theory]
    [InlineData("!", 0x31, "!")]
    [InlineData("@", 0x32, "@")]
    [InlineData("#", 0x33, "#")]
    [InlineData("$", 0x34, "$")]
    [InlineData("%", 0x35, "%")]
    [InlineData("^", 0x36, "^")]
    [InlineData("&", 0x37, "&")]
    [InlineData("*", 0x38, "*")]
    [InlineData("(", 0x39, "(")]
    [InlineData(")", 0x30, ")")]
    [InlineData("\"", 0xDE, "\"")]
    [InlineData(":", 0xBA, ":")]
    [InlineData("<", 0xBC, "<")]
    [InlineData(">", 0xBE, ">")]
    [InlineData("?", 0xBF, "?")]
    [InlineData("_", 0xBD, "_")]
    [InlineData("{", 0xDB, "{")]
    [InlineData("|", 0xDC, "|")]
    [InlineData("}", 0xDD, "}")]
    [InlineData("~", 0xC0, "~")]
    public void Parse_ShiftedCharacterAddsShift(string key, int keyCode, string macKey)
        => Assert.Equal(new Accelerator(keyCode, macKey, M.Control | M.Shift), Accelerator.Parse("Ctrl+" + key, false));

    [Theory]
    [InlineData("=", 0xBB, "=")]
    [InlineData(";", 0xBA, ";")]
    [InlineData("'", 0xDE, "'")]
    [InlineData("`", 0xC0, "`")]
    [InlineData("0", 0x30, "0")]
    public void Parse_UnshiftedCharacterAddsNoShift(string key, int keyCode, string macKey)
        => Assert.Equal(new Accelerator(keyCode, macKey, M.Control), Accelerator.Parse("Ctrl+" + key, false));

    [Fact]
    public void Parse_ShiftFromAShiftedCharacterIsAddedOnce()
    {
        Assert.Equal(Accelerator.Parse("Ctrl+Shift+1", false).Modifiers, Accelerator.Parse("Ctrl+!", false).Modifiers | M.Shift);
        Assert.Equal(M.Shift | M.Alt, Accelerator.Parse("Shift+Alt+(", false).Modifiers);
        Assert.Equal(M.Shift, Accelerator.Parse("Ctrl+)+Z", false).Modifiers & M.Shift);
    }

    [Theory]
    [InlineData("Ctrl + A")]
    [InlineData(" ctrl+a ")]
    [InlineData("CTRL+A")]
    [InlineData("Ctrl+++A")]
    [InlineData("Ctrl+A++")]
    public void Parse_TrimsAndIgnoresCase(string text)
        => Assert.Equal(new Accelerator('A', "a", M.Control), Accelerator.Parse(text, false));

    [Theory]
    [InlineData("\b", 0x08)]
    [InlineData("\u001B", 0x1B)]
    [InlineData("\u007F", 0x2E)]
    public void Parse_ControlCharacterTokens(string key, int keyCode) => Assert.Equal(keyCode, Accelerator.Parse(key, false).KeyCode);

    [Theory]
    [InlineData("Shift+1", "!", (int)M.Shift, (int)M.None)]
    [InlineData("!", "!", (int)M.Shift, (int)M.None)]
    [InlineData("Cmd+Shift+A", "A", (int)(M.Command | M.Shift), (int)M.Command)]
    [InlineData("Cmd+Plus", "+", (int)(M.Command | M.Shift), (int)M.Command)]
    [InlineData("Cmd+Shift+/", "?", (int)(M.Command | M.Shift), (int)M.Command)]
    [InlineData("Cmd+Shift+Enter", "\r", (int)(M.Command | M.Shift), (int)(M.Command | M.Shift))]
    [InlineData("Shift+numadd", "+", (int)M.Shift, (int)M.Shift)]
    [InlineData("Shift+num5", "5", (int)M.Shift, (int)M.Shift)]
    public void MacMenu_DropsShiftWhenTheKeyEquivalentIsTheShiftedCharacter(string text, string equivalent, int modifiers, int menuModifiers)
    {
        var parsed = Accelerator.Parse(text, macOS: true);
        Assert.Equal(equivalent, parsed.MacKeyEquivalent);
        Assert.Equal((M)modifiers, parsed.Modifiers);
        Assert.Equal((M)menuModifiers, parsed.MacMenuModifiers);
    }
}
