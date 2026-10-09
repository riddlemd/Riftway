using Oriel.Cef.Handlers;

namespace Oriel.Tests;

public sealed class TitleBarTests
{
    [Theory]
    [InlineData(true, TitleBarStyle.Default, false, false)]
    [InlineData(false, TitleBarStyle.Default, false, true)]
    [InlineData(true, TitleBarStyle.Hidden, false, true)]
    [InlineData(true, TitleBarStyle.HiddenInset, false, true)]
    [InlineData(true, TitleBarStyle.CustomButtonsOnHover, false, true)]
    [InlineData(true, TitleBarStyle.Hidden, true, false)]
    [InlineData(false, TitleBarStyle.Hidden, true, true)]
    public void IsFrameless_NoFrame_OrHiddenTitleBarOffMac(bool frame, TitleBarStyle style, bool isMacOS, bool frameless)
        => Assert.Equal(frameless, TitleBar.IsFrameless(frame, style, isMacOS));

    [Fact]
    public void HasOverlay_NeedsOptions_AHiddenTitleBar_AndNotMac()
    {
        var options = new TitleBarOverlayOptions();
        Assert.True(TitleBar.HasOverlay(TitleBarStyle.Hidden, options, isMacOS: false));
        Assert.True(TitleBar.HasOverlay(TitleBarStyle.HiddenInset, options, isMacOS: false));
        Assert.False(TitleBar.HasOverlay(TitleBarStyle.Default, options, isMacOS: false));
        Assert.False(TitleBar.HasOverlay(TitleBarStyle.Hidden, null, isMacOS: false));
        Assert.False(TitleBar.HasOverlay(TitleBarStyle.Hidden, options, isMacOS: true));
    }

    [Theory]
    [InlineData(0xFF000000u, 0xFFFFFFFFu, 0.0, 0xFF000000u)]
    [InlineData(0xFF000000u, 0xFFFFFFFFu, 1.0, 0xFFFFFFFFu)]
    [InlineData(0xFF000000u, 0xFFFFFFFFu, 0.1, 0xFF1A1A1Au)]
    [InlineData(0xFF204060u, 0xFFFFFFFFu, 0.5, 0xFF90A0B0u)]
    [InlineData(0xFF102030u, 0xFF000000u, 0.25, 0xFF0C1824u)]
    [InlineData(0x00FF0000u, 0x0000FF00u, 0.5, 0xFF808000u)]
    public void Blend_MixesEachChannel_AndIsOpaque(uint under, uint over, double amount, uint blended)
        => Assert.Equal(blended, TitleBar.Blend(under, over, amount));

    [Theory]
    [InlineData(false, 1.0, 0xFF222222u)]
    [InlineData(true, 0.0, 0xFF424242u)]
    [InlineData(false, 0.0, 0xFF000000u)]
    [InlineData(false, 0.5, 0xFF111111u)]
    public void ButtonFill_OverAColourWithAlpha_TintsWithTheForegroundAt1Point3Times0x1AAnd0x33(bool pressed, double hover, uint fill)
        => Assert.Equal(fill, TitleBar.ButtonFill(0xFF000000, 0xFFFFFFFF, close: false, pressed, hover));

    [Theory]
    [InlineData(false, 1.0, 0x1A000000u)]
    [InlineData(true, 0.0, 0x33000000u)]
    [InlineData(false, 0.0, 0u)]
    public void ButtonFill_OverATransparentColour_TintsAt0x1AAnd0x33(bool pressed, double hover, uint fill)
        => Assert.Equal(fill, TitleBar.ButtonFill(0, 0xFF000000, close: false, pressed, hover));

    [Fact]
    public void ButtonFill_PaintsATranslucentColourAgain_AsElectronsButtonOverItsContainer()
        => Assert.Equal(0x80FF0000u, TitleBar.ButtonFill(0x80FF0000, 0xFF000000, close: false, pressed: false, hover: 0));

