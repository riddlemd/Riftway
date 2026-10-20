namespace Oriel.Tests;

public sealed class UnresponsiveScheduleTests
{
    private readonly List<(Action Run, TimeSpan Delay)> _posted = [];
    private bool _canRaise = true;
    private int _raised;

    private UnresponsiveSchedule Schedule() => new((run, delay) => _posted.Add((run, delay)), () => _canRaise, () => _raised++);

    [Fact]
    public void AHangRaisesAfterFiftyMilliseconds()
    {
        var schedule = Schedule();
        schedule.PageUnresponsive();
        Assert.Equal(TimeSpan.FromMilliseconds(50), Assert.Single(_posted).Delay);
        Assert.Equal(0, _raised);
        _posted[0].Run();
        Assert.Equal(1, _raised);
    }

    [Fact]
    public void AProceedingCloseRaisesAfterFiveSeconds()
    {
        var schedule = Schedule();
        schedule.CloseProceeding();
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(_posted).Delay);
        _posted[0].Run();
        Assert.Equal(1, _raised);
    }

    [Fact]
    public void OnlyOneTimerRunsAtATime()
    {
        var schedule = Schedule();
        schedule.PageUnresponsive();
        schedule.CloseProceeding();
        Assert.Single(_posted);
        _posted[0].Run();
        schedule.CloseProceeding();
        Assert.Equal(2, _posted.Count);
    }

    [Fact]
    public void ResponsiveCancelsTheTimer()
    {
        var schedule = Schedule();
        schedule.PageUnresponsive();
        schedule.PageResponsive();
        _posted[0].Run();
        Assert.Equal(0, _raised);
    }

    [Fact]
    public void DestroyedCancelsTheTimer()
    {
        var schedule = Schedule();
        schedule.CloseProceeding();
        schedule.PageDestroyed();
        _posted[0].Run();
        Assert.Equal(0, _raised);
    }

    [Fact]
    public void AStaleTimerDoesNotEndTheNextOne()
    {
        var schedule = Schedule();
        schedule.PageUnresponsive();
        schedule.PageResponsive();
        schedule.PageUnresponsive();
        _posted[0].Run();
        Assert.Equal(0, _raised);
        schedule.PageUnresponsive();
        Assert.Equal(2, _posted.Count);
        _posted[1].Run();
        Assert.Equal(1, _raised);
    }

    [Fact]
    public void ADisabledOrDestroyedWindowIsNotReported()
    {
        var schedule = Schedule();
        _canRaise = false;
        schedule.PageUnresponsive();
        _posted[0].Run();
        Assert.Equal(0, _raised);
        schedule.PageUnresponsive();
        Assert.Equal(2, _posted.Count);
    }
}
