namespace Riftway.Tests;

public sealed class WebContentsZoomTests
{
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(1.2, 1.0)]
    [InlineData(1.44, 2.0)]
    [InlineData(1 / 1.2, -1.0)]
    public void ZoomLevelOf_IsTheBase12Logarithm(double factor, double level) => Assert.Equal(level, WebContents.ZoomLevelOf(factor), 10);

    [Theory]
    [InlineData(1.0, 1.0, false)]
    [InlineData(1.0, 3.0, true)]
    [InlineData(0.5, 1.0, true)]
    [InlineData(1.0, 1.0001, true)]
    public void AllowsPinch_OnlyWhenTheLimitsLeaveRoom(double minimum, double maximum, bool allowed)
        => Assert.Equal(allowed, WebContents.AllowsPinch(minimum, maximum));
}
