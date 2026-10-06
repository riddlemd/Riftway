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
