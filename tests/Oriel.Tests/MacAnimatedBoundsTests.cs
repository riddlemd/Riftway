using System.Drawing;

namespace Oriel.Tests;

// Electron v44's NativeWindowMac::SetBounds: the size limits setFrame: ignores, and nothing in full screen.
public sealed class MacAnimatedBoundsTests
{
    private static readonly Rectangle Bounds = new(10, 20, 300, 200);

    [Fact]
    public void WithoutLimitsTheBoundsPassThrough()
        => Assert.Equal(Bounds, MacAnimatedBounds.Frame(Bounds, Size.Empty, Size.Empty, fullScreen: false));

    [Fact]
    public void InFullScreenNothingHappens()
        => Assert.Null(MacAnimatedBounds.Frame(Bounds, Size.Empty, Size.Empty, fullScreen: true));

    [Fact]
    public void TheSizeIsRaisedToTheMinimum_KeepingThePosition()
        => Assert.Equal(new Rectangle(10, 20, 400, 250), MacAnimatedBounds.Frame(Bounds, new Size(400, 250), Size.Empty, fullScreen: false));

    [Fact]
    public void EachSideIsRaisedOnItsOwn()
        => Assert.Equal(new Rectangle(10, 20, 300, 250), MacAnimatedBounds.Frame(Bounds, new Size(100, 250), Size.Empty, fullScreen: false));

    [Fact]
    public void TheSizeIsLoweredToTheMaximum()
        => Assert.Equal(new Rectangle(10, 20, 250, 150), MacAnimatedBounds.Frame(Bounds, Size.Empty, new Size(250, 150), fullScreen: false));

    [Fact]
    public void EachSideIsLoweredOnItsOwn()
        => Assert.Equal(new Rectangle(10, 20, 300, 150), MacAnimatedBounds.Frame(Bounds, Size.Empty, new Size(500, 150), fullScreen: false));

    [Theory]
    [InlineData(250, 0)]
    [InlineData(0, 150)]
    public void AMaximumWithAZeroSideIsNoLimit(int width, int height)
        => Assert.Equal(Bounds, MacAnimatedBounds.Frame(Bounds, Size.Empty, new Size(width, height), fullScreen: false));

    [Fact]
    public void TheMaximumWinsOverTheMinimum()
        => Assert.Equal(new Rectangle(10, 20, 250, 150), MacAnimatedBounds.Frame(Bounds, new Size(400, 400), new Size(250, 150), fullScreen: false));
}
