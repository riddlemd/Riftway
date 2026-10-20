using Oriel.Cef.Platform;

namespace Oriel.Tests;

// Electron v44's ElectronNSWindowDelegate windowWillResize:toSize:.
public sealed class MacWillResizeTests
{
    private const double Unlimited = float.MaxValue;

    // A framed 400 × 330 window: a 30-point title bar over 400 × 300 of content.
    private static MacResizeGeometry Framed(double minW = 0, double minH = 30, double maxW = Unlimited, double maxH = Unlimited)
        => new(0, 0, 400, 330, 400, 300, minW, minH, maxW, maxH);

    [Fact]
    public void TheHeightDecidesTheWidth()
        => Assert.Equal((500.0, 280.0), MacWillResize.Constrain(600, 280, 2, Framed(), (0, 0), hasFrame: true));

    [Fact]
    public void TheExtraSizeIsOutsideTheRatio()
        => Assert.Equal((420.0, 240.0), MacWillResize.Constrain(900, 240, 2, Framed(), (20, 10), hasFrame: true));

    [Fact]
    public void HalvesRoundAwayFromZero()
        => Assert.Equal((107.0, 101.0), MacWillResize.Constrain(0, 101, 1.5, new(0, 0, 300, 330, 300, 300, 0, 30, Unlimited, Unlimited), (0, 0), hasFrame: true));

    // AppKit proposes fractional sizes; the height comes back from the rounded width.
    [Fact]
    public void TheHeightIsRecomputedFromTheRoundedWidth()
        => Assert.Equal((106.0, 101.0), MacWillResize.Constrain(0, 100.4, 1.5, new(0, 0, 300, 330, 300, 300, 0, 30, Unlimited, Unlimited), (0, 0), hasFrame: true));

    [Fact]
    public void TheMinimumWidthHoldsTheRatio()
        => Assert.Equal((450.0, 255.0), MacWillResize.Constrain(0, 230, 2, Framed(minW: 450), (0, 0), hasFrame: true));

    [Fact]
    public void TheMinimumHeightHoldsTheRatio()
        => Assert.Equal((410.0, 235.0), MacWillResize.Constrain(0, 200, 2, Framed(minH: 235), (0, 0), hasFrame: true));

    [Fact]
    public void TheMaximumWidthHoldsTheRatio()
        => Assert.Equal((600.0, 330.0), MacWillResize.Constrain(0, 380, 2, Framed(maxW: 600, maxH: 400), (0, 0), hasFrame: true));

    [Fact]
    public void TheMaximumHeightHoldsTheRatio()
        => Assert.Equal((500.0, 280.0), MacWillResize.Constrain(0, 380, 2, Framed(maxW: Unlimited, maxH: 280), (0, 0), hasFrame: true));

    // A minimum equal to the title bar is no limit for a framed window, but is one for a frameless window.
    [Fact]
    public void OnlyAFramedWindowTakesTheTitleBarAsItsFloor()
    {
        Assert.Equal((-10.0, 25.0), MacWillResize.Constrain(0, 25, 2, Framed(), (0, 0), hasFrame: true));
        Assert.Equal((0.0, 30.0), MacWillResize.Constrain(0, 25, 2, Framed(), (0, 0), hasFrame: false));
    }

    [Theory]
    [InlineData(450, 335, true)]
    [InlineData(402, 360, false)]
    [InlineData(410, 340, false)]
    public void TheDragIsHorizontalWhenTheWidthMovedMore(double width, double height, bool horizontal)
        => Assert.Equal(horizontal, MacWillResize.Horizontal(width, height, Framed()));
}
