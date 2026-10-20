using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class MacWindowEventTests
{
    // gfx::ScreenRectFromNSRect.
    [Theory]
    [InlineData(10, 100, 300, 200, 900, 10, 600)]
    [InlineData(-50, 0, 20, 30, 900, -50, 870)]
    [InlineData(5, 950, 40, 60, 900, 5, -110)]
    public void TheRectIsFlippedAgainstThePrimaryDisplay(double x, double y, double width, double height, double primaryHeight, double expectedX, double expectedY)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal((expectedX, expectedY, width, height), MacWindow.ScreenRectFromNSRect(x, y, width, height, primaryHeight));
    }
}
