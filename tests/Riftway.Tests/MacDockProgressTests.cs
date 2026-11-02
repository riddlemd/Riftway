using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Electron v44's NativeWindowMac::SetProgressBar and ElectronProgressBar drawRect:.
public sealed class MacDockProgressTests
{
    [Theory]
    [InlineData(-0.01, true, false, 0)]
    [InlineData(-1, true, false, 0)]
    [InlineData(0, false, false, 0)]
    [InlineData(0.555, false, false, 0.555)]
    [InlineData(1, false, false, 1)]
    [InlineData(1.01, false, true, 1)]
    [InlineData(double.NaN, false, false, 0)]
    public void OnlyTheValueCounts(double progress, bool hidden, bool indeterminate, double value)
        => Assert.Equal((hidden, indeterminate, value), ProgressBar.MacDock(progress));

    [Fact]
    public void TheFillIsTheInsideFlooredToTheValue()
    {
        var g = MacProgressBarGeometry.Of(width: 128, height: 15, value: 0.5, max: 1);
        Assert.Equal(new MacProgressBarGeometry.Rect(1, 1, 126, 13), g.Outline);
        Assert.Equal(new MacProgressBarGeometry.Rect(3, 3, 122, 9), g.Inside);
        Assert.Equal(new MacProgressBarGeometry.Rect(3, 3, 61, 9), g.Fill);
    }

    [Theory]
    [InlineData(0.333, 2, 20)]
    [InlineData(0.999, 1, 121)]
    [InlineData(1, 1, 122)]
    [InlineData(0, 1, 0)]
    public void TheFillIsAShareOfTheMaximum_Floored(double value, double max, double width)
        => Assert.Equal(width, MacProgressBarGeometry.Of(128, 15, value, max).Fill.Width);
}