    [Theory]
    [InlineData(0xFF000000u, false, 1.0, 0xFFE81123u)]
    [InlineData(0xFF000000u, true, 0.0, 0xFF8A0A15u)]
    [InlineData(0u, false, 1.0, 0xFFE81123u)]
    [InlineData(0u, true, 0.0, 0x98E81123u)]
    [InlineData(0u, false, 0.5, 0x7FE81123u)]
    public void ButtonFill_Close_IsE81123_OpaqueHovered_0x98Pressed(uint color, bool pressed, double hover, uint fill)
        => Assert.Equal(fill, TitleBar.ButtonFill(color, 0xFFFFFFFF, close: true, pressed, hover));

    [Theory]
    [InlineData(0.0, 0, 34, 0)]
    [InlineData(1.0, 0, 34, 34)]
    [InlineData(0.5, 0, 34, 17)]
    [InlineData(0.999, 0, 34, 34)]
    [InlineData(0.99, 0, 255, 253)]
    [InlineData(1.0, 10, 10, 10)]
    [InlineData(1.0, 34, 0, 0)]
    [InlineData(0.5, 34, 0, 17)]
    public void IntValueBetween_TruncatesAgainstOnePastTheEnd(double value, int start, int target, int between)
        => Assert.Equal(between, TitleBar.IntValueBetween(value, start, target));

    [Theory]
    [InlineData(0.0, 0xFF000000u, 0xFFFFFFFFu, 0xFF000000u)]
    [InlineData(1.0, 0xFF000000u, 0xFFFFFFFFu, 0xFFFFFFFFu)]
    [InlineData(0.5, 0xFF000000u, 0xFFFFFFFFu, 0xFF808080u)]
    [InlineData(0.5, 0x00000000u, 0xFFFFFFFFu, 0x80FFFFFFu)]
    [InlineData(0.0, 0x00000000u, 0xFFFFFFFFu, 0u)]
    [InlineData(0.5, 0x00FF0000u, 0xFF000000u, 0x80000000u)]
    [InlineData(0.00196078431372549, 0xFF000000u, 0xFFFFFFFFu, 0xFF010101u)]
    public void ColorValueBetween_BlendsPremultiplied(double value, uint start, uint target, uint blended)
        => Assert.Equal(blended, TitleBar.ColorValueBetween(value, start, target));

    [Theory]
    [InlineData(true, false, true, 0.5, 0xFF808080u)]
    [InlineData(true, true, false, 1.0, 0xFFFFFFFFu)]
    [InlineData(true, false, false, 0.0, 0xFF000000u)]
    [InlineData(false, true, true, 0.5, 0xFF000000u)]
    public void SymbolColor_CloseFadesToWhite_OthersKeepTheirs(bool close, bool hoveredOrPressed, bool animating, double hover, uint color)
        => Assert.Equal(color, TitleBar.SymbolColor(0xFF000000, close, hoveredOrPressed, animating, hover));

    [Fact]
    public void ButtonWidths_Are45_PlusOneDipSpacingAfterTheFirst()
    {
        Assert.Equal([45, 46, 46], [TitleBar.ButtonWidthAt(0), TitleBar.ButtonWidthAt(1), TitleBar.ButtonWidthAt(2)]);
        Assert.Equal([0, 1, 1], [TitleBar.SpacingAt(0), TitleBar.SpacingAt(1), TitleBar.SpacingAt(2)]);
    }

    [Fact]
    public void RenderButton_LeavesTheSpacingClear_FillsTheRest_AndCentresTheSymbolOnTheWhole()
    {
        var b = CaptionGlyphs.RenderButton(CaptionGlyph.Close, 1f, 46, 30, 1, 0xFF102030, 0xFFFFFFFF);
        Assert.Equal((46, 30), (b.Width, b.Height));
        byte[] Px(int x, int y) => b.Bgra[((y * b.Width + x) * 4)..((y * b.Width + x) * 4 + 4)];
        Assert.Equal([0, 0, 0, 0], Px(0, 0));
        Assert.Equal([0x30, 0x20, 0x10, 255], Px(1, 0));
        Assert.Equal([255, 255, 255, 255], Px(18 + 5, 10 + 5));
        Assert.Equal([0x30, 0x20, 0x10, 255], Px(18 + 5, 10));
        var max = CaptionGlyphs.RenderButton(CaptionGlyph.Maximize, 1f, 46, 30, 1, 0xFF102030, 0xFFFFFFFF);
        Assert.Equal([255, 255, 255, 255], max.Bgra[((10 * 46 + 18) * 4)..((10 * 46 + 18) * 4 + 4)]);
        Assert.Equal([0x30, 0x20, 0x10, 255], max.Bgra[((10 * 46 + 17) * 4)..((10 * 46 + 17) * 4 + 4)]);
    }

