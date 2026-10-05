using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Oriel.Tests;

public class WireFrameTests
{
    [Fact]
    public void EncodeDecode_RoundTripsEveryField()
    {
        var frame = new WireFrame("port-msg", "abc", """{"n":1,"s":"é\"x"}""", ["p1", "p2"], "tok", "at X.Y()", "boom");
        var back = WireFrame.Decode(frame.Encode());
        Assert.Equal("port-msg", back.Type);
        Assert.Equal("abc", back.Id);
        Assert.Equal(1, JsonNode.Parse(back.Data!)!["n"]!.GetValue<int>());
        Assert.Equal("é\"x", JsonNode.Parse(back.Data!)!["s"]!.GetValue<string>());
        Assert.Equal(["p1", "p2"], back.Ports!);
        Assert.Equal("tok", back.Token);
        Assert.Equal("at X.Y()", back.Location);
        Assert.Equal("boom", back.Report);
    }

    [Fact]
    public void Encode_LeavesOutWhatIsNotSet()
    {
        var json = Encoding.UTF8.GetString(new WireFrame("port-close", "id1").Encode());
        Assert.Equal("""{"t":"port-close","id":"id1"}""", json);
    }

    [Fact]
    public void Encode_KeepsNullData()
    {
        var back = WireFrame.Decode(new WireFrame("msg", Data: "null").Encode());
        Assert.Equal("null", back.Data);
        Assert.Empty(back.Ports!);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("{\"t\":3}")]
    [InlineData("{\"t\":")]
    [InlineData("{\"t\":\"msg\",\"ports\":[1]}")]
    public void Decode_RefusesWhatIsNotAFrame(string json)
        => Assert.Throws<InvalidDataException>(() => WireFrame.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task WriteRead_FramesFollowEachOtherThenEndCleanly()
    {
        using var stream = new MemoryStream();
        WireFrame.Write(stream, "first"u8);
        WireFrame.Write(stream, ""u8);
        WireFrame.Write(stream, "third"u8);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, stream.ToArray()[..4]);
        stream.Position = 0;
        Assert.Equal("first"u8.ToArray(), await WireFrame.ReadAsync(stream));
        Assert.Equal(Array.Empty<byte>(), await WireFrame.ReadAsync(stream));
        Assert.Equal("third"u8.ToArray(), await WireFrame.ReadAsync(stream));
        Assert.Null(await WireFrame.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_ReassemblesAFrameThatArrivesInPieces()
    {
        using var stream = new TrickleStream([5, 0, 0, 0, .. "hello"u8.ToArray()]);
        Assert.Equal("hello"u8.ToArray(), await WireFrame.ReadAsync(stream));
    }

    [Theory]
    [InlineData(new byte[] { 5, 0 })]
    [InlineData(new byte[] { 5, 0, 0, 0, 1, 2 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0x01, 0x00, 0x00, 0x04 })]
    public async Task Read_RefusesATruncatedOrOutOfRangeFrame(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => WireFrame.ReadAsync(stream));
    }

    [Fact]
    public async Task Read_AcceptsTheLargestFrame()
    {
        var payload = new byte[WireFrame.MaxLength];
        using var stream = new MemoryStream();
        WireFrame.Write(stream, payload);
        stream.Position = 0;
        Assert.Equal(WireFrame.MaxLength, (await WireFrame.ReadAsync(stream))!.Length);
    }

    [Fact]
    public void Write_RefusesAFrameOverTheLimit()
        => Assert.Throws<InvalidDataException>(() => WireFrame.Write(Stream.Null, new byte[WireFrame.MaxLength + 1]));

    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}

public class UtilityProcessOptionsTests
{
    [Theory]
    [InlineData("inherit", (int)StdioMode.Inherit)]
    [InlineData("pipe", (int)StdioMode.Pipe)]
    [InlineData("ignore", (int)StdioMode.Ignore)]
    public void Stdio_StringAppliesToStdoutAndStderr(string mode, int expected)
        => Assert.Equal(((StdioMode)expected, (StdioMode)expected), ((UtilityProcessStdio)mode).Resolve());

