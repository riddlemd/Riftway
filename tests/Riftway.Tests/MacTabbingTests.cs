namespace Riftway.Tests;

// Electron v44's NativeWindowMac constructor: tabbing only for an identifier and a native title bar.
public sealed class MacTabbingTests
{
    [Theory]
    [InlineData("docs", false, false, "docs")]
    [InlineData(null, false, false, null)]
    [InlineData("", false, false, null)]
    [InlineData("docs", true, false, null)]
    [InlineData("docs", false, true, null)]
    public void OnlyAnIdentifiedFramedOpaqueWindowTabs(string? identifier, bool transparent, bool frameless, string? tabbing)
        => Assert.Equal(tabbing, MacTabbing.Identifier(identifier, transparent, frameless));
}

// Electron v44's BaseWindow::PreviewFile: the display name defaults to the path.
public sealed class MacQuickLookTests
{
    [Fact]
    public void TheTitleIsTheDisplayName_ElseThePath()
    {
        Assert.Equal("Report", MacQuickLook.Title("/tmp/r.pdf", "Report"));
        Assert.Equal("/tmp/r.pdf", MacQuickLook.Title("/tmp/r.pdf", null));
        Assert.Equal("", MacQuickLook.Title("/tmp/r.pdf", ""));
    }
}

// Electron v44's NativeWindowMac constructor: roundedCorners false on a frameless window makes it borderless.
public sealed class MacRoundedCornersTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public void OnlyAFramelessWindowWithoutRoundedCornersIsBorderless(bool rounded, bool frameless, bool borderless)
        => Assert.Equal(borderless, MacRoundedCorners.Borderless(rounded, frameless));
}
