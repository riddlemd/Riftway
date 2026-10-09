using System.Text;
using System.Text.Json;

namespace Oriel.Tests;

public sealed class ServiceWorkerProtocolTests
{
    internal static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("starting", ServiceWorkerRunningStatus.Starting)]
    [InlineData("running", ServiceWorkerRunningStatus.Running)]
    [InlineData("stopping", ServiceWorkerRunningStatus.Stopping)]
    [InlineData("stopped", ServiceWorkerRunningStatus.Stopped)]
    public void RunningStatus_ReadsTheProtocolNames_AndNamesThemAsElectron(string name, ServiceWorkerRunningStatus status)
    {
        Assert.Equal(status, ServiceWorkerProtocol.RunningStatus(name));
        Assert.Equal(name, ServiceWorkerProtocol.Name(status));
    }

    [Fact]
    public void RunningStatus_UnknownIsNull() => Assert.Null(ServiceWorkerProtocol.RunningStatus("paused"));

    [Theory]
    [InlineData("debug", ConsoleMessageLevel.Debug)]
    [InlineData("count", ConsoleMessageLevel.Debug)]
    [InlineData("timeEnd", ConsoleMessageLevel.Debug)]
    [InlineData("error", ConsoleMessageLevel.Error)]
    [InlineData("assert", ConsoleMessageLevel.Error)]
    [InlineData("warning", ConsoleMessageLevel.Warning)]
    [InlineData("log", ConsoleMessageLevel.Info)]
    [InlineData("info", ConsoleMessageLevel.Info)]
    [InlineData("table", ConsoleMessageLevel.Info)]
    [InlineData("trace", ConsoleMessageLevel.Info)]
    public void ConsoleApiLevel_IsV8sClientLevel(string type, ConsoleMessageLevel level) => Assert.Equal(level, ServiceWorkerProtocol.ConsoleApiLevel(type));

    [Theory]
    [InlineData("verbose", ConsoleMessageLevel.Debug)]
    [InlineData("info", ConsoleMessageLevel.Info)]
    [InlineData("warning", ConsoleMessageLevel.Warning)]
    [InlineData("error", ConsoleMessageLevel.Error)]
    public void LogLevel(string level, ConsoleMessageLevel expected) => Assert.Equal(expected, ServiceWorkerProtocol.LogLevel(level));

    [Theory]
    [InlineData("xml", "xml")]
    [InlineData("javascript", "javascript")]
    [InlineData("network", "network")]
    [InlineData("storage", "storage")]
    [InlineData("rendering", "rendering")]
    [InlineData("security", "security")]
    [InlineData("deprecation", "deprecation")]
    [InlineData("worker", "worker")]
    [InlineData("violation", "violation")]
    [InlineData("intervention", "intervention")]
    [InlineData("recommendation", "recommendation")]
    [InlineData("appcache", "other")]
    [InlineData("other", "other")]
    [InlineData(null, "other")]
    public void LogSource_IsElectronsMessageSourceToString(string? source, string expected) => Assert.Equal(expected, ServiceWorkerProtocol.LogSource(source));

    private static ServiceWorkerConsoleMessageEventArgs Api(string argsJson, string type = "log", string stack = """{"callFrames":[{"url":"https://a.test/sw.js","lineNumber":4,"columnNumber":2,"functionName":"","scriptId":"1"}]}""")
        => ServiceWorkerProtocol.FromConsoleApi(J($$"""{"type":"{{type}}","args":{{argsJson}},"timestamp":5,"stackTrace":{{stack}}}"""), 7)!;

    [Fact]
    public void ConsoleApi_JoinsArgumentsWithSpaces_AndTakesTheTopFramesOneBasedLine()
    {
        var message = Api("""[{"type":"string","value":"hello"},{"type":"number","value":1.5,"description":"1.5"},{"type":"boolean","value":true}]""");
        Assert.Equal(new ServiceWorkerConsoleMessageEventArgs("hello 1.5 true", 7, "console-api", ConsoleMessageLevel.Info, "https://a.test/sw.js", 5), message);
    }

    [Fact]
    public void ConsoleApi_WithoutStack_HasNoSourceOrLine()
    {
        var message = ServiceWorkerProtocol.FromConsoleApi(J("""{"type":"warning","args":[]}"""), 3)!;
        Assert.Equal(("", "", 0, ConsoleMessageLevel.Warning), (message.Message, message.SourceUrl, message.LineNumber, message.Level));
    }

    [Fact]
    public void ConsoleApi_ClearIsNoMessage() => Assert.Null(ServiceWorkerProtocol.FromConsoleApi(J("""{"type":"clear","args":[]}"""), 1));

    [Theory]
    [InlineData("""{"type":"undefined"}""", "undefined")]
    [InlineData("""{"type":"object","subtype":"null","value":null}""", "null")]
    [InlineData("""{"type":"number","unserializableValue":"-0","description":"-0"}""", "0")]
    [InlineData("""{"type":"number","unserializableValue":"NaN","description":"NaN"}""", "NaN")]
    [InlineData("""{"type":"bigint","unserializableValue":"12n","description":"12n"}""", "12n")]
    [InlineData("""{"type":"boolean","value":false}""", "false")]
    [InlineData("""{"type":"symbol","description":"Symbol(k)"}""", "Symbol(k)")]
    [InlineData("""{"type":"function","className":"Function","description":"() => 1"}""", "() => 1")]
    [InlineData("""{"type":"object","subtype":"date","className":"Date","description":"Thu Jan 01 1970"}""", "Thu Jan 01 1970")]
    [InlineData("""{"type":"object","subtype":"regexp","className":"RegExp","description":"/a/g"}""", "/a/g")]
    [InlineData("""{"type":"object","subtype":"error","className":"Error","description":"Error: boom\n    at f (https://a.test/sw.js:1:1)"}""", "Error: boom")]
    [InlineData("""{"type":"object","subtype":"error","className":"Error","description":"Error: bare"}""", "Error: bare")]
    [InlineData("""{"type":"object","subtype":"proxy","className":"Object","description":"Proxy(Object)"}""", "[object Proxy]")]
    [InlineData("""{"type":"object","className":"Foo","description":"Foo"}""", "[object Object]")]
    [InlineData("""{"type":"object","subtype":"map","className":"Map","description":"Map(0)"}""", "[object Map]")]
    public void ConsoleApi_ValueText_IsV8ValueStringBuilders(string remoteObject, string text) => Assert.Equal(text, Api($"[{remoteObject}]").Message);

    [Fact]
    public void ConsoleApi_ArrayText_JoinsElementsWithCommas_EmptyForNullAndUndefined()
    {
        var array = """
            {"type":"object","subtype":"array","className":"Array","description":"Array(5)","preview":{"type":"object","subtype":"array","overflow":false,"properties":[
              {"name":"0","type":"number","value":"1"},
              {"name":"1","type":"object","subtype":"null","value":"null"},
              {"name":"2","type":"string","value":"a"},
              {"name":"3","type":"undefined","value":"undefined"},
              {"name":"4","type":"object","value":"Object"}]}}
            """;
        Assert.Equal("1,,a,,[object Object]", Api($"[{array}]").Message);
    }

    [Fact]
    public void LogEntry_ReadsSourceLevelTextAndOneBasedLine()
    {
        var message = ServiceWorkerProtocol.FromLogEntry(J("""{"entry":{"source":"network","level":"error","text":"Failed to load","timestamp":9,"url":"https://a.test/x","lineNumber":0}}"""), 4);
        Assert.Equal(new ServiceWorkerConsoleMessageEventArgs("Failed to load", 4, "network", ConsoleMessageLevel.Error, "https://a.test/x", 1), message);
    }

    [Fact]
    public void LogEntry_WithoutLineOrUrl()
    {
        var message = ServiceWorkerProtocol.FromLogEntry(J("""{"entry":{"source":"violation","level":"verbose","text":"t","timestamp":9}}"""), 4);
        Assert.Equal(("", 0, ConsoleMessageLevel.Debug, "violation"), (message.SourceUrl, message.LineNumber, message.Level, message.Source));
    }

    [Fact]
    public void Timestamp_IsTheEntrysForLog_AndTheEventsForRuntime()
    {
        Assert.Equal(9, ServiceWorkerProtocol.Timestamp("Log.entryAdded", J("""{"entry":{"timestamp":9},"timestamp":1}""")));
        Assert.Equal(5, ServiceWorkerProtocol.Timestamp("Runtime.consoleAPICalled", J("""{"timestamp":5}""")));
        Assert.Equal(0, ServiceWorkerProtocol.Timestamp("Runtime.consoleAPICalled", J("{}")));
    }
}

