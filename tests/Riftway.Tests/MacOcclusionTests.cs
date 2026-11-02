using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class MacOcclusionTests
{
    [Theory]
    [InlineData(2u, false, true)]
    [InlineData(0u, false, false)]
    [InlineData(8194u, false, true)]
    [InlineData(8192u, false, false)]
    public void TheVisibleBitDecides(uint state, bool fromChecker, bool visible)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(visible, MacWindow.OcclusionVisible(state, fromChecker));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(0u)]
    public void TheOcclusionCheckersNotificationsAreIgnored(uint state)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Null(MacWindow.OcclusionVisible(state, fromOcclusionChecker: true));
    }
}
