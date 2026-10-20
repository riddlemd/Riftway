namespace Oriel.Tests;

// Electron v44's menu-item.ts validateBadge and the macOS 14 item types.
public sealed class MenuItemBadgeTests
{
    [Theory]
    [InlineData(MenuItemBadgeType.None, null, "hi", null)]
    [InlineData(MenuItemBadgeType.None, null, null, "badge.content must be a string when badge.type is 'none'")]
    [InlineData(MenuItemBadgeType.None, 2, "hi", "badge.count cannot be used when badge.type is 'none'")]
    [InlineData(MenuItemBadgeType.Alerts, 0, null, null)]
    [InlineData(MenuItemBadgeType.Updates, 3, null, null)]
    [InlineData(MenuItemBadgeType.NewItems, null, null, "badge.count must be a non-negative integer when badge.type is 'new-items'")]
    [InlineData(MenuItemBadgeType.Alerts, -1, null, "badge.count must be a non-negative integer when badge.type is 'alerts'")]
    [InlineData(MenuItemBadgeType.Updates, 1, "x", "badge.content can only be used when badge.type is 'none'")]
    public void BadgesAreCheckedWithElectronsMessages(MenuItemBadgeType type, int? count, string? content, string? error)
    {
        var badge = new MenuItemBadge { Type = type, Count = count, Content = content };
        if (error is null) Assert.Same(badge, badge.Validated());
        else Assert.Equal(error, Assert.Throws<ArgumentException>(() => badge.Validated()).Message);
    }

    [Fact]
    public void TheBadgeReachesTheMacState_AndSettingItTellsTheMenus()
    {
        var item = new MenuItem { Label = "Inbox" };
        var menu = new Menu { item };
        var changes = 0;
        menu.Changed += () => changes++;
        item.Badge = new MenuItemBadge { Type = MenuItemBadgeType.Alerts, Count = 4 };
        Assert.Equal(1, changes);
        Assert.Equal(item.Badge, MacMenuItemState.Of(item)!.Value.Badge);
    }

    [Fact]
    public void OnMacABadgeIsCheckedWhenSet()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Throws<ArgumentException>(() => new MenuItem { Badge = new MenuItemBadge() });
    }
}