public sealed class ServiceWorkerStateTests
{
    internal static JsonElement Registrations(params (long Id, string Scope, bool Deleted)[] items)
    {
        var json = new StringBuilder("""{"registrations":[""");
        json.AppendJoin(',', items.Select(r => $$"""{"registrationId":"{{r.Id}}","scopeURL":"{{r.Scope}}","isDeleted":{{(r.Deleted ? "true" : "false")}}}"""));
        return ServiceWorkerProtocolTests.J(json.Append("]}").ToString());
    }

    internal static JsonElement Versions(params (long Id, long Registration, string Status, string Running, string? Target)[] items)
    {
        var json = new StringBuilder("""{"versions":[""");
        json.AppendJoin(',', items.Select(v => $$"""{"versionId":"{{v.Id}}","registrationId":"{{v.Registration}}","scriptURL":"https://a.test/sw{{v.Id}}.js","runningStatus":"{{v.Running}}","status":"{{v.Status}}"{{(v.Target is null ? "" : $",\"targetId\":\"{v.Target}\"")}},"controlledClients":[]}"""));
        return ServiceWorkerProtocolTests.J(json.Append("]}").ToString());
    }

    internal static ServiceWorkerState Listed(JsonElement? registrations = null, JsonElement? versions = null)
    {
        var state = new ServiceWorkerState();
        state.ApplyRegistrations(registrations ?? Registrations());
        state.ApplyVersions(versions ?? Versions());
        return state;
    }

