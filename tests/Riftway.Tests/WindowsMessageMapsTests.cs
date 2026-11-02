namespace Riftway.Tests;

public sealed class WindowsMessageMapsTests
{
    [Theory]
    [InlineData(0x0001_0000L, 1)]
    [InlineData(0x8001_0000L, 1)] // FAPPCOMMAND_MOUSE
    [InlineData(0x1035_0000L, 53)] // FAPPCOMMAND_OEM
    [InlineData(0x0035_1234L, 53)]
    public void AppCommandFromLParam_DropsTheDeviceBits(long lParam, int command)
        => Assert.Equal(command, AppCommands.FromLParam((nint)lParam));

    [Theory]
    [InlineData(1, "browser-backward")]
    [InlineData(2, "browser-forward")]
    [InlineData(7, "browser-home")]
    [InlineData(14, "media-play-pause")]
    [InlineData(26, "microphone-volume-up")]
    [InlineData(43, "dictate-or-command-control-toggle")]
    [InlineData(44, "mic-on-off-toggle")]
    [InlineData(45, "correction-list")]
    [InlineData(53, "delete")]
    [InlineData(0, "unknown")]
    [InlineData(54, "unknown")]
    [InlineData(-1, "unknown")]
    public void AppCommandName_IsElectrons(int command, string name) => Assert.Equal(name, AppCommands.Name(command));

    [Fact]
    public void AppCommandName_CoversEveryCommandElectronNames()
    {
        var names = Enumerable.Range(1, 53).Select(AppCommands.Name).ToArray();
        Assert.DoesNotContain("unknown", names);
        Assert.Equal(53, names.Distinct().Count());
    }

    [Theory]
    [InlineData(0L, new[] { "shutdown" })]
    [InlineData(0x1L, new[] { "close-app" })]
    [InlineData(0x40000000L, new[] { "critical" })]
    [InlineData(0x80000000L, new[] { "logoff" })]
    [InlineData(0xC0000001L, new[] { "close-app", "critical", "logoff" })]
    [InlineData(0x2L, new string[0])]
    public void EndSessionReasons_AreElectrons(long lParam, string[] reasons)
        => Assert.Equal(reasons, EndSessionReasons.From((nint)lParam));

    [Fact]
    public void AspectRatio_IsTheScaledFloatQuotient()
    {
        Assert.Equal(1.5f, AspectRatioSizing.Ratio(1.5));
        Assert.Equal(2f, AspectRatioSizing.Ratio(2));
    }

    public static TheoryData<int, int[], int[]> Edges => new()
    {
        { AspectRatioSizing.Right, [0, 0, 300, 100], [0, 0, 300, 150] },
        { AspectRatioSizing.Bottom, [0, 0, 300, 100], [0, 0, 200, 100] },
        { AspectRatioSizing.BottomRight, [0, 0, 300, 100], [0, 0, 200, 100] },
        { AspectRatioSizing.Left, [100, 100, 400, 300], [100, 150, 400, 300] },
        { AspectRatioSizing.Top, [0, 100, 300, 400], [0, 100, 600, 400] },
        { AspectRatioSizing.TopLeft, [100, 100, 400, 400], [100, 250, 400, 400] },
        { AspectRatioSizing.TopRight, [0, 100, 300, 400], [0, 100, 600, 400] },
        { AspectRatioSizing.BottomLeft, [100, 100, 400, 400], [100, 100, 400, 250] },
    };

    [Theory]
    [MemberData(nameof(Edges))]
    public void Fit_SizesFromTheDraggedEdge(int edge, int[] rect, int[] expected)
        => Assert.Equal(new PixelRect(expected[0], expected[1], expected[2], expected[3]),
            AspectRatioSizing.Fit(edge, 2f, (0, 0), null, (0, 0), new(rect[0], rect[1], rect[2], rect[3])));

    [Fact]
    public void Fit_RoundsHalfAwayFromZero()
        => Assert.Equal(new PixelRect(0, 0, 5, 3), AspectRatioSizing.Fit(AspectRatioSizing.Right, 2f, (0, 0), null, (0, 0), new(0, 0, 5, 5)));

    [Fact]
    public void Fit_HorizontalBelowTheMinimumHeight_TakesTheMinimumAndWidensToMatch()
        => Assert.Equal(new PixelRect(0, 0, 400, 200), AspectRatioSizing.Fit(AspectRatioSizing.Right, 2f, (0, 200), null, (0, 0), new(0, 0, 300, 100)));

    [Fact]
    public void Fit_HorizontalAboveTheMaximumHeight_ClampsAndNarrowsToMatch()
        => Assert.Equal(new PixelRect(0, 0, 200, 100), AspectRatioSizing.Fit(AspectRatioSizing.Right, 2f, (0, 0), (1000, 100), (0, 0), new(0, 0, 300, 100)));

