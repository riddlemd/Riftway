namespace Oriel.Tests;

public sealed class BrowserWindowColorTests
{
    [Theory]
    [InlineData("#202830", 0xFF202830u)]
    [InlineData("202830", 0xFF202830u)]
    [InlineData("#80202830", 0x80202830u)]
    [InlineData("#ffffff", 0xFFFFFFFFu)]
    public void ParseColor_ReadsRgbAndArgb(string color, uint argb) => Assert.Equal(argb, BrowserWindow.ParseColor(color));

    [Theory]
    [InlineData("#2028")]
    [InlineData("#20283040FF")]
    [InlineData("#GG2830")]
    [InlineData("")]
    public void ParseColor_RejectsOtherForms(string color) => Assert.Throws<FormatException>(() => BrowserWindow.ParseColor(color));
}