    [Fact]
    public void FirstLists_ChangeNothing_AndCountRunningAndStoppingWorkersAsRunning()
    {
        var state = new ServiceWorkerState();
        Assert.False(state.IsListed);
        Assert.Empty(state.ApplyRegistrations(Registrations((1, "https://a.test/", false))));
        Assert.False(state.IsListed);
        var changes = state.ApplyVersions(Versions((10, 1, "activated", "running", null), (11, 1, "activated", "stopping", null), (12, 1, "installed", "stopped", null), (13, 1, "activated", "starting", null)));
        Assert.True(state.IsListed);
        Assert.DoesNotContain(changes, c => c is ServiceWorkerChange.RunningStatusChanged);
        Assert.Equal([10L, 11L], state.Running.Order());
    }

    [Fact]
    public void RunningStatusChanges_AreReportedInOrder_AndAWorkerCountsAsRunningFromStartedToStopped()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)), Versions((10, 1, "activated", "stopped", null)));
        var seen = new List<ServiceWorkerChange>();
        foreach (var (running, listed) in new[] { ("starting", false), ("running", true), ("stopping", true), ("stopped", false) })
        {
            seen.AddRange(state.ApplyVersions(Versions((10, 1, "activated", running, null))));
            Assert.Equal(listed, state.Running.Contains(10));
        }
        Assert.Equal(
            [
                new ServiceWorkerChange.RunningStatusChanged(10, ServiceWorkerRunningStatus.Starting),
                new ServiceWorkerChange.RunningStatusChanged(10, ServiceWorkerRunningStatus.Running),
                new ServiceWorkerChange.RunningStatusChanged(10, ServiceWorkerRunningStatus.Stopping),
                new ServiceWorkerChange.RunningStatusChanged(10, ServiceWorkerRunningStatus.Stopped),
            ], seen);
    }

    [Fact]
    public void SameRunningStatus_IsNoChange()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)), Versions((10, 1, "activated", "running", null)));
        Assert.Empty(state.ApplyVersions(Versions((10, 1, "activated", "running", null))));
    }

    [Fact]
    public void ANewVersionFirstSeenStarting_ReportsStarting()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)));
        Assert.Equal([new ServiceWorkerChange.RunningStatusChanged(10, ServiceWorkerRunningStatus.Starting)],
            state.ApplyVersions(Versions((10, 1, "new", "starting", null))));
    }

    [Fact]
    public void ANewRegistration_CompletesOnceItsWorkerRuns_NotWhenFirstAnnounced()
    {
        var state = Listed();
        Assert.Empty(state.ApplyRegistrations(Registrations((1, "https://a.test/app/", false))));
        state.ApplyVersions(Versions((10, 1, "new", "stopped", null)));
        state.ApplyVersions(Versions((10, 1, "new", "running", null)));
        state.ApplyVersions(Versions((10, 1, "installing", "running", null)));
        Assert.Equal([new ServiceWorkerChange.RegistrationCompleted("https://a.test/app/")], state.ApplyRegistrations(Registrations((1, "https://a.test/app/", false))));
        Assert.Empty(state.ApplyRegistrations(Registrations((2, "https://b.test/", false))));
    }

    [Fact]
    public void AKnownRegistrationsUpdate_CompletesOnlyWhileAWorkerOfItRuns()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)), Versions((10, 1, "activated", "stopped", null)));
        Assert.Empty(state.ApplyRegistrations(Registrations((1, "https://a.test/", false))));
        state.ApplyVersions(Versions((10, 1, "activated", "starting", null)));
        Assert.Equal([new ServiceWorkerChange.RegistrationCompleted("https://a.test/")], state.ApplyRegistrations(Registrations((1, "https://a.test/", false))));
        state.ApplyVersions(Versions((10, 1, "activated", "running", null)));
        Assert.Single(state.ApplyRegistrations(Registrations((1, "https://a.test/", false))));
    }

    [Fact]
    public void AnInstallThatEnded_DoesNotCompleteALaterReload()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)), Versions((10, 1, "activated", "stopped", null)));
        state.ApplyVersions(Versions((11, 1, "installing", "stopped", null)));
        state.ApplyVersions(Versions((11, 1, "installed", "stopped", null)));
        Assert.Empty(state.ApplyRegistrations(Registrations((1, "https://a.test/", false))));
    }

    [Fact]
    public void ADeletedRegistration_IsForgotten()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)));
        Assert.Equal(1, state.RegistrationFor("https://a.test/"));
        Assert.Empty(state.ApplyRegistrations(Registrations((1, "https://a.test/", true))));
        Assert.Null(state.RegistrationFor("https://a.test/"));
        Assert.Null(state.Scope(1));
    }

    [Fact]
    public void RedundantIsReportedOnce_AndAStoppedRedundantVersionIsForgotten()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)), Versions((10, 1, "activated", "running", null)));
        Assert.Equal([new ServiceWorkerChange.VersionRedundant(10)], state.ApplyVersions(Versions((10, 1, "redundant", "running", null))));
        Assert.NotNull(state.Version(10));
        Assert.Equal([new ServiceWorkerChange.RunningStatusChanged(10, ServiceWorkerRunningStatus.Stopped)], state.ApplyVersions(Versions((10, 1, "redundant", "stopped", null))));
        Assert.Null(state.Version(10));
    }

    [Fact]
    public void ATargetId_IsAChange()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)), Versions((10, 1, "activated", "starting", null)));
        Assert.Equal([new ServiceWorkerChange.TargetChanged(10)], state.ApplyVersions(Versions((10, 1, "activated", "starting", "T1"))));
        Assert.Equal("T1", state.Version(10)!.TargetId);
    }

    [Fact]
    public void ActiveVersion_IsTheActivatingOrActivatedOne()
    {
        var state = Listed(Registrations((1, "https://a.test/", false)), Versions((10, 1, "installed", "stopped", null), (11, 1, "activated", "stopped", null)));
        Assert.Equal(11, state.ActiveVersion(1)!.VersionId);
        state.ApplyVersions(Versions((11, 1, "redundant", "stopped", null), (10, 1, "activating", "stopped", null)));
        Assert.Equal(10, state.ActiveVersion(1)!.VersionId);
    }
}

