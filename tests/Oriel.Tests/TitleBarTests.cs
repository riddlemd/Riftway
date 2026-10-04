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

    [Fact]
    public void ButtonBackgrounds_TintTowardsTheSymbol_CloseTurnsRed()
    {
        Assert.Equal((0xFF000000u, 0xFF1A1A1Au, 0xFF333333u), TitleBar.ButtonBackgrounds(0xFF000000, 0xFFFFFFFF, close: false));
        Assert.Equal((0xFF000000u, TitleBar.CloseHover, 0xFFB02719u), TitleBar.ButtonBackgrounds(0xFF000000, 0xFFFFFFFF, close: true));
        Assert.Equal(0xFFC42B1Cu, TitleBar.CloseHover);
    }

    [Fact]
    public void ToWindow_OffsetsEveryRegion_KeepingSizeAndKind()
    {
        DraggableRegion[] regions = [new(0, 0, 100, 40, true), new(5, 5, 30, 30, false)];
        Assert.Equal([new DraggableRegion(0, 26, 100, 40, true), new DraggableRegion(5, 31, 30, 30, false)], TitleBar.ToWindow(regions, 0, 26));
        Assert.Equal([new DraggableRegion(3, 7, 100, 40, true), new DraggableRegion(8, 12, 30, 30, false)], TitleBar.ToWindow(regions, 3, 7));
        Assert.Empty(TitleBar.ToWindow([], 3, 7));
    }

    [Fact]
    public void ButtonWidth_IsWindowsCaptionButtonWidth() => Assert.Equal(46, TitleBar.ButtonWidth);

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