    [Fact]
    public void Fit_VerticalBelowTheMinimumWidth_TakesTheMinimumAndHeightensToMatch()
        => Assert.Equal(new PixelRect(0, 0, 500, 250), AspectRatioSizing.Fit(AspectRatioSizing.Bottom, 2f, (500, 0), null, (0, 0), new(0, 0, 300, 100)));

    [Fact]
    public void Fit_FromTheTop_KeepsTheBottomEdgeWhenTheHeightChanges()
        => Assert.Equal(new PixelRect(0, 0, 800, 400), AspectRatioSizing.Fit(AspectRatioSizing.Top, 2f, (800, 0), null, (0, 0), new(0, 100, 300, 400)));

    [Fact]
    public void Fit_FromTheTopRight_KeepsTheLeftAndBottomEdges()
        => Assert.Equal(new PixelRect(100, 0, 900, 400), AspectRatioSizing.Fit(AspectRatioSizing.TopRight, 2f, (800, 0), null, (0, 0), new(100, 100, 400, 400)));

    [Fact]
    public void Fit_FromTheBottomLeft_KeepsTheRightAndTopEdgesWhenTheWidthChanges()
        => Assert.Equal(new PixelRect(0, 100, 400, 300), AspectRatioSizing.Fit(AspectRatioSizing.BottomLeft, 2f, (0, 200), null, (0, 0), new(100, 100, 400, 250)));

    [Fact]
    public void Fit_VerticalAboveTheMaximumWidth_ClampsAndShortensToMatch()
        => Assert.Equal(new PixelRect(0, 0, 400, 200), AspectRatioSizing.Fit(AspectRatioSizing.Bottom, 2f, (0, 0), (400, 1000), (0, 0), new(0, 0, 300, 300)));

    [Fact]
    public void Fit_WhatCannotFit_IsCappedByTheMaximumAfterwards()
        => Assert.Equal(new PixelRect(0, 0, 100, 150), AspectRatioSizing.Fit(AspectRatioSizing.Right, 2f, (0, 0), (100, 1000), (0, 0), new(0, 0, 300, 100)));

    [Fact]
    public void Fit_WhatCannotFit_IsRaisedToTheMinimumAfterwards()
        => Assert.Equal(new PixelRect(0, 0, 400, 150), AspectRatioSizing.Fit(AspectRatioSizing.Right, 2f, (400, 0), null, (0, 0), new(0, 0, 300, 100)));

    [Fact]
    public void Fit_KeepsTheExcludedMarginOutOfTheRatio()
        => Assert.Equal(new PixelRect(0, 0, 310, 170), AspectRatioSizing.Fit(AspectRatioSizing.Right, 2f, (0, 0), null, (10, 20), new(0, 0, 310, 100)));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void IsEdge_AcceptsWmszValuesOnly(int edge, bool accepted) => Assert.Equal(accepted, AspectRatioSizing.IsEdge(edge));

    [Fact]
    public void Shape_AtScaleOne_IsTakenAsItIs()
        => Assert.Equal([new PixelRect(10, 20, 40, 60)], WindowShape.ToPixels([new Rectangle(10, 20, 30, 40)], 1.0));

    [Fact]
    public void Shape_BelowScaleOne_IsTakenAsItIs()
        => Assert.Equal([new PixelRect(10, 20, 40, 60)], WindowShape.ToPixels([new Rectangle(10, 20, 30, 40)], 0.75));

    [Fact]
    public void Shape_AboveScaleOne_IsScaledAndRoundedOutwards()
        => Assert.Equal([new PixelRect(1, 1, 5, 5), new PixelRect(15, 30, 60, 90)],
            WindowShape.ToPixels([new Rectangle(1, 1, 2, 2), new Rectangle(10, 20, 30, 40)], 1.5));

    [Fact]
    public void MediaSourceId_ForAWindow_IsWindowHandleAndNullAuraId()
        => Assert.Equal("window:1311490:0", MediaSourceId.ForWindow(1311490));

    [Theory]
    [InlineData("window:1311490:0", 1311490L)]
    [InlineData(" window : 42 : 7 ", 42L)]
    [InlineData("window:42:7:s", 42L)]
    [InlineData("window:+42:-1", 42L)]
    [InlineData("window:-5:0", -5L)]
    public void ParseWindow_ReadsTheWindowId(string id, long expected) => Assert.Equal(expected, MediaSourceId.ParseWindow(id));

    [Theory]
    [InlineData("screen:1:0")]
    [InlineData("window:42:7:x")]
    [InlineData("window:42")]
    [InlineData("window:42:7:s:1")]
    [InlineData("window:4x:0")]
    [InlineData("window:42:z")]
    [InlineData("web-contents-media-stream://1:2")]
    [InlineData("")]
    public void ParseWindow_RejectsWhatChromiumDoesNotReadAsAWindow(string id) => Assert.Null(MediaSourceId.ParseWindow(id));
}
