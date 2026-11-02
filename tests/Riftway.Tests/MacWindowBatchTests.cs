using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Electron v44's NativeWindow(Mac)::IsNormal and ElectronNSWindowDelegate window:willPositionSheet:usingRect:.
public sealed class MacWindowBatchTests
{
    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    public void NormalIsNoneOfTheFourStates(bool minimized, bool maximized, bool fullScreen, bool simple, bool normal)
        => Assert.Equal(normal, WindowStateRules.IsNormal(minimized, maximized, fullScreen, simple));

    [Fact]
    public void ASheetStartsAtTheOffsets_DownFromTheContentsTop()
        => Assert.Equal((30.0, 600.0 - 40), MacSheet.Position((30, 40), contentHeight: 600));

    [Fact]
    public void WithoutOffsetsTheSheetHangsFromTheContentsTop()
        => Assert.Equal((0.0, 572.0), MacSheet.Position((0, 0), contentHeight: 572));
}

// Electron v44's ElectronNSWindow constrainFrameRect:toScreen: with enableLargerThanScreen.
public sealed class MacLargerThanScreenTests
{
    private static readonly (double, double, double, double) Asked = (-50, 900, 4000, 3000);
    private static readonly (double, double, double, double) Constrained = (0, 25, 1440, 875);

    [Fact]
    public void AFramedWindowKeepsAppKitsPlace_AndTheAskedSize()
        => Assert.Equal((0.0, 25.0, 4000.0, 3000.0), MacLargerThanScreen.Constrain(Asked, Constrained, hasFrame: true));

    [Fact]
    public void AFramelessWindowGoesExactlyWhereItWasAsked()
        => Assert.Equal(Asked, MacLargerThanScreen.Constrain(Asked, Constrained, hasFrame: false));
}

// Electron v44's windowWillUseStandardFrame:defaultFrame: with zoomToPageWidth.
public sealed class MacZoomTests
{
    private static readonly (double, double, double, double) Screen = (0, 25, 1440, 875);

    [Fact]
    public void WithoutAPreferredWidthTheWindowKeepsItsWidthAndPlace_AndTakesTheScreensHeight()
        => Assert.Equal((100.0, 25.0, 800.0, 875.0), MacZoom.StandardFrame(Screen, (100, 800), preferredWidth: 0));

    [Fact]
    public void APreferredWidthWidensTheWindowInPlace()
        => Assert.Equal((100.0, 25.0, 1000.0, 875.0), MacZoom.StandardFrame(Screen, (100, 800), preferredWidth: 1000));

    [Fact]
    public void AWindowNeverShrinks()
        => Assert.Equal((100.0, 25.0, 800.0, 875.0), MacZoom.StandardFrame(Screen, (100, 800), preferredWidth: 300));

    [Fact]
    public void AWindowThatWouldOverflowMovesLeftJustEnough()
        => Assert.Equal((1440.0 - 1000, 25.0, 1000.0, 875.0), MacZoom.StandardFrame(Screen, (600, 800), preferredWidth: 1000));

    [Fact]
    public void ExactlyFillingTheRightEdgeDoesNotMove()
        => Assert.Equal((440.0, 25.0, 1000.0, 875.0), MacZoom.StandardFrame(Screen, (440, 800), preferredWidth: 1000));
}
