namespace Riftway.Tests;

public sealed class WindowPolishTests
{
    [Theory]
    [InlineData(1, ResizeEdge.Left)]
    [InlineData(2, ResizeEdge.Right)]
    [InlineData(3, ResizeEdge.Top)]
    [InlineData(4, ResizeEdge.TopLeft)]
    [InlineData(5, ResizeEdge.TopRight)]
    [InlineData(6, ResizeEdge.Bottom)]
    [InlineData(7, ResizeEdge.BottomLeft)]
    [InlineData(8, ResizeEdge.BottomRight)]
    [InlineData(0, ResizeEdge.BottomRight)]
    [InlineData(9, ResizeEdge.BottomRight)]
    public void EdgeFromWmsz_MapsWin32Edges_BottomRightOtherwise(int wmsz, ResizeEdge edge)
        => Assert.Equal(edge, WindowPolish.EdgeFromWmsz(wmsz));

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(-0.1, 0.0)]
    [InlineData(1.0001, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    public void ClampOpacity_KeepsItWithinZeroAndOne(double opacity, double clamped)
        => Assert.Equal(clamped, WindowPolish.ClampOpacity(opacity));

    [Fact]
    public void ClampOpacity_NaN_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => WindowPolish.ClampOpacity(double.NaN));

    [Theory]
    [InlineData(1.0, 255)]
    [InlineData(0.5, 127)]
    [InlineData(0.25, 63)]
    [InlineData(0.0, 0)]
    [InlineData(2.0, 255)]
    [InlineData(-1.0, 0)]
    [InlineData(0.999, 254)]
    public void ToAlpha_TruncatesTheClampedOpacityTimes255(double opacity, byte alpha)
        => Assert.Equal(alpha, WindowPolish.ToAlpha(opacity));

    [Fact]
    public void WillResizeEventArgs_CarryBoundsAndEdge_AndCancel()
    {
        var args = new WillResizeEventArgs(new Rectangle(1, 2, 3, 4), ResizeEdge.TopLeft);
        Assert.Equal(new Rectangle(1, 2, 3, 4), args.NewBounds);
        Assert.Equal(ResizeEdge.TopLeft, args.Edge);
        Assert.False(args.Cancel);
        Assert.Equal(new Rectangle(5, 6, 7, 8), new WillMoveEventArgs(new Rectangle(5, 6, 7, 8)).NewBounds);
    }

    [Fact]
    public void Options_DefaultToElectronsValues()
    {
        var options = new BrowserWindowOptions();
        Assert.Equal(1.0, options.Opacity);
        Assert.True(options.HasShadow);
        Assert.False(options.Kiosk);
    }
}