    [Fact]
    public void RenderButton_ScalesTheSpacing_AndPremultipliesTheFill()
    {
        var b = CaptionGlyphs.RenderButton(CaptionGlyph.Minimize, 2f, 46, 30, 1, 0x80FF0000, 0xFFFFFFFF);
        Assert.Equal((92, 60), (b.Width, b.Height));
        Assert.Equal([0, 0, 0, 0], b.Bgra[4..8]);
        Assert.Equal([0, 0, 128, 128], b.Bgra[8..12]);
        var flush = CaptionGlyphs.RenderButton(CaptionGlyph.Minimize, 1f, 45, 30, 0, 0xFF102030, 0xFFFFFFFF);
        Assert.Equal([0x30, 0x20, 0x10, 255], flush.Bgra[0..4]);
    }

    [Theory]
    [InlineData(0xFF000000u, 0xFFFFFFFFu, 0xFFFFFFFFu)]
    [InlineData(0xFF123456u, 0x00FFFFFFu, 0xFF123456u)]
    [InlineData(0u, 0u, 0u)]
    [InlineData(0u, 0x80FF0000u, 0x80FF0000u)]
    [InlineData(0x80FF0000u, 0x800000FFu, 0xC05500AAu)]
    [InlineData(0xFF000000u, 0x80FFFFFFu, 0xFF808080u)]
    public void Over_ComposesStraightArgbSourceOver(uint under, uint over, uint composed)
        => Assert.Equal(composed, TitleBar.Over(under, over));

    [Theory]
    [InlineData(0xFF808080u, 0xFFFFFFFFu)]
    [InlineData(0xFF818181u, 0xFF000000u)]
    [InlineData(0xFFDEE1E6u, 0xFF000000u)]
    [InlineData(0xFF202124u, 0xFFFFFFFFu)]
    [InlineData(0xFF00D000u, 0xFF000000u)]
    [InlineData(0xFF00CC00u, 0xFFFFFFFFu)]
    [InlineData(0xFFFF00FFu, 0xFFFFFFFFu)]
    public void CaptionForeground_IsWhiteUpToWindowsLuma128_BlackAbove(uint frame, uint foreground)
        => Assert.Equal(foreground, TitleBar.CaptionForeground(frame));

    [Fact]
    public void DefaultFrame_IsChromiumsUnthemedFrame()
    {
        Assert.Equal(0xFF202124u, TitleBar.DefaultFrame(dark: true));
        Assert.Equal(0xFFDEE1E6u, TitleBar.DefaultFrame(dark: false));
    }

    [Fact]
    public void FallbackHeight_IsViewsCaptionButtonHeight() => Assert.Equal(32, TitleBar.FallbackHeight);

    [Fact]
    public void ToWindow_OffsetsEveryRegion_KeepingSizeAndKind()
    {
        DraggableRegion[] regions = [new(0, 0, 100, 40, true), new(5, 5, 30, 30, false)];
        Assert.Equal([new DraggableRegion(0, 26, 100, 40, true), new DraggableRegion(5, 31, 30, 30, false)], TitleBar.ToWindow(regions, 0, 26));
        Assert.Equal([new DraggableRegion(3, 7, 100, 40, true), new DraggableRegion(8, 12, 30, 30, false)], TitleBar.ToWindow(regions, 3, 7));
        Assert.Empty(TitleBar.ToWindow([], 3, 7));
    }

