namespace Oriel.Tests;

public sealed class PopupCancelGateTests
{
    [Fact]
    public void ARequestStandsUntilAClose()
    {
        var gate = new PopupCancelGate();
        var request = gate.Request();
        Assert.False(gate.IsCancelled(request));
        gate.CancelAll();
        Assert.True(gate.IsCancelled(request));
    }

    [Fact]
    public void ACloseCancelsEveryEarlierRequest()
    {
        var gate = new PopupCancelGate();
        var first = gate.Request();
        var second = gate.Request();
        gate.CancelAll();
        Assert.True(gate.IsCancelled(first));
        Assert.True(gate.IsCancelled(second));
    }

    [Fact]
    public void ARequestAfterACloseStands()
    {
        var gate = new PopupCancelGate();
        gate.Request();
        gate.CancelAll();
        var later = gate.Request();
        Assert.False(gate.IsCancelled(later));
    }

    [Fact]
    public void ACloseWithNothingRequestedCancelsNothingLater()
    {
        var gate = new PopupCancelGate();
        gate.CancelAll();
        Assert.False(gate.IsCancelled(gate.Request()));
    }
}
