namespace Oriel.Tests;

public sealed class TrayTests
{
    [Theory]
    [InlineData(0x0201u, TrayEvent.Click)]
    [InlineData(0x0401u, TrayEvent.Click)]
    [InlineData(0x0203u, TrayEvent.DoubleClick)]
    [InlineData(0x0207u, TrayEvent.MiddleClick)]
    [InlineData(0x007Bu, TrayEvent.RightClick)]
    [InlineData(0x0200u, TrayEvent.MouseMove)]
    [InlineData(0x0402u, TrayEvent.BalloonShow)]
    [InlineData(0x0405u, TrayEvent.BalloonClick)]
    [InlineData(0x0403u, TrayEvent.BalloonClosed)]
    [InlineData(0x0404u, TrayEvent.BalloonClosed)]
    [InlineData(0x0202u, TrayEvent.None)]
    [InlineData(0x0400u, TrayEvent.None)]
    [InlineData(0x0204u, TrayEvent.None)]
    // TrayEvent is internal; a public test method takes it boxed.
    public void EventOf_MapsVersion4Codes(uint code, object e) => Assert.Equal(e, TrayRules.EventOf(code));

    [Theory]
    [InlineData(BalloonIconType.None, false, false, false, 0x00u)]
    [InlineData(BalloonIconType.Info, true, false, false, 0x21u)]
    [InlineData(BalloonIconType.Warning, false, true, false, 0x12u)]
    [InlineData(BalloonIconType.Error, false, false, true, 0x83u)]
    [InlineData(BalloonIconType.Custom, true, true, true, 0xB4u)]
    public void BalloonFlags_CombineTypeAndOptions(BalloonIconType type, bool large, bool noSound, bool quiet, uint flags)
        => Assert.Equal(flags, TrayRules.BalloonFlags(new DisplayBalloonOptions { IconType = type, LargeIcon = large, NoSound = noSound, RespectQuietTime = quiet }));

    [Fact]
    public void BalloonFlags_AnIconAlone_MeansCustom_AndLargeIsTheDefault()
    {
        var icon = NativeImage.CreateEmpty();
        Assert.Equal(0x24u, TrayRules.BalloonFlags(new DisplayBalloonOptions { Icon = icon }));
        Assert.Equal(0x21u, TrayRules.BalloonFlags(new DisplayBalloonOptions { Icon = icon, IconType = BalloonIconType.Info }));
        Assert.Equal(0x20u, TrayRules.BalloonFlags(new DisplayBalloonOptions()));
    }

    [Theory]
    [InlineData("CmdOrCtrl+Shift+S", "Ctrl+Shift+S")]
    [InlineData("CommandOrControl+q", "Ctrl+Q")]
    [InlineData("Control+Alt+Delete", "Ctrl+Alt+Delete")]
    [InlineData("Option+F4", "Alt+F4")]
    [InlineData("Ctrl+Plus", "Ctrl++")]
    [InlineData("Cmd+Q", null)]
    [InlineData("Super+L", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AcceleratorText_WritesItTheWindowsWay(string? accelerator, string? text) => Assert.Equal(text, TrayRules.AcceleratorText(accelerator));

    [Fact]
    public void MenuEntries_FollowTheMenu()
    {
        var menu = new Menu
        {
            new MenuItem { Label = "&Show", Accelerator = "CmdOrCtrl+S" },
            new MenuItem { Type = MenuItemType.Separator },
            new MenuItem { Label = "Pinned", Type = MenuItemType.Checkbox, Checked = true },
            new MenuItem { Label = "Plain", Checked = true },
            new MenuItem { Label = "A", Type = MenuItemType.Radio, Checked = true },
            new MenuItem { Label = "Off", Enabled = false },
            new MenuItem { Label = "Hidden", Visible = false },
            new MenuItem { Label = "More", Submenu = new Menu { new MenuItem { Label = "Deep" } } },
        };
        var entries = TrayRules.MenuEntries(menu);
        Assert.Equal(["&Show\tCtrl+S", "", "Pinned", "Plain", "A", "Off", "More"], entries.Select(e => e.Text));
        Assert.Equal([false, true, false, false, false, false, false], entries.Select(e => e.Separator));
        Assert.Equal([false, false, true, false, true, false, false], entries.Select(e => e.Checked));
        Assert.Equal([false, false, false, false, true, false, false], entries.Select(e => e.Radio));
        Assert.Equal([true, true, true, true, true, false, true], entries.Select(e => e.Enabled));
        Assert.Equal(menu.Items[0].CommandId, entries[0].CommandId);
        Assert.Equal(0, entries[1].CommandId);
        Assert.Equal(["Deep"], entries[6].Submenu!.Select(e => e.Text));
        Assert.All(entries.Take(6), e => Assert.Null(e.Submenu));
    }

    [Fact]
    public void DisplayBalloonOptions_DefaultToElectrons()
    {
        var o = new DisplayBalloonOptions();
        Assert.True(o.LargeIcon);
        Assert.False(o.NoSound);
        Assert.False(o.RespectQuietTime);
        Assert.Equal("", o.Title);
    }
}
