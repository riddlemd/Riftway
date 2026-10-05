namespace Oriel.Tests;

public sealed class ScreenTests
{
    private static readonly Display s_main = new() { Id = 1, Bounds = new(0, 0, 1600, 1000), WorkArea = new(0, 25, 1600, 975), ScaleFactor = 2, Rotation = 0 };
    private static readonly Display s_side = new() { Id = 2, Bounds = new(1600, 0, 1920, 1080), WorkArea = new(1600, 0, 1920, 1080), ScaleFactor = 1 };

    [Fact]
    public void Between_NothingChanged()
    {
        Assert.Empty(DisplayChanges.Between([s_main, s_side], [s_side, s_main]));
    }

    [Fact]
    public void Between_FindsAddedAndRemovedById_RemovedFirst()
    {
        var replacement = s_side with { Id = 3 };
        Assert.Equal(
            [new(DisplayChangeKind.Removed, s_side, DisplayMetrics.None), new(DisplayChangeKind.Added, replacement, DisplayMetrics.None)],
            DisplayChanges.Between([s_main, s_side], [s_main, replacement]));
    }

    [Fact]
    public void Between_FollowsChromiumOrder_RemovedThenEachCurrentDisplayInTurn()
    {
        var third = s_side with { Id = 3 };
        var added = s_side with { Id = 4 };
        var moved = s_main with { Bounds = new(10, 0, 1600, 1000) };
        var changes = DisplayChanges.Between([s_main, s_side, third], [added, moved]);
        Assert.Equal(
            [
                new(DisplayChangeKind.Removed, s_side, DisplayMetrics.None),
                new(DisplayChangeKind.Removed, third, DisplayMetrics.None),
                new(DisplayChangeKind.Added, added, DisplayMetrics.None),
                new(DisplayChangeKind.MetricsChanged, moved, DisplayMetrics.Bounds),
            ],
            changes);
    }

    [Theory]
    [InlineData(nameof(Display.Bounds), DisplayMetrics.Bounds)]
    [InlineData(nameof(Display.WorkArea), DisplayMetrics.WorkArea)]
    [InlineData(nameof(Display.ScaleFactor), DisplayMetrics.ScaleFactor)]
    [InlineData(nameof(Display.Rotation), DisplayMetrics.Rotation)]
    public void Between_NamesTheMetricThatChanged(string metric, DisplayMetrics expected)
    {
        var after = metric switch
        {
            nameof(Display.Bounds) => s_main with { Bounds = new(0, 0, 1280, 800) },
            nameof(Display.WorkArea) => s_main with { WorkArea = new(0, 0, 1600, 1000) },
            nameof(Display.ScaleFactor) => s_main with { ScaleFactor = 1 },
            _ => s_main with { Rotation = 90 },
        };
        Assert.Equal([new(DisplayChangeKind.MetricsChanged, after, expected)], DisplayChanges.Between([s_main], [after]));
    }

    [Fact]
    public void Between_ReportsSeveralMetricsAtOnce()
    {
        var after = s_main with { Bounds = new(0, 0, 1280, 800), ScaleFactor = 1 };
        Assert.Equal(DisplayMetrics.Bounds | DisplayMetrics.ScaleFactor, Assert.Single(DisplayChanges.Between([s_main], [after])).Metrics);
    }

    [Theory]
    [InlineData(nameof(Display.ColorSpace))]
    [InlineData(nameof(Display.ColorDepth))]
    [InlineData(nameof(Display.ColorSpacesKey))]
    public void Between_ColourSpaceChange_RaisesMetricsChangedWithNoNamedMetric(string field)
    {
        var after = field switch
        {
            nameof(Display.ColorSpace) => s_main with { ColorSpace = DisplayMapping.HdrRaster },
            nameof(Display.ColorDepth) => s_main with { ColorDepth = 30 },
            _ => s_main with { ColorSpacesKey = "240 1000" },
        };
        Assert.Equal([new(DisplayChangeKind.MetricsChanged, after, DisplayMetrics.None)], DisplayChanges.Between([s_main], [after]));
    }

    [Fact]
    public void Between_IgnoresFieldsChromiumDoesNotCompare()
    {
        var after = s_main with { Label = "Renamed", DisplayFrequency = 144, Internal = true, NativeOrigin = new(5, 5), DepthPerComponent = 10 };
        Assert.Empty(DisplayChanges.Between([s_main], [after]));
    }

    [Theory]
    [InlineData(0x007Eu, 0, true)]   // WM_DISPLAYCHANGE
    [InlineData(0x001Cu, 1, true)]   // WM_ACTIVATEAPP, activating
    [InlineData(0x001Cu, 0, false)]  // WM_ACTIVATEAPP, deactivating
    [InlineData(0x001Au, 0x2F, true)] // WM_SETTINGCHANGE, SPI_SETWORKAREA
    [InlineData(0x001Au, 0x14, false)] // WM_SETTINGCHANGE, SPI_SETDESKWALLPAPER
    [InlineData(0x001Au, 0, false)]  // WM_SETTINGCHANGE for a policy or locale
    [InlineData(0x02E0u, 0x00780078, true)] // WM_DPICHANGED
    [InlineData(0x0005u, 0, false)]  // WM_SIZE
    public void ReadsDisplays_TheMessagesScreenWinUpdatesOn(uint message, int wParam, bool expected)
    {
        Assert.Equal(expected, Screen.ReadsDisplays(message, wParam));
    }

    [Fact]
    public void Display_DefaultsAreChromiumsDisplayDefaults()
    {
        var display = new Display();
        Assert.Equal(("", false, 0.0, 24, 8), (display.Label, display.Internal, display.DisplayFrequency, display.ColorDepth, display.DepthPerComponent));
        Assert.Equal("{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}", display.ColorSpace);
        Assert.Equal((false, DisplayFeatureSupport.Unknown, DisplayFeatureSupport.Unknown, true), (display.Monochrome, display.TouchSupport, display.AccelerometerSupport, display.Detected));
        Assert.Equal((new Point(0, 0), (0, 0)), (display.NativeOrigin, display.MaximumCursorSize));
    }
}
