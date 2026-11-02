namespace Riftway.Tests;

public sealed class MenuPopupsTests
{
    private readonly object _a = new(), _b = new();

    [Fact]
    public void WithoutAWindowEveryWindowShowingTheMenuIsATarget()
    {
        var popups = new MenuPopups<object>();
        popups.Opened(_a);
        popups.Opened(_b);
        Assert.Equal([_a, _b], popups.Targets(null));
    }

    [Fact]
    public void AWindowIsATargetOnlyWhereTheMenuIsShowing()
    {
        var popups = new MenuPopups<object>();
        popups.Opened(_a);
        Assert.Equal([_a], popups.Targets(_a));
        Assert.Empty(popups.Targets(_b));
    }

    [Fact]
    public void AClosedPopupIsNoLongerATarget()
    {
        var popups = new MenuPopups<object>();
        var first = popups.Opened(_a);
        popups.Opened(_b);
        popups.Closed(first);
        Assert.Equal([_b], popups.Targets(null));
    }

    [Fact]
    public void TwoPopupsInOneWindowNameItOnceAndEachClosesAlone()
    {
        var popups = new MenuPopups<object>();
        var first = popups.Opened(_a);
        popups.Opened(_a);
        Assert.Equal([_a], popups.Targets(null));
        popups.Closed(first);
        Assert.Equal([_a], popups.Targets(_a));
    }
}