    [Fact]
    public void Stdio_ArrayPicksEachStream()
    {
        Assert.Equal((StdioMode.Pipe, StdioMode.Inherit), ((UtilityProcessStdio)new[] { "ignore", "pipe", "inherit" }).Resolve());
        Assert.Equal((StdioMode.Ignore, StdioMode.Pipe), ((UtilityProcessStdio)new[] { "ignore", "ignore", "pipe", "extra" }).Resolve());
    }

    [Theory]
    [InlineData(new[] { "bogus" }, "stdio must be of the following values: inherit, pipe, ignore")]
    [InlineData(new[] { "ignore", "pipe" }, "configuration missing for stdin, stdout or stderr.")]
    [InlineData(new[] { "pipe", "pipe", "pipe" }, "stdin value other than ignore is not supported.")]
    [InlineData(new[] { "ignore", "file", "pipe" }, "stdout configuration must be of the following values: inherit, pipe, ignore")]
    [InlineData(new[] { "ignore", "pipe", "file" }, "stderr configuration must be of the following values: inherit, pipe, ignore")]
    public void Stdio_RefusesWithElectronsMessages(string[] modes, string message)
    {
        var stdio = modes.Length == 1 ? UtilityProcessStdio.FromMode(modes[0]) : UtilityProcessStdio.FromModes(modes);
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => stdio.Resolve()).Message);
    }

    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "dn");
    private static readonly string s_runtime = Path.Combine(s_root, "shared", "Microsoft.NETCore.App", "10.0.0") + Path.DirectorySeparatorChar;
    private static readonly string s_module = Path.Combine(Path.GetTempPath(), "app", "Child.dll");

    [Fact]
    public void Launch_AnExecutableRunsAsItIs()
    {
        var launch = UtilityLaunch.Resolve("/app/child", ["a"], ["--x"], "/usr/bin/dotnet", s_runtime, windows: false, _ => true);
        Assert.Equal("/app/child", launch.FileName);
        Assert.False(launch.ViaHost);
        Assert.Equal(["a"], launch.Arguments);
    }

    [Fact]
    public void Launch_UnderTheMuxerReusesIt()
    {
        var host = Path.Combine(s_root, "dotnet.exe");
        var launch = UtilityLaunch.Resolve(s_module, ["a", "b"], ["--roll-forward", "Major"], host, s_runtime, windows: true, _ => false);
        Assert.Equal(host, launch.FileName);
        Assert.True(launch.ViaHost);
        Assert.Equal(["exec", "--roll-forward", "Major", s_module, "a", "b"], launch.Arguments);
    }

    [Fact]
    public void Launch_UnderAnApphostFindsTheSharedInstallsMuxer()
    {
        var muxer = Path.Combine(s_root, "dotnet");
        var launch = UtilityLaunch.Resolve(s_module, ["a"], [], "/app/Parent", s_runtime, windows: false, p => p == muxer);
        Assert.Equal(muxer, launch.FileName);
        Assert.Equal(["exec", s_module, "a"], launch.Arguments);
    }

    [Fact]
    public void Launch_SelfContainedUsesTheModulesApphost()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "app") + Path.DirectorySeparatorChar;
        var apphost = Path.ChangeExtension(s_module, ".exe");
        // An app directory three levels down must not be taken for a shared install.
        var launch = UtilityLaunch.Resolve(s_module, ["a"], ["--x"], Path.Combine(appDir, "Parent.exe"), appDir, windows: true, p => p == apphost || p.EndsWith("dotnet.exe", StringComparison.Ordinal));
        Assert.Equal(apphost, launch.FileName);
        Assert.False(launch.ViaHost);
        Assert.Equal(["a"], launch.Arguments);
    }

    [Fact]
    public void Launch_SharedInstallWithoutAMuxerFallsBackToTheApphost()
    {
        var apphost = Path.ChangeExtension(s_module, null);
        var launch = UtilityLaunch.Resolve(s_module, [], [], "/app/Parent", s_runtime, windows: false, p => p == apphost);
        Assert.Equal(apphost, launch.FileName);
        Assert.False(launch.ViaHost);
    }

    [Fact]
    public void Launch_WithNothingElseUsesDotnetOnPath()
    {
        var launch = UtilityLaunch.Resolve(s_module.ToUpperInvariant().Replace(".DLL", ".Dll", StringComparison.Ordinal), ["a"], [], "/app/Parent", "/app/", windows: false, _ => false);
        Assert.Equal("dotnet", launch.FileName);
        Assert.True(launch.ViaHost);
        Assert.Equal("exec", launch.Arguments[0]);
    }

    [Theory]
    [InlineData(143, true, false, 0)]
    [InlineData(137, true, false, 0)]
    [InlineData(143, false, false, 143)]
    [InlineData(7, true, false, 7)]
    [InlineData(143, true, true, 143)]
    [InlineData(0, true, true, 0)]
    public void ExitCode_AKilledChildExitedNormally(int code, bool killed, bool windows, int expected)
        => Assert.Equal(expected, UtilityLaunch.ExitCode(code, killed, windows));

    [Fact]
    public void Fatal_NamesTheTopFrameAndKeepsTheWholeException()
    {
        Exception thrown;
        try { throw new InvalidOperationException("boom"); }
        catch (InvalidOperationException e) { thrown = e; }
        var frame = ParentPort.Fatal(thrown);
        Assert.Equal("fatal", frame.Type);
        Assert.StartsWith("at Oriel.Tests.UtilityProcessOptionsTests.", frame.Location, StringComparison.Ordinal);
        Assert.Contains("boom", frame.Report, StringComparison.Ordinal);
        Assert.Equal(typeof(string).FullName, ParentPort.Fatal("not an exception").Location);
    }

    [Fact]
    public void Fork_RefusesAMissingModuleAndBadStdioBeforeReadiness()
    {
        Assert.Equal("Missing UtilityProcess entry script. (Parameter 'modulePath')", Assert.Throws<ArgumentException>(() => UtilityProcess.Fork("")).Message);
        Assert.Throws<ArgumentException>(() => UtilityProcess.Fork("x.dll", new UtilityProcessOptions { Stdio = "bogus" }));
        Assert.Equal("utilityProcess cannot be created before app is ready.", Assert.Throws<InvalidOperationException>(() => UtilityProcess.Fork("x.dll")).Message);
    }
}

