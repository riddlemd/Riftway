namespace Riftway.Tests;

public sealed class RiftwayProcessTests
{
    [Fact]
    public void ParseMeminfo_ReadsChromiumsFieldsInAnyOrder()
    {
        var memory = RiftwayProcess.ParseMeminfo("SwapFree:  10 kB\nMemTotal:   8000 kB\nHugePages_Total: 0\nMemFree: 1000 kB\nCached: 5 kB\nMemAvailable: 3000 kB\nSwapTotal: 20 kB\nbad\n")!;
        Assert.Equal((8000UL * 1024, 1000UL * 1024, 3000UL * 1024, 20UL * 1024, 10UL * 1024), (memory.Total, memory.Free, memory.Available, memory.SwapTotal, memory.SwapFree));
        Assert.Null(memory.FileBacked);
        Assert.Null(RiftwayProcess.ParseMeminfo("MemFree: 1000 kB\n"));
    }

    [Fact]
    public void FormatWindowsVersion_IsMajorMinorBuildWithoutThePatchNumber()
    {
        Assert.Equal("10.0.26200", RiftwayProcess.FormatWindowsVersion(10, 0, 26200));
        Assert.Equal("6.1.7601", RiftwayProcess.FormatWindowsVersion(6, 1, 7601));
    }

    [Fact]
    public void ToSystemMemoryInfo_TruncatesBytesToKiB_AndKeepsAbsentFieldsNull()
    {
        var info = RiftwayProcess.ToSystemMemoryInfo(new Riftway.Cef.Platform.ProcessInfo.SystemMemory(8192 * 1024 + 1023, 2048 * 1024 + 1024, null, 4096UL * 1024, 1023, null, null));
        Assert.Equal((8192L, 2049L, 4096L, 0L), (info.Total, info.Free, info.SwapTotal, info.SwapFree));
        Assert.Null(info.Available);
        Assert.Null(info.FileBacked);
    }

    [Fact]
    public void ToProcessMemoryInfo_ConvertsToKiB_SharedIsZero_AndSaturatesLikeChromium()
    {
        Assert.Equal(new ProcessMemoryInfo(100, 50, 0), RiftwayProcess.ToProcessMemoryInfo(100 * 1024 + 1023, 50 * 1024));
        var huge = RiftwayProcess.ToProcessMemoryInfo(ulong.MaxValue, ulong.MaxValue);
        Assert.Equal((uint.MaxValue, int.MaxValue), (huge.ResidentSet, huge.Private));
    }

    [Fact]
    public void CpuSampler_ReportsZeroFirst_ThenPercentOfOneCpuSinceTheLastCall()
    {
        var cpu = TimeSpan.FromSeconds(2);
        var now = TimeSpan.FromSeconds(10);
        var sampler = new RiftwayProcess.CpuSampler(() => cpu, _ => now);
        Assert.Equal((0.0, 2.0), sampler.Sample());
        cpu += TimeSpan.FromMilliseconds(500);
        now += TimeSpan.FromSeconds(1);
        Assert.Equal((50.0, 2.5), sampler.Sample());
        Assert.Equal((0.0, 2.5), sampler.Sample());
        cpu += TimeSpan.FromSeconds(3);
        now += TimeSpan.FromSeconds(2);
        Assert.Equal((150.0, 5.5), sampler.Sample());
    }

    [Fact]
    public void EventRate_ReportsZeroFirst_ThenRoundedEventsPerSecond()
    {
        var now = TimeSpan.FromSeconds(5);
        var rate = new RiftwayProcess.EventRate(_ => now);
        Assert.Equal(0, rate.PerSecond(100));
        now += TimeSpan.FromSeconds(2);
        Assert.Equal(25, rate.PerSecond(150));
        now += TimeSpan.FromSeconds(4);
        Assert.Equal(1, rate.PerSecond(152));
        Assert.Equal(0, rate.PerSecond(160));
        now += TimeSpan.FromSeconds(2);
        // A count that goes back wraps, as Chromium's unsigned delta does, and clamps; then it starts over.
        Assert.Equal(int.MaxValue, rate.PerSecond(0));
        Assert.Equal(0, rate.PerSecond(0));
    }

