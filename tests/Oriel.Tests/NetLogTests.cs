namespace Oriel.Tests;

public sealed class NetLogTests
{
    private sealed class FakeBackend : INetLogBackend
    {
        public List<string> Calls { get; } = [];
        public bool Logging { get; set; }
        public string StartError { get; set; } = "";
        public string StopError { get; set; } = "";

        public bool IsLogging()
        {
            Calls.Add("is");
            return Logging;
        }

        public Task<string> Start(string path, int captureMode, long maxFileSize)
        {
            Calls.Add($"start {path} {captureMode} {maxFileSize}");
            return Task.FromResult(StartError);
        }

        public Task<string> Stop()
        {
            Calls.Add("stop");
            return Task.FromResult(StopError);
        }
    }

    [Fact]
    public void Stock_StartThrowsPlatformNotSupportedWithoutTheBackend()
    {
        var backend = new FakeBackend();
        var netLog = new SessionNetLog(patched: false, backend);

        var e = Assert.Throws<PlatformNotSupportedException>(() => { _ = netLog.StartLoggingAsync("/tmp/net.json"); });

        Assert.Contains("--log-net-log", e.Message);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void Stock_StopThrowsPlatformNotSupportedWithoutTheBackend()
    {
        var backend = new FakeBackend();
        Assert.Throws<PlatformNotSupportedException>(() => { _ = new SessionNetLog(patched: false, backend).StopLoggingAsync(); });
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void Stock_IsNeverLogging()
    {
        var backend = new FakeBackend { Logging = true };
        Assert.False(new SessionNetLog(patched: false, backend).CurrentlyLogging);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Patched_CurrentlyLoggingIsLibcefs(bool logging)
    {
        var backend = new FakeBackend { Logging = logging };
        Assert.Equal(logging, new SessionNetLog(patched: true, backend).CurrentlyLogging);
        Assert.Equal(["is"], backend.Calls);
    }

    [Theory]
    [InlineData(NetLogCaptureMode.Default, 0)]
    [InlineData(NetLogCaptureMode.IncludeSensitive, 1)]
    [InlineData(NetLogCaptureMode.Everything, 2)]
    public async Task Start_PassesTheCaptureMode(NetLogCaptureMode mode, int expected)
    {
        var backend = new FakeBackend();
        await new SessionNetLog(patched: true, backend).StartLoggingAsync("/tmp/net.json", new NetLogOptions { CaptureMode = mode });
        Assert.Equal([$"start /tmp/net.json {expected} -1"], backend.Calls);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1048576L)]
    public async Task Start_PassesTheMaximumSize(long size)
    {
        var backend = new FakeBackend();
        await new SessionNetLog(patched: true, backend).StartLoggingAsync("net.json", new NetLogOptions { MaxFileSize = size });
        Assert.Equal([$"start net.json 0 {size}"], backend.Calls);
    }

    [Fact]
    public async Task Start_WithoutOptionsIsDefaultAndUnlimited()
    {
        var backend = new FakeBackend();
        await new SessionNetLog(patched: true, backend).StartLoggingAsync("/tmp/net.json");
        Assert.Equal(["start /tmp/net.json 0 -1"], backend.Calls);
    }

    [Fact]
    public void Start_EmptyPathIsElectronsTypeError()
    {
        var backend = new FakeBackend();
        var e = Assert.Throws<ArgumentException>(() => { _ = new SessionNetLog(patched: true, backend).StartLoggingAsync(""); });
        Assert.StartsWith("The first parameter must be a valid string", e.Message);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void Start_NegativeSizeIsRejected()
    {
        var backend = new FakeBackend();
        var e = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new SessionNetLog(patched: true, backend).StartLoggingAsync("/tmp/net.json", new NetLogOptions { MaxFileSize = -1 });
        });
        Assert.StartsWith("Invalid value for maxFileSize", e.Message);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void Start_UnknownCaptureModeIsRejected()
    {
        var backend = new FakeBackend();
        var e = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new SessionNetLog(patched: true, backend).StartLoggingAsync("/tmp/net.json", new NetLogOptions { CaptureMode = (NetLogCaptureMode)3 });
        });
        Assert.StartsWith("Invalid value for captureMode", e.Message);
    }

    [Fact]
    public async Task Start_LibcefsErrorFaultsTheTask()
    {
        var backend = new FakeBackend { StartError = "FILE_ERROR_ACCESS_DENIED" };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SessionNetLog(patched: true, backend).StartLoggingAsync("/root/net.json"));
        Assert.Equal("FILE_ERROR_ACCESS_DENIED", e.Message);
    }

    [Fact]
    public async Task Stop_CompletesWhenLibcefReportsNoError()
    {
        var backend = new FakeBackend();
        await new SessionNetLog(patched: true, backend).StopLoggingAsync();
        Assert.Equal(["stop"], backend.Calls);
    }

    [Fact]
    public async Task Stop_WithNothingRunningFaultsWithElectronsText()
    {
        var backend = new FakeBackend { StopError = "No net log in progress" };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new SessionNetLog(patched: true, backend).StopLoggingAsync());
        Assert.Equal("No net log in progress", e.Message);
    }

    [Fact]
    public async Task Module_BeforeReadyDoesNothing()
    {
        Assert.Null(OrielApp.CurrentOrNull);
        Assert.False(NetLog.CurrentlyLogging);
        await NetLog.StartLoggingAsync("");
        await NetLog.StopLoggingAsync();
    }
}