public class PortWireTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(10);

    private sealed class Side(PortWire wire)
    {
        public PortWire Wire { get; } = wire;
        public TaskCompletionSource<(JsonNode? Data, IReadOnlyList<MessagePortMain> Ports)> Message { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(string, string)> Fatal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static (Side A, Side B) Pair()
    {
        var name = "oriel-test-" + Guid.NewGuid().ToString("N")[..16];
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accept = server.WaitForConnectionAsync();
        client.Connect(5000);
        accept.Wait(s_wait);
        var a = Wire(server);
        var b = Wire(client);
        return (a, b);

        static Side Wire(Stream stream)
        {
            var side = new Side(new PortWire(Task.FromResult(stream)));
            side.Wire.Message = (data, ports) => side.Message.TrySetResult((data, ports));
            side.Wire.Gone = () => side.Gone.TrySetResult();
            side.Wire.Fatal = (location, report) => side.Fatal.TrySetResult((location, report));
            side.Wire.Start();
            return side;
        }
    }

    private static void Send(Side side, JsonNode? data, params MessagePortMain[] ports)
    {
        var moved = PortLink.CheckTransfer(ports, null);
        PortThread.Run(() => side.Wire.SendMessage(data?.ToJsonString() ?? "null", moved));
    }

    private static Task<string?> NextText(MessagePortMain port)
    {
        var got = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Message += (_, e) => got.TrySetResult(e.Data?.GetValue<string>());
        port.Start();
        return got.Task;
    }

    private static Task Closed(MessagePortMain port)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Closed += (_, _) => closed.TrySetResult();
        return closed.Task;
    }

    [Fact]
    public async Task Message_ArrivesWithItsData()
    {
        var (a, b) = Pair();
        Send(a, new JsonObject { ["n"] = 5 });
        var (data, ports) = await b.Message.Task.WaitAsync(s_wait);
        Assert.Equal(5, data!["n"]!.GetValue<int>());
        Assert.Empty(ports);
        a.Wire.Close();
    }

    [Fact]
    public async Task Port_MovedAcrossStaysEntangledBothWays()
    {
        var (a, b) = Pair();
        var channel = new MessageChannelMain();
        var atA = NextText(channel.Port1);
        Send(a, null, channel.Port2);
        var (_, ports) = await b.Message.Task.WaitAsync(s_wait);
        var far = Assert.Single(ports);
        Assert.NotSame(channel.Port2, far);
        var atB = NextText(far);

        far.PostMessage("from b");
        Assert.Equal("from b", await atA.WaitAsync(s_wait));
        channel.Port1.PostMessage("from a");
        Assert.Equal("from a", await atB.WaitAsync(s_wait));
        a.Wire.Close();
    }

    [Fact]
    public async Task Port_SentBackOverTheSameWireStillReachesItsPartner()
    {
        var (a, b) = Pair();
        var channel = new MessageChannelMain();
        var atA = NextText(channel.Port1);
        Send(a, null, channel.Port2);
        var (_, ports) = await b.Message.Task.WaitAsync(s_wait);
        a.Wire.Message = (data, back) => a.Message.TrySetResult((data, back));
        Send(b, "back", ports[0]);
        var (_, returned) = await a.Message.Task.WaitAsync(s_wait);
        Assert.Single(returned).PostMessage("round");
        Assert.Equal("round", await atA.WaitAsync(s_wait));
        a.Wire.Close();
    }

    [Fact]
    public async Task Close_OnOneSideClosesTheOther()
    {
        var (a, b) = Pair();
        var one = new MessageChannelMain();
        var two = new MessageChannelMain();
        var oneClosedAtA = Closed(one.Port1);
        Send(a, null, one.Port2, two.Port2);
        var (_, ports) = await b.Message.Task.WaitAsync(s_wait);
        var twoClosedAtB = Closed(ports[1]);

        ports[0].Close();
        await oneClosedAtA.WaitAsync(s_wait);
        two.Port1.Close();
        await twoClosedAtB.WaitAsync(s_wait);
        a.Wire.Close();
    }

    [Fact]
    public async Task Gone_PeerEndingClosesEveryPortItHeld()
    {
        var (a, b) = Pair();
        var channel = new MessageChannelMain();
        var closedAtA = Closed(channel.Port1);
        Send(a, null, channel.Port2);
        var (_, ports) = await b.Message.Task.WaitAsync(s_wait);
        var closedAtB = Closed(ports[0]);

        b.Wire.Close();
        await a.Gone.Task.WaitAsync(s_wait);
        await closedAtA.WaitAsync(s_wait);
        await b.Gone.Task.WaitAsync(s_wait);
        await closedAtB.WaitAsync(s_wait);
    }

    [Fact]
    public async Task Send_AfterThePeerIsGoneClosesThePortsInstead()
    {
        var (a, b) = Pair();
        b.Wire.Close();
        await a.Gone.Task.WaitAsync(s_wait);
        var channel = new MessageChannelMain();
        var closed = Closed(channel.Port1);
        Send(a, null, channel.Port2);
        await closed.WaitAsync(s_wait);
    }

    [Fact]
    public async Task SendNow_ArrivesAfterWhatWasQueued()
    {
        var (a, b) = Pair();
        var order = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Wire.Message = (data, _) => order.Add(data!.GetValue<string>());
        b.Wire.Fatal = (location, _) =>
        {
            order.Add(location);
            done.TrySetResult();
        };
        PortThread.Run(() =>
        {
            a.Wire.SendMessage("\"queued\"", []);
            a.Wire.SendNow(new WireFrame("fatal", Location: "fatal", Report: "r"));
        });
        await done.Task.WaitAsync(s_wait);
        Assert.Equal(["queued", "fatal"], order);
        a.Wire.Close();
    }

    [Fact]
    public async Task Wire_NoPeerEverConnectingIsGone()
    {
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new PortWire(Task.FromException<Stream>(new OperationCanceledException())) { Gone = () => gone.TrySetResult() };
        wire.Start();
        await gone.Task.WaitAsync(s_wait);
        await wire.ReadCompletion.WaitAsync(s_wait);
    }
}