public sealed class ServiceWorkerTaskRulesTests
{
    [Theory]
    [InlineData(ServiceWorkerRunningStatus.Starting, true)]
    [InlineData(ServiceWorkerRunningStatus.Running, true)]
    [InlineData(ServiceWorkerRunningStatus.Stopping, false)]
    [InlineData(ServiceWorkerRunningStatus.Stopped, false)]
    [InlineData(null, false)]
    public void CanStart(ServiceWorkerRunningStatus? status, bool can) => Assert.Equal(can, ServiceWorkerTaskRules.CanStart(status));

    [Theory]
    [InlineData(null, true, "Service worker was not found")]
    [InlineData(ServiceWorkerRunningStatus.Stopped, true, "Service worker is no longer running")]
    [InlineData(ServiceWorkerRunningStatus.Starting, false, "Unknown external request UUID")]
    [InlineData(ServiceWorkerRunningStatus.Starting, true, null)]
    [InlineData(ServiceWorkerRunningStatus.Running, false, null)]
    [InlineData(ServiceWorkerRunningStatus.Stopping, true, null)]
    public void EndError_IsFinishExternalRequests(ServiceWorkerRunningStatus? status, bool pending, string? error)
        => Assert.Equal(error, ServiceWorkerTaskRules.EndError(status, pending));

