using Change = Oriel.Screen.DisplayChange;

namespace Oriel.Tests;

public sealed class ScreenTests
{
    private static readonly Display s_main = new() { Id = 1, Bounds = new(0, 0, 1600, 1000), WorkArea = new(0, 25, 1600, 975), ScaleFactor = 2, Rotation = 0, DisplayFrequency = 60 };
    private static readonly Display s_side = new() { Id = 2, Bounds = new(1600, 0, 1920, 1080), WorkArea = new(1600, 0, 1920, 1080), ScaleFactor = 1 };

    [Fact]
    public void Compare_NothingChanged()
        => Assert.Empty(Screen.Compare([s_main, s_side], [s_side, s_main], compareRefreshRate: true));

    [Fact]
    public void Compare_FindsAddedAndRemovedById()
    {
        var replacement = s_side with { Id = 3 };
        Assert.Equal([(Change.Removed, s_side, DisplayMetrics.None), (Change.Added, replacement, DisplayMetrics.None)],
            Screen.Compare([s_main, s_side], [s_main, replacement], compareRefreshRate: true));
    }

    [Fact]
    public void Compare_ReportsRemovalsFirst_ThenEachCurrentDisplayInOrder()
    {
        // DisplayChangeNotifier: every removal, then added or changed per display in the new list's order.
        var added = s_side with { Id = 3 };
        var moved = s_main with { Bounds = new(0, 0, 1280, 800) };
        Assert.Equal([Change.Removed, Change.Added, Change.MetricsChanged],
            Screen.Compare([s_main, s_side], [added, moved], compareRefreshRate: true).Select(e => e.Change));
    }

    [Theory]
    [InlineData(nameof(Display.Bounds), DisplayMetrics.Bounds)]
    [InlineData(nameof(Display.WorkArea), DisplayMetrics.WorkArea)]
    [InlineData(nameof(Display.ScaleFactor), DisplayMetrics.ScaleFactor)]
    [InlineData(nameof(Display.Rotation), DisplayMetrics.Rotation)]
    public void Compare_NamesTheMetricThatChanged(string metric, DisplayMetrics expected)
    {
        var after = metric switch
        {
            nameof(Display.Bounds) => s_main with { Bounds = new(0, 0, 1280, 800) },
            nameof(Display.WorkArea) => s_main with { WorkArea = new(0, 0, 1600, 1000) },
            nameof(Display.ScaleFactor) => s_main with { ScaleFactor = 1 },
            _ => s_main with { Rotation = 90 },
        };
        Assert.Equal([(Change.MetricsChanged, after, expected)], Screen.Compare([s_main], [after], compareRefreshRate: true));
    }

    [Fact]
    public void Compare_ReportsSeveralMetricsAtOnce()
    {
        var after = s_main with { Bounds = new(0, 0, 1280, 800), ScaleFactor = 1 };
        Assert.Equal(DisplayMetrics.Bounds | DisplayMetrics.ScaleFactor, Assert.Single(Screen.Compare([s_main], [after], compareRefreshRate: true)).Metrics);
    }

    [Theory]
    [InlineData(nameof(Display.ColorSpace))]
    [InlineData(nameof(Display.ColorSpacesKey))]
    [InlineData(nameof(Display.HdrMaxLuminanceRelative))]
    public void Compare_AColorSpaceChangeIsAMetricsChangeNamingNothing(string what)
    {
        // Electron emits display-metrics-changed with an empty changedMetrics for DISPLAY_METRIC_COLOR_SPACE.
        var after = what switch
        {
            nameof(Display.ColorSpace) => s_main with { ColorSpace = "{primaries:P3, transfer:SRGB, matrix:RGB, range:FULL}" },
            nameof(Display.ColorSpacesKey) => s_main with { ColorSpacesKey = "|hdr" },
            _ => s_main with { HdrMaxLuminanceRelative = 2 },
        };
        Assert.Equal([(Change.MetricsChanged, after, DisplayMetrics.None)], Screen.Compare([s_main], [after], compareRefreshRate: false));
    }

    [Theory]
    [InlineData(60.02, true, true)]
    [InlineData(60.005, true, false)]
    [InlineData(120, false, false)]
    public void Compare_ARefreshRateChangeCountsOnMacBeyondTheEpsilon(double rate, bool compareRefreshRate, bool reported)
    {
        var events = Screen.Compare([s_main], [s_main with { DisplayFrequency = rate }], compareRefreshRate);
        Assert.Equal(reported ? [(Change.MetricsChanged, s_main with { DisplayFrequency = rate }, DisplayMetrics.None)] : [], events);
    }

    [Fact]
    public void OnlyHeadroomDiffers_WhenHeadroomAloneChanged()
        => Assert.True(Screen.OnlyHeadroomDiffers([s_main, s_side], [s_main with { HdrMaxLuminanceRelative = 1.5f }, s_side]));

    [Fact]
    public void OnlyHeadroomDiffers_NotWhenNothingChanged()
        => Assert.False(Screen.OnlyHeadroomDiffers([s_main], [s_main]));

    [Fact]
    public void OnlyHeadroomDiffers_NotWhenSomethingElseChangedToo()
        => Assert.False(Screen.OnlyHeadroomDiffers([s_main], [s_main with { HdrMaxLuminanceRelative = 1.5f, ScaleFactor = 1 }]));

    [Fact]
    public void OnlyHeadroomDiffers_NotWhenTheCountChanged()
        => Assert.False(Screen.OnlyHeadroomDiffers([s_main], [s_main with { HdrMaxLuminanceRelative = 1.5f }, s_side]));

    [Fact]
    public void Display_DefaultsAreChromiumsUnsetValues()
    {
        // display.h's member initializers; gfx_converter.cc prints the enums as 'unknown'.
        var display = new Display();
        Assert.Equal(("", false, false, 24, 8, 0.0), (display.Label, display.Internal, display.Monochrome, display.ColorDepth, display.DepthPerComponent, display.DisplayFrequency));
        Assert.Equal((DisplayFeatureSupport.Unknown, DisplayFeatureSupport.Unknown, true), (display.TouchSupport, display.AccelerometerSupport, display.Detected));
        Assert.Equal((new Point(0, 0), (0, 0)), (display.NativeOrigin, display.MaximumCursorSize));
        Assert.Equal("{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}", display.ColorSpace);
    }
}