    [Fact]
    public void ResourcesPathFor_IsTheBundlesResourcesOnMacOS_ElseResourcesBesideTheExecutable()
    {
        // A native root: on Windows Path.GetDirectoryName turns each slash into a backslash.
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(Path.Combine(root, "A.app", "Contents", "Resources"), RiftwayProcess.ResourcesPathFor(Path.Combine(root, "A.app", "Contents", "MacOS", "A"), macOS: true));
        Assert.Equal(Path.Combine(root, "opt", "app", "resources"), RiftwayProcess.ResourcesPathFor(Path.Combine(root, "opt", "app", "a"), macOS: false));
    }
}

public sealed class ProcessMemoryInfoTests
{
    [Fact]
    public void ParseProcStatus_ReadsChromiumsFields_AndFallsBackToStatm()
    {
        const string status = "Name:\triftway\nVmRSS:\t   2048 kB\nRssAnon:\t    512 kB\nRssFile:\t 1536 kB\nVmSwap:\t     64 kB\n";
        Assert.Equal((2048UL * 1024, 512UL * 1024, 64UL * 1024), RiftwayProcess.ParseProcStatus(status, "", 4096));
        const string old = "Name:\triftway\nVmRSS:\t   2048 kB\nVmSwap:\t      0 kB\n";
        Assert.Equal((2048UL * 1024, 300UL * 4096, 0UL), RiftwayProcess.ParseProcStatus(old, "9000 500 200 1 0 400 0", 4096));
        Assert.Null(RiftwayProcess.ParseProcStatus(old, "", 4096));
        Assert.Equal((2048UL * 1024, 300UL * 4096, 0UL), RiftwayProcess.ParseProcStatus("VmRSS:\t 2048 kB\nRssAnon:\t 0 kB\n", "9000 500 200", 4096));
    }

    [Fact]
    public void LinuxAndMacInfo_FollowChromiumsOsDumps()
    {
        Assert.Equal(new ProcessMemoryInfo(2048, 576, 0), RiftwayProcess.LinuxProcessMemoryInfo((2048UL * 1024, 512UL * 1024, 64UL * 1024)));
        Assert.Equal(new ProcessMemoryInfo(uint.MaxValue, 1, 0), RiftwayProcess.LinuxProcessMemoryInfo((ulong.MaxValue, 1024, 0)));
        Assert.Equal(new ProcessMemoryInfo(null, 3000, 0), RiftwayProcess.MacProcessMemoryInfo(3000UL * 1024));
        Assert.Equal(new ProcessMemoryInfo(null, int.MaxValue, 0), RiftwayProcess.MacProcessMemoryInfo(ulong.MaxValue));
    }
}

public sealed class ProcArgsTests
{
    [Fact]
    public void ParseProcArgs_SkipsTheExecutablePathAndItsPadding_AndStopsAtArgc()
    {
        var bytes = new List<byte>(BitConverter.GetBytes(3));
        bytes.AddRange("/x/Helper\0\0\0\0helper\0--type=renderer\0--lang=en\0PATH=/bin\0"u8.ToArray());
        Assert.Equal(["helper", "--type=renderer", "--lang=en"], Riftway.Cef.Platform.ProcessInfo.ParseProcArgs(bytes.ToArray()));
        Assert.Equal(["a", "b"], Riftway.Cef.Platform.ProcessInfo.ParseProcArgs([2, 0, 0, 0, (byte)'/', 0, (byte)'a', 0, (byte)'b']));
        Assert.Empty(Riftway.Cef.Platform.ProcessInfo.ParseProcArgs([1, 0]));
    }
}
