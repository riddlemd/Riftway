using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class HandoffRuleTests
{
    [Theory]
    [InlineData(5.0, true)]
    [InlineData(-2147483648.0, true)]
    [InlineData(2147483647.0, true)]
    [InlineData(2147483648.0, false)]
    [InlineData(-2147483649.0, false)]
    [InlineData(5.5, false)]
    [InlineData(-0.0, false)]
    [InlineData(0.0, true)]
    public void IsInt32_IsV8s(double value, bool expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(expected, MacUserActivity.IsInt32(value));
    }

    [Theory]
    [InlineData(1099511627776.0, "1099511627776.0")]
    [InlineData(1.5, "1.5")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1e21, "1E+21")]
    public void JsonWriterDouble_MarksIntegralDoubles(double value, string expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(expected, MacUserActivity.JsonWriterDouble(value));
    }

    [Fact]
    public void WriteJson_WritesNumbersAsV8HandsThemOver()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var value = new JsonObject { ["i"] = 5L, ["big"] = 1099511627776L, ["d"] = 1.5, ["whole"] = 2.0, ["n"] = null, ["a"] = new JsonArray(true, "x", false) };
        Assert.Equal("""{"i":5,"big":1099511627776.0,"d":1.5,"whole":2,"n":null,"a":[true,"x",false]}""", Encoding.UTF8.GetString(MacUserActivity.WriteJson(value)!));
    }

    [Fact]
    public void WriteJson_FailsOnANonFiniteNumber()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Null(MacUserActivity.WriteJson(new JsonObject { ["x"] = new JsonArray(JsonValue.Create(double.NaN)) }));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    [InlineData("https://Example.com", "https://example.com/")]
    [InlineData("http://example.com/a b", "http://example.com/a%20b")]
    public void UserActivityUrl_IsTheCanonicalUrlOrNone(string? url, string? expected)
        => Assert.Equal(expected, OrielApp.UserActivityUrl(url));

    [Theory]
    [InlineData("file:///tmp/x")]
    [InlineData("mailto:a@example.com")]
    [InlineData("httpx://example.com")]
    public void UserActivityUrl_RefusesOtherSchemes(string url)
        => Assert.Throws<ArgumentException>(() => OrielApp.UserActivityUrl(url));
}

[CollectionDefinition(nameof(HandoffCollection), DisableParallelization = true)]
public sealed class HandoffCollection;