    [Fact]
    public void ConsoleFilter_DropsWhatCameBeforeTheListener_AndReplays()
    {
        var filter = new ServiceWorkerConsoleFilter { Since = 100 };
        Assert.False(filter.Accept(1, 99));
        Assert.True(filter.Accept(1, 100));
        Assert.True(filter.Accept(1, 150));
        Assert.False(filter.Accept(1, 120));
        Assert.True(filter.Accept(2, 120));
        filter.Forget(1);
        Assert.True(filter.Accept(1, 120));
    }
}

public sealed class ServiceWorkersTests
{
    private sealed class FakeDevTools : IServiceWorkerDevTools
    {
        public List<(string Method, string Params, string? SessionId)> Sent { get; } = [];
        public bool FailStart { get; set; }
        public TaskCompletionSource<JsonElement>? PendingAttach { get; set; }

        public Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? writeParams = null, string? sessionId = null)
        {
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writeParams?.Invoke(writer);
                writer.WriteEndObject();
            }
            Sent.Add((method, Encoding.UTF8.GetString(buffer.ToArray()), sessionId));
            if (method == "ServiceWorker.startWorker" && FailStart) return Task.FromException<JsonElement>(new InvalidOperationException("closed"));
            if (method == "Target.attachToTarget" && PendingAttach is { } pending) return pending.Task;
            var result = method == "Target.attachToTarget" ? $$"""{"sessionId":"S{{Sent.Count}}"}""" : "{}";
            return Task.FromResult(ServiceWorkerProtocolTests.J(result));
        }
    }

    private static (ServiceWorkers Workers, FakeDevTools DevTools) Listed(string running = "running", string? target = "T10", string status = "activated")
    {
        var devTools = new FakeDevTools();
        var workers = new ServiceWorkers(devTools);
        workers.OnDevToolsEvent("ServiceWorker.workerRegistrationUpdated", ServiceWorkerStateTests.Registrations((1, "https://a.test/", false)), null);
        workers.OnDevToolsEvent("ServiceWorker.workerVersionUpdated", ServiceWorkerStateTests.Versions((10, 1, status, running, target)), null);
        return (workers, devTools);
    }

    private static void Version(ServiceWorkers workers, string status, string running, string? target = "T10")
        => workers.OnDevToolsEvent("ServiceWorker.workerVersionUpdated", ServiceWorkerStateTests.Versions((10, 1, status, running, target)), null);

    [Fact]
    public void GetAllRunning_ListsScriptAndScope_WithNoProcessId()
    {
        var (workers, _) = Listed();
        var all = workers.GetAllRunning();
        Assert.Equal([10L], all.Keys);
        Assert.Equal(new ServiceWorkerInfo("https://a.test/sw10.js", "https://a.test/", 0), all[10]);
        Assert.Equal(all[10], workers.GetInfoFromVersionId(10));
    }

    [Fact]
    public void GetInfoFromVersionId_ThrowsElectronsErrorForAWorkerNotRunning()
    {
        var (workers, _) = Listed(running: "stopped");
        Assert.Empty(workers.GetAllRunning());
        Assert.Equal("Could not find service worker with that version_id", Assert.Throws<InvalidOperationException>(() => workers.GetInfoFromVersionId(10)).Message);
        Assert.Throws<InvalidOperationException>(() => workers.GetInfoFromVersionId(99));
#pragma warning disable CS0618 // The deprecated alias is under test.
        Assert.Throws<InvalidOperationException>(() => workers.GetFromVersionId(10));
#pragma warning restore CS0618
    }

    [Fact]
    public void RunningStatusChanged_IsRaisedWithTheVersionAndStatus()
    {
        var (workers, _) = Listed(running: "stopped");
        var seen = new List<ServiceWorkerRunningStatusChangedEventArgs>();
        workers.RunningStatusChanged += (_, e) => seen.Add(e);
        Version(workers, "activated", "starting");
        Version(workers, "activated", "running");
        Assert.Equal([new(10, ServiceWorkerRunningStatus.Starting), new(10, ServiceWorkerRunningStatus.Running)], seen);
    }

    [Fact]
    public void RegistrationCompleted_IsRaisedWithTheScope()
    {
        var (workers, _) = Listed();
        var seen = new List<string>();
        workers.RegistrationCompleted += (_, e) => seen.Add(e.Scope);
        workers.OnDevToolsEvent("ServiceWorker.workerRegistrationUpdated", ServiceWorkerStateTests.Registrations((1, "https://a.test/", false)), null);
        Assert.Equal(["https://a.test/"], seen);
    }

    [Fact]
    public void GetWorkerFromVersionId_IsOneObjectPerVersion_AndNullForUnknownOrRedundant()
    {
        var (workers, _) = Listed();
        var worker = workers.GetWorkerFromVersionId(10);
        Assert.NotNull(worker);
        Assert.Same(worker, workers.GetWorkerFromVersionId(10));
        Assert.Equal((10L, "https://a.test/", "https://a.test/sw10.js", false), (worker.VersionId, worker.Scope, worker.ScriptUrl, worker.IsDestroyed()));
        Assert.Null(workers.GetWorkerFromVersionId(11));
        Version(workers, "redundant", "running");
        Assert.Same(worker, workers.GetWorkerFromVersionId(10));
        var (fresh, _) = Listed(status: "redundant");
        Assert.Null(fresh.GetWorkerFromVersionId(10));
    }

    [Fact]
    public void ARunningRedundantWorker_IsDestroyedOnceStopped()
    {
        var (workers, _) = Listed();
        var worker = workers.GetWorkerFromVersionId(10)!;
        Version(workers, "redundant", "running");
        Assert.False(worker.IsDestroyed());
        Version(workers, "redundant", "stopped");
        Assert.True(worker.IsDestroyed());
        Assert.Equal(("", ""), (worker.Scope, worker.ScriptUrl));
        Assert.Null(workers.GetWorkerFromVersionId(10));
    }

    [Fact]
    public void AStoppedRedundantWorker_IsDestroyedAtOnce()
    {
        var (workers, _) = Listed(running: "stopped");
        var worker = workers.GetWorkerFromVersionId(10)!;
        Version(workers, "redundant", "stopped");
        Assert.True(worker.IsDestroyed());
    }

    [Fact]
    public void AStoppedWorker_IsNotDestroyed()
    {
        var (workers, _) = Listed();
        var worker = workers.GetWorkerFromVersionId(10)!;
        Version(workers, "activated", "stopped");
        Assert.False(worker.IsDestroyed());
        Assert.Same(worker, workers.GetWorkerFromVersionId(10));
    }

    [Fact]
    public void Send_IsNotSupported()
    {
        var (workers, _) = Listed();
        var worker = workers.GetWorkerFromVersionId(10)!;
        Assert.Throws<NotSupportedException>(() => worker.Send("ch", 1));
        Assert.Throws<ArgumentNullException>(() => worker.Send(null!));
    }

    [Fact]
    public void StartTask_HoldsADevToolsSessionUntilItEnds()
    {
        var (workers, devTools) = Listed();
        var task = workers.GetWorkerFromVersionId(10)!.StartTask();
        Assert.Contains(devTools.Sent, s => s.Method == "Target.attachToTarget" && s.Params.Contains("\"targetId\":\"T10\""));
        Assert.DoesNotContain(devTools.Sent, s => s.Method == "Target.detachFromTarget");
        task.End();
        Assert.Contains(devTools.Sent, s => s.Method == "Target.detachFromTarget");
        task.End();
    }

    [Fact]
    public void StartTask_RefusesAWorkerNotRunning_OrDestroyed()
    {
        var (workers, _) = Listed(running: "stopped");
        var worker = workers.GetWorkerFromVersionId(10)!;
        Assert.Equal("Unable to start service worker task.", Assert.Throws<InvalidOperationException>(() => worker.StartTask()).Message);
        Version(workers, "redundant", "stopped");
        Assert.Equal("ServiceWorkerMain is destroyed", Assert.Throws<InvalidOperationException>(() => worker.StartTask()).Message);
    }

    [Fact]
    public void EndingATaskAfterTheWorkerStopped_IsElectronsError()
    {
        var (workers, devTools) = Listed();
        var task = workers.GetWorkerFromVersionId(10)!.StartTask();
        Version(workers, "activated", "stopped");
        Assert.Contains(devTools.Sent, s => s.Method == "Target.detachFromTarget");
        Assert.Equal("Service worker is no longer running", Assert.Throws<InvalidOperationException>(task.End).Message);
    }

    [Fact]
    public void ConsoleMessage_AttachesToRunningWorkers_AndRaisesTheirConsoleCalls()
    {
        var (workers, devTools) = Listed();
        var seen = new List<ServiceWorkerConsoleMessageEventArgs>();
        EventHandler<ServiceWorkerConsoleMessageEventArgs> handler = (_, e) => seen.Add(e);
        workers.ConsoleMessage += handler;
        Assert.Single(devTools.Sent, s => s.Method == "Target.attachToTarget");
        const string session = "S1";
        Assert.Contains(devTools.Sent, s => s.Method == "Runtime.enable" && s.SessionId == session);
        Assert.Contains(devTools.Sent, s => s.Method == "Log.enable" && s.SessionId == session);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000;
        workers.OnDevToolsEvent("Runtime.consoleAPICalled", ServiceWorkerProtocolTests.J("""{"type":"log","args":[{"type":"string","value":"old"}],"timestamp":1}"""), session);
        workers.OnDevToolsEvent("Runtime.consoleAPICalled", ServiceWorkerProtocolTests.J($$"""{"type":"log","args":[{"type":"string","value":"hi"}],"timestamp":{{now}}}"""), session);
        workers.OnDevToolsEvent("Runtime.consoleAPICalled", ServiceWorkerProtocolTests.J($$"""{"type":"log","args":[{"type":"string","value":"page"}],"timestamp":{{now}}}"""), null);
        Assert.Equal(["hi"], seen.Select(m => m.Message));
        Assert.Equal(10, seen[0].VersionId);
        workers.ConsoleMessage -= handler;
        Assert.Contains(devTools.Sent, s => s.Method == "Target.detachFromTarget" && s.Params.Contains("\"sessionId\":\"S1\""));
    }

    [Fact]
    public void AStoppedWorkersTasks_DoNotOutliveIt()
    {
        var (workers, devTools) = Listed();
        workers.GetWorkerFromVersionId(10)!.StartTask();
        Version(workers, "activated", "stopped");
        devTools.Sent.Clear();
        Version(workers, "activated", "starting");
        Assert.DoesNotContain(devTools.Sent, s => s.Method == "Target.attachToTarget");
    }

    [Fact]
    public void ARedundantWorker_IsDetached_SoChromiumCanStopIt()
    {
        var (workers, devTools) = Listed();
        workers.GetWorkerFromVersionId(10)!.StartTask();
        Version(workers, "redundant", "running");
        Assert.Contains(devTools.Sent, s => s.Method == "Target.detachFromTarget");
    }

    [Fact]
    public void AnAttachLandingAfterTheWorkerStopped_IsUndone()
    {
        var (workers, devTools) = Listed();
        devTools.PendingAttach = new TaskCompletionSource<JsonElement>();
        workers.GetWorkerFromVersionId(10)!.StartTask();
        Version(workers, "activated", "stopped");
        devTools.PendingAttach.SetResult(ServiceWorkerProtocolTests.J("""{"sessionId":"late"}"""));
        // The attach's continuation may run on another thread.
        Assert.True(SpinWait.SpinUntil(() => devTools.Sent.ToArray().Any(s => s.Method == "Target.detachFromTarget" && s.Params.Contains("\"sessionId\":\"late\"")), 2000));
    }

    [Fact]
    public void ADetachedSession_RaisesNoMoreMessages()
    {
        var (workers, _) = Listed();
        var seen = 0;
        workers.ConsoleMessage += (_, _) => seen++;
        var later = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000;
        var call = ServiceWorkerProtocolTests.J($$"""{"type":"log","args":[],"timestamp":{{later}}}""");
        workers.OnDevToolsEvent("Runtime.consoleAPICalled", call, "S1");
        workers.OnDevToolsEvent("Target.detachedFromTarget", ServiceWorkerProtocolTests.J("""{"sessionId":"S1","targetId":"T10"}"""), null);
        workers.OnDevToolsEvent("Runtime.consoleAPICalled", call, "S1");
        Assert.Equal(1, seen);
    }

    [Fact]
    public void ConsoleMessage_LeavesStoppedWorkersAlone()
    {
        var (workers, devTools) = Listed(running: "stopped");
        workers.ConsoleMessage += (_, _) => { };
        Assert.DoesNotContain(devTools.Sent, s => s.Method == "Target.attachToTarget");
        Version(workers, "activated", "starting");
        Assert.Contains(devTools.Sent, s => s.Method == "Target.attachToTarget");
    }

    [Fact]
    public async Task StartWorkerForScope_StartsTheActiveWorker_AndResolvesOnceRunning()
    {
        var (workers, devTools) = Listed(running: "stopped");
        var start = workers.StartWorkerForScopeAsync("https://a.test");
        Assert.Contains(devTools.Sent, s => s.Method == "ServiceWorker.startWorker" && s.Params.Contains("\"scopeURL\":\"https://a.test/\""));
        Assert.False(start.IsCompleted);
        Version(workers, "activated", "starting");
        Assert.False(start.IsCompleted);
        Version(workers, "activated", "running");
        Assert.Equal(10, (await start.WaitAsync(TimeSpan.FromSeconds(5))).VersionId);
    }

    [Fact]
    public async Task StartWorkerForScope_ResolvesARunningWorkerAtOnce()
    {
        var (workers, devTools) = Listed();
        Assert.Equal(10, (await workers.StartWorkerForScopeAsync("https://a.test/").WaitAsync(TimeSpan.FromSeconds(5))).VersionId);
        Assert.DoesNotContain(devTools.Sent, s => s.Method == "ServiceWorker.startWorker");
    }

    [Fact]
    public async Task StartWorkerForScope_FailsWithoutAnActiveWorker_OrWhenItStops()
    {
        var (workers, _) = Listed(running: "stopped");
        Assert.Equal("Failed to start service worker.", (await Assert.ThrowsAsync<InvalidOperationException>(() => workers.StartWorkerForScopeAsync("https://b.test/").WaitAsync(TimeSpan.FromSeconds(5)))).Message);
        var (installed, _) = Listed(running: "stopped", status: "installed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => installed.StartWorkerForScopeAsync("https://a.test/").WaitAsync(TimeSpan.FromSeconds(5)));
        var start = workers.StartWorkerForScopeAsync("https://a.test/");
        Version(workers, "activated", "starting");
        Version(workers, "activated", "stopped");
        await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StartWorkerForScope_WaitsForTheFirstLists()
    {
        var workers = new ServiceWorkers(new FakeDevTools());
        var start = workers.StartWorkerForScopeAsync("https://a.test/");
        Assert.False(start.IsCompleted);
        workers.OnDevToolsEvent("ServiceWorker.workerRegistrationUpdated", ServiceWorkerStateTests.Registrations((1, "https://a.test/", false)), null);
        Assert.False(start.IsCompleted);
        workers.OnDevToolsEvent("ServiceWorker.workerVersionUpdated", ServiceWorkerStateTests.Versions((10, 1, "activated", "running", null)), null);
        Assert.Equal(10, (await start.WaitAsync(TimeSpan.FromSeconds(5))).VersionId);
    }
}
