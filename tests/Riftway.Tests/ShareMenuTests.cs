using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class ShareMenuRoleTests
{
    [Fact]
    public void TheRoleIsAnEmptySubmenuLabelledShare()
    {
        var item = new MenuItem { Role = MenuItemRole.ShareMenu };
        Assert.Equal("Share", item.Label);
        Assert.Equal(MenuItemType.Submenu, item.Type);
        Assert.Empty(item.Submenu!.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnEmptyFilePathIsRefused(string? path)
        => Assert.Throws<ArgumentException>(() => new MenuItem { SharingItem = new SharingItem { FilePaths = [path!] } });

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void AnInvalidUrlIsRefused(string? url)
        => Assert.Throws<ArgumentException>(() => new MenuItem { SharingItem = new SharingItem { Urls = [url!] } });

    [Fact]
    public void AValidItemIsKeptAndSpelledAsGurlForMac()
    {
        var sharing = new SharingItem { Texts = ["t"], FilePaths = ["/tmp/a"], Urls = ["https://Example.com"] };
        var item = new MenuItem { SharingItem = sharing };
        Assert.Same(sharing, item.SharingItem);
        var (texts, files, urls) = sharing.ForMac();
        Assert.Equal(["t"], texts);
        Assert.Equal(["/tmp/a"], files);
        Assert.Equal(["https://example.com/"], urls);
        var none = new SharingItem().ForMac();
        Assert.Empty(none.Texts);
        Assert.Empty(none.FilePaths);
        Assert.Empty(none.Urls);
    }
}

public sealed class MacShareMenuTests
{
    [Fact]
    public void NothingToShareIsOneDisabledEmptyItem()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var menu = MacMenu.CreateShareMenu([], [], []);
        try
        {
            Assert.Equal(1, MacMenu.ItemCount(menu));
            var item = MacMenu.ItemAt(menu, 0);
            Assert.Equal("(empty)", MacMenu.Title(item));
            Assert.False(MacMenu.IsEnabled(item));
            Assert.Equal("", MacMenu.ActionName(item));
        }
        finally { MacMenu.Release(menu); }
    }

    [Fact]
    public void EachSharingServiceIsAnItemThatSharesEverything()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var menu = MacMenu.CreateShareMenu(["hello"], ["/tmp/riftway share.txt"], ["https://example.com/"]);
        try
        {
            Assert.True(MacMenu.ItemCount(menu) > 0);
            for (var i = 0; i < MacMenu.ItemCount(menu); i++)
            {
                var item = MacMenu.ItemAt(menu, i);
                Assert.Equal("riftwayPerformShare:", MacMenu.ActionName(item));
                Assert.NotEqual(0, MacMenu.SharingServiceOf(item));
                Assert.NotEqual("", MacMenu.Title(item));
                Assert.Equal(["hello", "file:///tmp/riftway%20share.txt", "https://example.com/"], MacMenu.SharedItemsOf(item));
            }
        }
        finally { MacMenu.Release(menu); }
    }

    [Fact]
    public void TheRoleBuildsTheShareMenuForItsSharingItem()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var model = new Menu
        {
            new MenuItem { Role = MenuItemRole.ShareMenu, SharingItem = new SharingItem { Texts = ["hi"] } },
            new MenuItem { Role = MenuItemRole.ShareMenu },
        };
        var controller = MacApplicationMenu.CreatePopupMenu(model);
        try
        {
            var shared = MacMenu.Submenu(MacMenu.ItemAt(controller.NativeMenu, 0));
            Assert.True(MacMenu.ItemCount(shared) > 0);
            Assert.Equal(["hi"], MacMenu.SharedItemsOf(MacMenu.ItemAt(shared, 0)));
            Assert.Equal("Share", MacMenu.Title(MacMenu.ItemAt(controller.NativeMenu, 0)));
            var empty = MacMenu.Submenu(MacMenu.ItemAt(controller.NativeMenu, 1));
            Assert.Equal("(empty)", MacMenu.Title(MacMenu.ItemAt(empty, 0)));
        }
        finally { MacApplicationMenu.ReleasePopupMenu(controller); }
    }
}

public sealed class ShareMenuPopupTests
{
    [Fact]
    public void PopupWindow_IsTheGivenOpenWindowElseFocusedElseFirst()
    {
        string a = "a", b = "b", gone = "gone";
        Assert.Same(b, ShareMenu.PopupWindow(b, [a, b], a));
        Assert.Same(a, ShareMenu.PopupWindow(gone, [a, b], a));
        Assert.Same(b, ShareMenu.PopupWindow(null, [a, b], b));
        Assert.Same(a, ShareMenu.PopupWindow(gone, [a, b], null));
        Assert.Null(ShareMenu.PopupWindow<string>(null, [], null));
    }

