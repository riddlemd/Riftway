namespace Riftway.Tests;

public sealed class AppMetricsTests
{
    [Fact]
    public void ChildKind_NamesTrackedChildrenAsChromium_AndSkipsTheRest()
    {
        Assert.Equal(("Tab", (string?)null), RiftwayApp.ChildKind(["helper", "--type=renderer", "--lang=en"]));
        Assert.Equal(("GPU", (string?)null), RiftwayApp.ChildKind(["helper", "--type=gpu-process"]));
        Assert.Equal(("Utility", "network.mojom.NetworkService"), RiftwayApp.ChildKind(["helper", "--type=utility", "--utility-sub-type=network.mojom.NetworkService"]));
        Assert.Equal(("Utility", (string?)null), RiftwayApp.ChildKind(["helper", "--type=utility", "--utility-sub-type="]));
        Assert.Null(RiftwayApp.ChildKind(["helper", "--type=zygote"]));
        Assert.Null(RiftwayApp.ChildKind(["helper", "--type=crashpad-handler"]));
        Assert.Null(RiftwayApp.ChildKind(["helper", "--types=renderer"]));
        Assert.Null(RiftwayApp.ChildKind(["riftway"]));
    }

    [Fact]
    public void ParseLinuxStat_ReadsFieldsAfterTheCommand_EvenWithSpacesAndParentheses()
    {
        const string stat = "4242 (Riftway (x) Helper) S 4000 4242 4242 0 -1 4194560 100 0 0 0 250 75 0 0 20 0 30 0 987654 123456 789 18446744073709551615";
        Assert.Equal((4000, 250UL, 75UL, 987654UL), RiftwayApp.ParseLinuxStat(stat));
        Assert.Null(RiftwayApp.ParseLinuxStat("garbage"));
        Assert.Null(RiftwayApp.ParseLinuxStat("1 (short) S 0 1"));
    }
}

public sealed class AppMetricsWindowsTests
{
    [Theory]
    [InlineData(0u, "untrusted")]
    [InlineData(0xFFFu, "untrusted")]
    [InlineData(0x1000u, "low")]
    [InlineData(0x1FFFu, "low")]
    [InlineData(0x2000u, "medium")]
    [InlineData(0x2FFFu, "medium")]
    [InlineData(0x3000u, "high")]
    [InlineData(0x3FFFu, "high")]
    [InlineData(0x4000u, "unknown")]
    [InlineData(0x5000u, "unknown")]
    public void IntegrityLevelName_BandsTheRidAsElectronDoes(uint rid, string expected) => Assert.Equal(expected, RiftwayApp.IntegrityLevelName(rid));

    [Theory]
    [InlineData("untrusted", true)]
    [InlineData("low", true)]
    [InlineData("medium", false)]
    [InlineData("high", false)]
    [InlineData("unknown", false)]
    public void IsSandboxedLevel_IsBelowMediumAndAboveUnknown(string level, bool expected) => Assert.Equal(expected, RiftwayApp.IsSandboxedLevel(level));

    [Fact]
    public void KiB_ShiftsBytesRightTen_Truncating() => Assert.Equal((0L, 1L, 1L, 4096L), (RiftwayApp.KiB(1023), RiftwayApp.KiB(1024), RiftwayApp.KiB(2047), RiftwayApp.KiB(4096UL * 1024)));

    [Fact]
    public void FileTimeToUnixMilliseconds_CountsFrom1970In100nsTicks()
    {
        Assert.Equal(0, RiftwayApp.FileTimeToUnixMilliseconds(116444736000000000UL));
        Assert.Equal(1500.5, RiftwayApp.FileTimeToUnixMilliseconds(116444736000000000UL + 15005000UL));
    }

    [Fact]
    public void Descendants_FollowsParentChainsToTheRoot_InPidOrder_AndSurvivesCycles()
    {
        var parents = new Dictionary<int, int> { [10] = 1, [11] = 10, [12] = 11, [20] = 2, [30] = 31, [31] = 30, [5] = 1 };
        Assert.Equal([5, 10, 11, 12], RiftwayApp.Descendants(parents, 1));
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
        var found = RiftwayApp.LinuxDescendants(Environment.ProcessId).ToList();
        try { Assert.Contains(shell.Id, found); Assert.True(found.Count >= 2, string.Join(",", found)); }
        finally { shell.Kill(entireProcessTree: true); }
    }
}
