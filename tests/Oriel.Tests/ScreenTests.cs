namespace Oriel.Tests;

public sealed class ScreenTests
{
    private static readonly Display s_main = new() { Id = 1, Bounds = new(0, 0, 1600, 1000), WorkArea = new(0, 25, 1600, 975), ScaleFactor = 2, Rotation = 0 };
    private static readonly Display s_side = new() { Id = 2, Bounds = new(1600, 0, 1920, 1080), WorkArea = new(1600, 0, 1920, 1080), ScaleFactor = 1 };

    [Fact]
    public void Compare_NothingChanged()
    {
        var (added, removed, changed) = Screen.Compare([s_main, s_side], [s_side, s_main]);
        Assert.Empty(added);
        Assert.Empty(removed);
        Assert.Empty(changed);
    }

    [Fact]
    public void Compare_FindsAddedAndRemovedById()
    {
        var replacement = s_side with { Id = 3 };
        var (added, removed, changed) = Screen.Compare([s_main, s_side], [s_main, replacement]);
        Assert.Equal([replacement], added);
        Assert.Equal([s_side], removed);
        Assert.Empty(changed);
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
        var (_, _, changed) = Screen.Compare([s_main], [after]);
        Assert.Equal([(after, expected)], changed);
    }

    [Fact]
    public void Compare_ReportsSeveralMetricsAtOnce()
    {
        var after = s_main with { Bounds = new(0, 0, 1280, 800), ScaleFactor = 1 };
        var (_, _, changed) = Screen.Compare([s_main], [after]);
        Assert.Equal(DisplayMetrics.Bounds | DisplayMetrics.ScaleFactor, Assert.Single(changed).Metrics);
    }
}