    [Fact]
    public void PopupPosition_FlipsTheGivenPointAndUsesThePointerOtherwise()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal((10.0, 280.0), MacMenu.PopupPosition(10, 20, (99, 99), 300, (0, 500), (50, 50), (0, 2000)));
        Assert.Equal((99.0, 98.0), MacMenu.PopupPosition(null, null, (99, 98), 300, (0, 500), (50, 50), (0, 2000)));
    }

    [Theory]
    [InlineData(100.0, 50.0, 200.0, 25.0, 129.0)]
    [InlineData(100.0, 125.0, 200.0, 25.0, 125.0)]
    [InlineData(100.0, 124.0, 200.0, 25.0, 129.0)]
    public void PopupPosition_RaisesAMenuThatWouldRunBelowTheScreen(double windowMinY, double y, double menuHeight, double screenMinY, double expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(expected, MacMenu.PopupPosition(null, null, (0, y), 300, (0, windowMinY), (10, menuHeight), (screenMinY, 2000)).Y);
    }

    [Theory]
    [InlineData(1000.0, 300.0, 200.0, 1440.0, 100.0)]
    [InlineData(1000.0, 240.0, 200.0, 1440.0, 240.0)]
    [InlineData(1000.0, 241.0, 200.0, 1440.0, 41.0)]
    public void PopupPosition_MovesAMenuThatWouldRunOffTheRightLeftOfThePoint(double windowMinX, double x, double menuWidth, double screenMaxX, double expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(expected, MacMenu.PopupPosition(null, null, (x, 500), 600, (windowMinX, 0), (menuWidth, 10), (0, screenMaxX)).X);
    }

    [Fact]
    public void ThePopupClosesOnceWhenItsTopMenuCloses()
    {
        var posted = 0;
        var popup = new MacSharePopup(7, () => posted++, a => a());
        popup.DidClose(7);
        Assert.Equal(0, posted);
        popup.WillOpen();
        Assert.True(popup.IsMenuOpen);
        popup.DidClose(8);
        Assert.Equal(0, posted);
        popup.DidClose(7);
        Assert.Equal(1, posted);
        Assert.False(popup.IsMenuOpen);
        popup.DidClose(7);
        Assert.Equal(1, posted);
    }

    [Fact]
    public void CancelEndsAnOpenPopupOnce()
    {
        var posted = 0;
        var cancelled = new List<nint>();
        var popup = new MacSharePopup(7, () => posted++, a => a());
        popup.Cancel(cancelled.Add);
        Assert.Empty(cancelled);
        popup.WillOpen();
        popup.Cancel(cancelled.Add);
        Assert.Equal([(nint)7], cancelled);
        Assert.Equal(1, posted);
        popup.DidClose(7);
        popup.Cancel(cancelled.Add);
        Assert.Equal(1, posted);
        Assert.Single(cancelled);
    }

    [Fact]
    public void TheClosedCallbackIsPostedNotRun()
    {
        var queue = new List<Action>();
        var ran = false;
        var popup = new MacSharePopup(7, () => ran = true, queue.Add);
        popup.WillOpen();
        popup.DidClose(7);
        Assert.False(ran);
        Assert.Single(queue);
        queue[0]();
        Assert.True(ran);
        ran = false;
        popup.WillOpen();
        popup.Cancel(_ => { });
        Assert.False(ran);
        Assert.Equal(2, queue.Count);
        queue[1]();
        Assert.True(ran);
    }

    [Fact]
    public void AnInvalidSharingItemIsRefused()
        => Assert.Throws<ArgumentException>(() => new ShareMenu(new SharingItem { Urls = ["nope"] }));

    [Fact]
    public void PopupNeedsMacOSBothCoordinatesAndAWindow()
    {
        var menu = new ShareMenu(new SharingItem { Texts = ["x"] });
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => menu.Popup());
            return;
        }
        Assert.Throws<ArgumentException>(() => menu.Popup(new PopupOptions { X = 1 }));
        var e = Assert.Throws<InvalidOperationException>(() => menu.Popup());
        Assert.Equal("Cannot open Menu without a BaseWindow present", e.Message);
    }
}
