using System.Runtime.Versioning;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class WindowTransparencyTests
{
    [Fact]
    public void InitialBackgroundColor_OwnColourWins() =>
        Assert.Equal("#80FF0000", WindowTransparency.InitialBackgroundColor("#80FF0000", transparent: true, BackgroundMaterial.Mica));

    [Fact]
    public void InitialBackgroundColor_TransparentWindowIsClear() =>
        Assert.Equal("#00000000", WindowTransparency.InitialBackgroundColor(null, transparent: true, material: null));

    [Theory]
    [InlineData(BackgroundMaterial.Auto)]
    [InlineData(BackgroundMaterial.Mica)]
    [InlineData(BackgroundMaterial.Acrylic)]
    [InlineData(BackgroundMaterial.Tabbed)]
    public void InitialBackgroundColor_MaterialOtherThanNoneIsClear(BackgroundMaterial material) =>
        Assert.Equal("#00000000", WindowTransparency.InitialBackgroundColor(null, transparent: false, material));

    [Fact]
    public void InitialBackgroundColor_NoneOrNothingLeavesCefDefault()
    {
        Assert.Null(WindowTransparency.InitialBackgroundColor(null, transparent: false, BackgroundMaterial.None));
        Assert.Null(WindowTransparency.InitialBackgroundColor(null, transparent: false, material: null));
    }

    // Electron's WebContentsPreferences: the window's copied backgroundColor wins over webPreferences.transparent.
    [Theory]
    [InlineData("#FFFFFFFF", true)]
    [InlineData("#00000000", false)]
    [InlineData("#80102030", true)]
    public void PageBackgroundColor_WindowColourWins(string window, bool transparent) =>
        Assert.Equal(window, WindowTransparency.PageBackgroundColor(window, transparent));

    [Fact]
    public void PageBackgroundColor_TransparentPreferenceClearsAPageWithoutWindowColour() =>
        Assert.Equal("#00000000", WindowTransparency.PageBackgroundColor(null, transparentPreference: true));

    [Fact]
    public void PageBackgroundColor_FalseOrUnsetLeavesChromiumWhite() =>
        Assert.Null(WindowTransparency.PageBackgroundColor(null, transparentPreference: false));

    // Electron's documented default of true is a <webview>'s; a window's or view's page acts only on an explicit true.
    [Fact]
    public void WebPreferences_TransparentIsOffByDefault() => Assert.False(new WebPreferences().Transparent);

    [Fact]
    public void WindowFill_OwnColourWins() =>
        Assert.Equal("#00000000", WindowTransparency.WindowFill("#00000000", transparentPreference: true));

    [Fact]
    public void WindowFill_WhiteUnderATransparentPage() =>
        Assert.Equal("#FFFFFFFF", WindowTransparency.WindowFill(null, transparentPreference: true));

    [Fact]
    public void WindowFill_NothingLeavesCefDefault() =>
        Assert.Null(WindowTransparency.WindowFill(null, transparentPreference: false));

    // Electron's NativeWindowViews::SetBackgroundColor on Linux: a transparent window's root view and compositor stay clear.
    [Theory]
    [InlineData("#FFFFFFFF", false)]
    [InlineData("#80000000", true)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void WindowFill_ClearWindowStaysClearWhateverTheColour(string? window, bool transparentPreference) =>
        Assert.Equal("#00000000", WindowTransparency.WindowFill(window, transparentPreference, clearWindow: true));

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void TranslucentWidget_TransparentWithPatchedCefOffWindows(bool transparent, bool patched, bool windows, bool expected) =>
        Assert.Equal(expected, WindowTransparency.TranslucentWidget(transparent, patched, windows));

    [Theory]
    [InlineData(BackgroundMaterial.Mica, "#00000000")]
    [InlineData(BackgroundMaterial.Acrylic, "#00000000")]
    [InlineData(BackgroundMaterial.Tabbed, "#00000000")]
    [InlineData(BackgroundMaterial.None, "#FFFFFFFF")]
    [InlineData(BackgroundMaterial.Auto, null)]
    public void MaterialBackgroundColor_FollowsElectronsSetBackgroundMaterial(BackgroundMaterial material, string? color) =>
        Assert.Equal(color, WindowTransparency.MaterialBackgroundColor(material));

    [Theory]
    [InlineData(0xFF102030u, 0xFF102030u)]
    [InlineData(0xFE102030u, 0u)]
    [InlineData(0x00000000u, 0u)]
    public void BrowserViewColor_OpaqueOnly(uint argb, uint expected) => Assert.Equal(expected, WindowTransparency.BrowserViewColor(argb));

    [Fact]
    public void PageColor_SplitsArgbWithAlphaAsFraction()
    {
        Assert.Equal((0x10, 0x20, 0x30, 1.0), WindowTransparency.PageColor(0xFF102030));
        Assert.Equal((0xAB, 0xCD, 0xEF, 0.0), WindowTransparency.PageColor(0x00ABCDEF));
        Assert.Equal(0x80 / 255.0, WindowTransparency.PageColor(0x80000000).A);
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void ReportsResizable_FramedTransparentIsNot(bool frame, bool transparent, bool resizable, bool reported) =>
        Assert.Equal(reported, WindowTransparency.ReportsResizable(frame, transparent, resizable));

    [Fact]
    public void FillsWorkArea_ExactBoundsAndNotMinimized()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        Assert.True(WindowTransparency.FillsWorkArea(area, area, minimized: false));
        Assert.False(WindowTransparency.FillsWorkArea(area, area, minimized: true));
        Assert.False(WindowTransparency.FillsWorkArea(area with { Height = 1039 }, area, minimized: false));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void StripFrame_RemovesCaptionAndSizingBorderOnly()
    {
        // WS_VISIBLE | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX
        Assert.Equal(0x100B0000u, WindowsTranslucency.StripFrame(0x10CF0000));
        Assert.Equal(0x10000000u, WindowsTranslucency.StripFrame(0x10000000));
    }
}
