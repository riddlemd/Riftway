namespace Oriel.Tests;

public sealed class AppBadgeTests
{
    [Fact]
    public void BadgeText_FollowsChromiumsBadgeManager()
    {
        Assert.Equal("•", OrielApp.BadgeText(null));
        Assert.Equal("1", OrielApp.BadgeText(1));
        Assert.Equal("99", OrielApp.BadgeText(99));
        Assert.Equal("99+", OrielApp.BadgeText(100));
    }
}
