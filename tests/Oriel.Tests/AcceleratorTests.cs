namespace Oriel.Tests;

using M = AcceleratorModifiers;

public sealed class AcceleratorTests
{
    [Theory]
    [InlineData("CmdOrCtrl+Z", true, 'Z', "z", (int)(M.Command))]
    [InlineData("CmdOrCtrl+Z", false, 'Z', "z", (int)(M.Control))]
    [InlineData("CommandOrControl+Shift+s", false, 'S', "s", (int)(M.Control | M.Shift))]
    [InlineData("Alt+Command+I", true, 'I', "i", (int)(M.Alt | M.Command))]
    [InlineData("Option+Ctrl+1", false, '1', "1", (int)(M.Alt | M.Control))]
    [InlineData("Super+Q", false, 'Q', "q", (int)(M.Command))]
    [InlineData("Meta+Q", true, 'Q', "q", (int)(M.Command))]
    [InlineData("F11", false, 0x7A, "\uF70E", (int)(M.None))]
    [InlineData("f1", true, 0x70, "\uF704", (int)(M.None))]
    [InlineData("F24", false, 0x87, "\uF71B", (int)(M.None))]
    [InlineData("CmdOrCtrl+Plus", false, 0xBB, "+", (int)(M.Control))]
    [InlineData("CmdOrCtrl++", false, 0xBB, "+", (int)(M.Control))]
    [InlineData("CmdOrCtrl+-", true, 0xBD, "-", (int)(M.Command))]
    [InlineData("Ctrl+,", false, 0xBC, ",", (int)(M.Control))]
    [InlineData("Shift+Delete", false, 0x2E, "\uF728", (int)(M.Shift))]
    [InlineData("Backspace", true, 0x08, "\b", (int)(M.None))]
    [InlineData("Escape", false, 0x1B, "\u001B", (int)(M.None))]
    [InlineData("Ctrl+Up", false, 0x26, "\uF700", (int)(M.Control))]
    [InlineData("Return", false, 0x0D, "\r", (int)(M.None))]
    public void Parse_ResolvesKeyAndModifiers(string text, bool macOS, int keyCode, string macKey, int modifiers)
        => Assert.Equal(new Accelerator(keyCode, macKey, (M)modifiers), Accelerator.Parse(text, macOS));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("A+B")]
    [InlineData("Hyper+A")]
    [InlineData("F25")]
    [InlineData("Ctrl+é")]
    public void Parse_RejectsInvalidAccelerators(string text) => Assert.ThrowsAny<ArgumentException>(() => Accelerator.Parse(text, false));
}