    // Alpha of the glyph pixel at (x, y): byte 3 of its premultiplied BGRA.
    private static int AlphaAt(ImageBitmap b, int x, int y) => b.Bgra[(y * b.Width + x) * 4 + 3];

    [Theory]
    [InlineData(1f, 10)]
    [InlineData(1.5f, 15)]
    [InlineData(2f, 20)]
    [InlineData(1.25f, 12)]
    public void Render_IsTenDipsSquareAtTheScale(float scale, int side)
    {
        var b = CaptionGlyphs.Render(CaptionGlyph.Close, scale, 0xFFFFFFFF);
        Assert.Equal((side, side), (b.Width, b.Height));
        Assert.Equal(side * side * 4, b.Bgra.Length);
    }

    [Fact]
    public void Render_Maximize_IsAnOutline()
    {
        var b = CaptionGlyphs.Render(CaptionGlyph.Maximize, 1f, 0xFFFFFFFF);
        Assert.Equal(255, AlphaAt(b, 0, 0));
        Assert.Equal(255, AlphaAt(b, 9, 5));
        Assert.Equal(255, AlphaAt(b, 5, 9));
        Assert.Equal(0, AlphaAt(b, 5, 5));
        Assert.Equal(0, AlphaAt(b, 2, 2));
    }

    [Fact]
    public void Render_Minimize_IsAHalfCoveredLineAcrossTheMiddle()
    {
        var b = CaptionGlyphs.Render(CaptionGlyph.Minimize, 1f, 0xFFFFFFFF);
        Assert.Equal(128, AlphaAt(b, 0, 4));
        Assert.Equal(128, AlphaAt(b, 9, 5));
        Assert.Equal(0, AlphaAt(b, 5, 2));
        Assert.Equal(0, AlphaAt(b, 5, 8));
    }

    [Fact]
    public void Render_Close_CrossesTheCentre_AndLeavesTheEdgeMiddles()
    {
        var b = CaptionGlyphs.Render(CaptionGlyph.Close, 1f, 0xFFFFFFFF);
        Assert.Equal(255, AlphaAt(b, 5, 5));
        Assert.Equal(255, AlphaAt(b, 0, 0));
        Assert.Equal(255, AlphaAt(b, 9, 0));
        Assert.Equal(0, AlphaAt(b, 5, 0));
        Assert.Equal(0, AlphaAt(b, 0, 5));
    }

    [Fact]
    public void Render_Restore_HasTheFrontSquare_AndTheBackCornerOnly()
    {
        var b = CaptionGlyphs.Render(CaptionGlyph.Restore, 1f, 0xFFFFFFFF);
        Assert.Equal(255, AlphaAt(b, 0, 2));
        Assert.Equal(255, AlphaAt(b, 6, 0));
        Assert.Equal(255, AlphaAt(b, 9, 0));
        Assert.Equal(0, AlphaAt(b, 0, 0));
        Assert.Equal(0, AlphaAt(b, 4, 5));
        Assert.Equal(0, AlphaAt(b, 9, 9));
    }

    [Fact]
    public void Render_PremultipliesTheColour()
    {
        var b = CaptionGlyphs.Render(CaptionGlyph.Close, 1f, 0x80FF4020);
        var i = (5 * b.Width + 5) * 4;
        Assert.Equal([16, 32, 128, 128], b.Bgra[i..(i + 4)]);
    }

    [Theory]
    [InlineData(5, 0, 0, 0, 10, 0, 0)]
    [InlineData(5, 3, 0, 0, 10, 0, 3)]
    [InlineData(13, 4, 0, 0, 10, 0, 5)]
    [InlineData(-3, -4, 0, 0, 10, 0, 5)]
    [InlineData(2, 2, 1, 1, 1, 1, 1.4142135623730951)]
    public void DistanceToSegment_ClampsToTheEnds(double px, double py, double x0, double y0, double x1, double y1, double distance)
        => Assert.Equal(distance, CaptionGlyphs.DistanceToSegment(px, py, x0, y0, x1, y1), 9);
}
