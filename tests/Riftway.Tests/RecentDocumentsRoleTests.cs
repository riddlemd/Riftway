using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class RecentDocumentsRoleTests
{
    [Fact]
    public void TheRoleIsLabelledOpenRecent() => Assert.Equal("Open Recent", new MenuItem { Role = MenuItemRole.RecentDocuments }.Label);

    [Fact]
    public void ItsSubmenuBecomesAppKitsRecentDocumentsMenuWithoutRiftwaysDelegate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var model = new Menu
        {
            new MenuItem { Role = MenuItemRole.RecentDocuments, Submenu = new Menu { new MenuItem { Role = MenuItemRole.ClearRecentDocuments } } },
            new MenuItem { Label = "Other", Submenu = new Menu { new MenuItem { Label = "x" } } },
        };
        var controller = MacApplicationMenu.CreatePopupMenu(model);
        try
        {
            var recent = MacMenu.Submenu(MacMenu.ItemAt(controller.NativeMenu, 0));
            var other = MacMenu.Submenu(MacMenu.ItemAt(controller.NativeMenu, 1));
            Assert.Equal("NSRecentDocumentsMenu", MacMenu.MenuName(recent));
            Assert.Equal(0, MacMenu.Delegate(recent));
            Assert.Equal("Clear Menu", MacMenu.Title(MacMenu.ItemAt(recent, 0)));
            Assert.Equal("", MacMenu.MenuName(other));
            Assert.NotEqual(0, MacMenu.Delegate(other));
        }
        finally { MacApplicationMenu.ReleasePopupMenu(controller); }
    }
}
