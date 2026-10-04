using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class ProgressBarTests
{
    [Theory]
    [InlineData(0.0, ProgressBarMode.Normal, TaskbarProgressState.Normal, 0)]
    [InlineData(0.5, ProgressBarMode.Normal, TaskbarProgressState.Normal, 50)]
    [InlineData(1.0, ProgressBarMode.Normal, TaskbarProgressState.Normal, 100)]
    [InlineData(0.257, ProgressBarMode.Normal, TaskbarProgressState.Normal, 25)]
    [InlineData(0.4, ProgressBarMode.Error, TaskbarProgressState.Error, 40)]
    [InlineData(0.6, ProgressBarMode.Paused, TaskbarProgressState.Paused, 60)]
    public void Map_ShowsTheModeAtThePercentage(double progress, ProgressBarMode mode, TaskbarProgressState state, int percent)
        => Assert.Equal((state, (int?)percent), ProgressBar.Map(progress, mode));

    [Theory]
    [InlineData(1.0001, ProgressBarMode.Normal)]
    [InlineData(2.0, ProgressBarMode.Error)]
    [InlineData(5.0, ProgressBarMode.None)]
    [InlineData(0.3, ProgressBarMode.Indeterminate)]
    [InlineData(-1.0, ProgressBarMode.Indeterminate)]
    public void Map_AboveOneOrIndeterminate_Cycles(double progress, ProgressBarMode mode)
        => Assert.Equal((TaskbarProgressState.Indeterminate, (int?)null), ProgressBar.Map(progress, mode));

    [Theory]
    [InlineData(-0.0001, ProgressBarMode.Normal)]
    [InlineData(-1.0, ProgressBarMode.Paused)]
    [InlineData(0.5, ProgressBarMode.None)]
    [InlineData(double.NaN, ProgressBarMode.Normal)]
    public void Map_BelowZeroNoneOrNaN_RemovesTheBar(double progress, ProgressBarMode mode)
        => Assert.Equal((TaskbarProgressState.None, (int?)null), ProgressBar.Map(progress, mode));

    [Fact]
    public void States_AreWin32TbpfFlags()
    {
        Assert.Equal(0, (int)TaskbarProgressState.None);
        Assert.Equal(1, (int)TaskbarProgressState.Indeterminate);
        Assert.Equal(2, (int)TaskbarProgressState.Normal);
        Assert.Equal(4, (int)TaskbarProgressState.Error);
        Assert.Equal(8, (int)TaskbarProgressState.Paused);
    }
}
