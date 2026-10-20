using Oriel.Cef.Platform;

namespace Oriel.Tests;

// Electron v44's swiz_nsview_swipeWithEvent: the direction from the deltas, in its order.
public sealed class MacSwipeTests
{
    [Theory]
    [InlineData(0, 1, "up")]
    [InlineData(-1, 0, "right")]
    [InlineData(0, -1, "down")]
    [InlineData(1, 0, "left")]
    [InlineData(-1, 1, "up")]
    [InlineData(1, -1, "down")]
    [InlineData(-1, -1, "right")]
    [InlineData(0.5, 0, null)]
    [InlineData(0, 0, null)]
    public void TheDirectionIsElectrons(double deltaX, double deltaY, string? direction) => Assert.Equal(direction, MacSwipe.Direction(deltaX, deltaY));
}
