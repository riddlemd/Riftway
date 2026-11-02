namespace Riftway.Tests;

public sealed class ScreenTests
{
    private static readonly Display s_main = new() { Id = 1, Bounds = new(0, 0, 1600, 1000), WorkArea = new(0, 25, 1600, 975), ScaleFactor = 2, Rotation = 0, DisplayFrequency = 60 };
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
    [InlineData(nameof(Display.HdrMaxLuminanceRelative))]
    public void Between_ColourSpaceChange_RaisesMetricsChangedWithNoNamedMetric(string field)
    {
        var after = field switch
        {
            nameof(Display.ColorSpace) => s_main with { ColorSpace = DisplayMapping.HdrRaster },
            nameof(Display.ColorDepth) => s_main with { ColorDepth = 30 },
            nameof(Display.ColorSpacesKey) => s_main with { ColorSpacesKey = "240 1000" },
            _ => s_main with { HdrMaxLuminanceRelative = 2 },
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

    [Theory]
    [InlineData(60.02, true, true)]
    [InlineData(60.005, true, false)]
    [InlineData(120, false, false)]
    public void Between_ARefreshRateChangeCountsOnMacBeyondTheEpsilon(double rate, bool compareRefreshRate, bool reported)
    {
        var events = DisplayChanges.Between([s_main], [s_main with { DisplayFrequency = rate }], compareRefreshRate);
        Assert.Equal(reported ? [new(DisplayChangeKind.MetricsChanged, s_main with { DisplayFrequency = rate }, DisplayMetrics.None)] : [], events);
    }

    // XRandR's first event code on a typical server; RRNotify is one past it.
    private const int RandRBase = 89;
    private const nint WorkArea = 300;

    [Theory]
    [InlineData(RandRBase + 1, 0, true)]   // RRNotify: a CRTC, output or output property change
    [InlineData(RandRBase, 0, false)]      // RRScreenChangeNotify alone, which XDisplayManager::OnEvent passes over
    [InlineData(28, 300, true)]            // PropertyNotify for _NET_WORKAREA
    [InlineData(28, 301, false)]           // PropertyNotify for another root property
    [InlineData(22, 300, false)]           // ConfigureNotify, even with a matching atom field
    [InlineData(RandRBase + 2, 0, false)]  // past the RandR events
    public void UpdatesDisplays_TheEventsXDisplayManagerUpdatesOn(int type, int atom, bool expected)
        => Assert.Equal(expected, Riftway.Cef.Platform.X11DisplayRules.UpdatesDisplays(type, atom, RandRBase, WorkArea));

    [Theory]
    [InlineData(1, 3, true)]
    [InlineData(1, 6, true)]
    [InlineData(2, 0, true)]
    [InlineData(1, 2, false)]
    [InlineData(0, 9, false)]
    public void RandRUsable_FromVersion13(int major, int minor, bool expected)
        => Assert.Equal(expected, Riftway.Cef.Platform.X11DisplayRules.RandRUsable(major, minor));

    [Fact]
    public void Between_OffMac_ARefreshRateChangeAloneRaisesNothing_ButAColourSpaceChangeStillDoes()
    {
        Assert.Empty(DisplayChanges.Between([s_side], [s_side with { DisplayFrequency = 75 }]));
        var recoloured = s_side with { ColorSpace = DisplayMapping.HdrRaster, DisplayFrequency = 75 };
        Assert.Equal([new(DisplayChangeKind.MetricsChanged, recoloured, DisplayMetrics.None)], DisplayChanges.Between([s_side], [recoloured]));
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
