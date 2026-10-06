namespace Oriel.Tests;

public sealed class AppMetricsTests
{
    [Fact]
    public void ChildKind_NamesTrackedChildrenAsChromium_AndSkipsTheRest()
    {
        Assert.Equal(("Tab", (string?)null), OrielApp.ChildKind(["helper", "--type=renderer", "--lang=en"]));
        Assert.Equal(("GPU", (string?)null), OrielApp.ChildKind(["helper", "--type=gpu-process"]));
        Assert.Equal(("Utility", "network.mojom.NetworkService"), OrielApp.ChildKind(["helper", "--type=utility", "--utility-sub-type=network.mojom.NetworkService"]));
        Assert.Equal(("Utility", (string?)null), OrielApp.ChildKind(["helper", "--type=utility", "--utility-sub-type="]));
        Assert.Null(OrielApp.ChildKind(["helper", "--type=zygote"]));
        Assert.Null(OrielApp.ChildKind(["helper", "--type=crashpad-handler"]));
        Assert.Null(OrielApp.ChildKind(["helper", "--types=renderer"]));
        Assert.Null(OrielApp.ChildKind(["oriel"]));
    }

    [Fact]
    public void ParseLinuxStat_ReadsFieldsAfterTheCommand_EvenWithSpacesAndParentheses()
    {
        const string stat = "4242 (Oriel (x) Helper) S 4000 4242 4242 0 -1 4194560 100 0 0 0 250 75 0 0 20 0 30 0 987654 123456 789 18446744073709551615";
        Assert.Equal((4000, 250UL, 75UL, 987654UL), OrielApp.ParseLinuxStat(stat));
        Assert.Null(OrielApp.ParseLinuxStat("garbage"));
        Assert.Null(OrielApp.ParseLinuxStat("1 (short) S 0 1"));
    }
}

public sealed class AppMetricsLinuxTests
{
    [Fact]
    public void LinuxDescendants_FindsAChildAndItsChild()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var shell = System.Diagnostics.Process.Start("/bin/sh", ["-c", "sleep 30 & wait"]);
        Thread.Sleep(500);
        var found = OrielApp.LinuxDescendants(Environment.ProcessId).ToList();
        try { Assert.Contains(shell.Id, found); Assert.True(found.Count >= 2, string.Join(",", found)); }
        finally { shell.Kill(entireProcessTree: true); }
    }
}
