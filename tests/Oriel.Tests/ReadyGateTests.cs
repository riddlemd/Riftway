using System.Text.Json.Nodes;

namespace Oriel.Tests;

// Electron's ready on macOS comes from applicationDidFinishLaunching:, after CEF's context is ready (drift A23).
public sealed class ReadyGateTests
{
    private readonly List<string> _raised = [];

    private ReadyGate Gate() => new(info => _raised.Add(info.ToJsonString()));

    [Fact]
    public void OffMacReadyIsRaisedWithTheContext()
    {
        var gate = Gate();
        gate.ContextReady(waitForLaunch: false);
        Assert.Equal(["{}"], _raised);
    }

    [Fact]
    public void OnMacReadyWaitsForTheLaunch_AndCarriesItsInfo()
    {
        var gate = Gate();
        gate.ContextReady(waitForLaunch: true);
        Assert.Empty(_raised);
        gate.FinishedLaunching(new JsonObject { ["identifier"] = "n1" });
        Assert.Equal(["""{"identifier":"n1"}"""], _raised);
    }

    [Fact]
    public void ReadyIsRaisedOnce()
    {
        var gate = Gate();
        gate.ContextReady(waitForLaunch: false);
        gate.FinishedLaunching(new JsonObject { ["x"] = 1 });
        gate.ContextReady(waitForLaunch: false);
        Assert.Equal(["{}"], _raised);
    }

    [Fact]
    public void ALaunchBeforeTheContextRaisesNothingYet()
    {
        var gate = Gate();
        gate.FinishedLaunching([]);
        Assert.Empty(_raised);
    }
}