[Collection(nameof(HandoffCollection))]
public sealed class MacUserActivityTests : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions s_relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Action<Action>? _savedRunOnMain = OperatingSystem.IsMacOS() ? MacUserActivity.RunOnMainSync : null;
    private readonly Action<nint, string>? _savedAnnounce = OperatingSystem.IsMacOS() ? MacUserActivity.Announce : null;
    private readonly List<string> _announced = [];

    // No test may make a real activity current: the system would advertise it, and save it on a queue that waits for a
    // main thread the test host never runs.
    public MacUserActivityTests()
    {
        if (!OperatingSystem.IsMacOS()) return;
        MacUserActivity.Announce = (_, call) => _announced.Add(call);
        MacUserActivity.RunOnMainSync = a => a();
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsMacOS()) return;
        MacUserActivity.InvalidateCurrent();
        MacUserActivity.WillSave = null;
        MacUserActivity.RunOnMainSync = _savedRunOnMain!;
        MacUserActivity.Announce = _savedAnnounce!;
    }

    [Fact]
    public void UserInfo_RoundTripsAsElectronsDictUtil()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var value = new JsonObject
        {
            ["z"] = 1,
            ["b"] = true,
            ["c"] = 1.5,
            ["big"] = 1099511627776L,
            ["n"] = null,
            ["arr"] = new JsonArray(1, false, "x", new JsonObject { ["k"] = 2 }),
            ["o"] = new JsonObject { ["x"] = new JsonArray() },
            ["s"] = "é",
        };
        var back = MacUserActivity.ToJson(MacUserActivity.ToNSDictionary(value));
        Assert.Equal("""{"arr":[1,false,"x",{"k":2}],"b":true,"big":1099511627776,"c":1.5,"n":"<null>","o":{"x":[]},"s":"é","z":1}""", back.ToJsonString(s_relaxed));
        Assert.Equal(1099511627776.0, back["big"]!.GetValue<double>());
        Assert.Equal(1, back["z"]!.GetValue<int>());
    }

    [Fact]
    public void ALongIsReadThroughIntValue()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var back = MacUserActivity.ToJson(MacUserActivity.FromJsonBytes("""{"q":4294967297}"""u8.ToArray()));
        Assert.Equal(1, back["q"]!.GetValue<int>());
    }

    [Fact]
    public void NilAndNonDictionariesAreEmptyOrNil()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Empty(MacUserActivity.ToJson(0));
        Assert.Equal(0, MacUserActivity.FromJsonBytes("[1]"u8.ToArray()));
        Assert.Equal(0, MacUserActivity.ToNSDictionary(new JsonObject { ["x"] = JsonValue.Create(double.PositiveInfinity) }));
    }

    [Fact]
    public void TheCurrentActivityIsSetUpdatedResignedAndInvalidated()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal("", MacUserActivity.CurrentType());
        MacUserActivity.SetCurrent("com.oriel.test.handoff", new JsonObject { ["a"] = 1, ["b"] = "x" }, "https://example.com/");
        Assert.Equal("com.oriel.test.handoff", MacUserActivity.CurrentType());
        MacUserActivity.UpdateCurrent(new JsonObject { ["b"] = "y", ["c"] = true });
        Assert.Equal("""{"a":1,"b":"y","c":true}""", MacUserActivity.CurrentUserInfo().ToJsonString());
        MacUserActivity.ResignCurrent();
        Assert.Equal("com.oriel.test.handoff", MacUserActivity.CurrentType());
        MacUserActivity.SetCurrent("com.oriel.test.second", [], null);
        Assert.Equal("com.oriel.test.second", MacUserActivity.CurrentType());
        Assert.Empty(MacUserActivity.CurrentUserInfo());
        MacUserActivity.InvalidateCurrent();
        Assert.Equal("", MacUserActivity.CurrentType());
        Assert.Empty(MacUserActivity.CurrentUserInfo());
        MacUserActivity.UpdateCurrent(new JsonObject { ["a"] = 1 });
        Assert.Equal("", MacUserActivity.CurrentType());
        Assert.Equal(["becomeCurrent", "setNeedsSave:", "resignCurrent", "becomeCurrent", "setNeedsSave:", "invalidate"], _announced);
    }

    [Fact]
    public void WillSave_ReportsTheActivityAndDoesNotWaitUnlessAsked()
    {
        if (!OperatingSystem.IsMacOS()) return;
        MacUserActivity.RunOnMainSync = a => a();
        MacUserActivity.SetCurrent("com.oriel.test.save", new JsonObject { ["k"] = 3 }, null);
        string? seen = null;
        MacUserActivity.WillSave = (type, info) => { seen = type + info.ToJsonString(); return false; };
        var clock = Stopwatch.StartNew();
        MacUserActivity.WillSaveActivity(CurrentActivity());
        Assert.True(clock.ElapsedMilliseconds < 500, $"{clock.ElapsedMilliseconds} ms");
        Assert.Equal("""com.oriel.test.save{"k":3}""", seen);
    }

    [Fact]
    public void WillSave_WaitsASecondForAnUpdate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        MacUserActivity.RunOnMainSync = a => a();
        MacUserActivity.SetCurrent("com.oriel.test.save", [], null);
        MacUserActivity.WillSave = (_, _) => true;
        var clock = Stopwatch.StartNew();
        MacUserActivity.WillSaveActivity(CurrentActivity());
        Assert.InRange(clock.ElapsedMilliseconds, 900, 3000);
    }

    [Fact]
    public void WillSave_StopsWaitingAtTheUpdate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        MacUserActivity.RunOnMainSync = a => a();
        MacUserActivity.SetCurrent("com.oriel.test.save", [], null);
        MacUserActivity.WillSave = (_, _) =>
        {
            _ = Task.Delay(100).ContinueWith(_ => { if (OperatingSystem.IsMacOS()) MacUserActivity.UpdateCurrent(new JsonObject { ["done"] = true }); }, TaskScheduler.Default);
            return true;
        };
        var clock = Stopwatch.StartNew();
        MacUserActivity.WillSaveActivity(CurrentActivity());
        Assert.InRange(clock.ElapsedMilliseconds, 50, 800);
        Assert.Equal("""{"done":true}""", MacUserActivity.CurrentUserInfo().ToJsonString());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static nint CurrentActivity() => MacUserActivity.Current;
}
