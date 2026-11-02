namespace Riftway.Tests;

// Electron v44's NativeWindowMac constructor for options.type.
public sealed class MacWindowTypeTests
{
    [Theory]
    [InlineData("desktop", false, true, false, false)]
    [InlineData("desktop", true, true, false, false)]
    [InlineData("panel", false, false, true, false)]
    [InlineData("textured", true, false, false, true)]
    [InlineData("textured", false, false, false, false)]
    [InlineData("toolbar", true, false, false, false)]
    [InlineData(null, true, false, false, false)]
    [InlineData("Desktop", false, false, false, false)]
    public void EachTypeDoesWhatElectronDoes(string? type, bool transparentOrFrameless, bool desktop, bool floating, bool textured)
        => Assert.Equal(new MacWindowType(desktop, floating, textured), MacWindowType.For(type, transparentOrFrameless));

    [Fact]
    public void TheMasksAreAppKits()
    {
        Assert.Equal((nuint)81, MacWindowType.DesktopBehavior);
        Assert.Equal((nuint)256, MacWindowType.TexturedStyle);
    }
}
