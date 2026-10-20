using Oriel.Cef.Platform;

namespace Oriel.Tests;

// Electron v44's WindowButtonsProxy redraw and the NativeWindowMac title bar rules.
public sealed class MacWindowButtonsLayoutTests
{
    // AppKit's traffic lights: 14 × 16, 6 apart, 6 above and below in a 28-point title bar.
    private static readonly MacWindowButtonsLayout Layout = new(ButtonWidth: 14, ButtonHeight: 16, Padding: 6, DefaultMarginY: 6);

    [Fact]
    public void TheMarginPlacesTheButtonsFromTheLeft_AndSizesTheContainer()
        => Assert.Equal(new MacWindowButtonsFrame(ContainerHeight: 38, LeftX: 12, MiddleX: 32, RightX: 52, ButtonY: 11),
            Layout.Redraw(windowWidth: 800, marginX: 12, marginY: 11, height: 0, rightToLeft: false));

    [Fact]
    public void RightToLeftTheMarginIsFromTheRightEdge()
        => Assert.Equal(new MacWindowButtonsFrame(38, 800 - 3 * 14 - 2 * 6 - 12, 800 - 3 * 14 - 2 * 6 - 12 + 20, 800 - 3 * 14 - 2 * 6 - 12 + 40, 11),
            Layout.Redraw(800, 12, 11, 0, rightToLeft: true));

    [Fact]
    public void ATallerHeightSizesTheContainer_AndCentresTheButtonsAtTheDefaultMargin()
        => Assert.Equal(new MacWindowButtonsFrame(40, 7, 27, 47, 12), Layout.Redraw(800, 7, 6, height: 40, rightToLeft: false));

    [Fact]
    public void ATallerHeightWithItsOwnMarginKeepsTheButtonsThatFarFromTheTop()
        => Assert.Equal(new MacWindowButtonsFrame(40, 7, 27, 47, 40 - 16 - 10), Layout.Redraw(800, 7, 10, height: 40, rightToLeft: false));

    [Fact]
    public void AHeightNoTallerThanTheDefaultIsNotUsedForTheContainer()
    {
        Assert.False(Layout.UsesCustomHeight(28));
        Assert.True(Layout.UsesCustomHeight(28.5));
        // getCurrentMargin still centres in the given height.
        Assert.Equal(new MacWindowButtonsFrame(28, 7, 27, 47, 5), Layout.Redraw(800, 7, 6, height: 26, rightToLeft: false));
    }
}

public sealed class MacTitleBarTests
{
    [Theory]
    [InlineData(TitleBarStyle.Default, false)]
    [InlineData(TitleBarStyle.Hidden, true)]
    [InlineData(TitleBarStyle.HiddenInset, true)]
    [InlineData(TitleBarStyle.CustomButtonsOnHover, true)]
    public void OnlyHiddenStylesKeepTheTrafficLights(TitleBarStyle style, bool proxy)
        => Assert.Equal(proxy, MacTitleBar.UsesButtonsProxy(style));

    [Theory]
    [InlineData(TitleBarStyle.Default, false)]
    [InlineData(TitleBarStyle.Hidden, true)]
    [InlineData(TitleBarStyle.HiddenInset, true)]
    [InlineData(TitleBarStyle.CustomButtonsOnHover, false)]
    public void CustomButtonsOnHoverStartsWithTheButtonsHidden(TitleBarStyle style, bool shown)
        => Assert.Equal(shown, MacTitleBar.ButtonsShownAtCreation(style));

    [Fact]
    public void TheMarginIsThePositionGiven_ElseHiddenInsets_ElseAppKits()
    {
        Assert.Equal(new Point(3, 4), MacTitleBar.Margin(TitleBarStyle.HiddenInset, new Point(3, 4)));
        Assert.Equal(new Point(3, 4), MacTitleBar.Margin(TitleBarStyle.Hidden, new Point(3, 4)));
        Assert.Equal(new Point(12, 11), MacTitleBar.Margin(TitleBarStyle.HiddenInset, null));
        Assert.Null(MacTitleBar.Margin(TitleBarStyle.Hidden, null));
        Assert.Null(MacTitleBar.Margin(TitleBarStyle.CustomButtonsOnHover, null));
    }

    [Theory]
    [InlineData("ar", true)]
    [InlineData("he", true)]
    [InlineData("en-US", false)]
    [InlineData("", false)]
    [InlineData("not a locale!", false)]
    public void RightToLeftFollowsTheAppLocale(string locale, bool rtl)
        => Assert.Equal(rtl, MacTitleBar.IsRightToLeft(locale));
}
