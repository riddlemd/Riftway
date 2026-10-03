namespace Oriel.Tests;

public sealed class WebContentsZoomTests
{
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(1.2, 1.0)]
    [InlineData(1.44, 2.0)]
    [InlineData(1 / 1.2, -1.0)]
    public void ZoomLevelOf_IsTheBase12Logarithm(double factor, double level) => Assert.Equal(level, WebContents.ZoomLevelOf(factor), 10);
}
