using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Riftway;

/// <summary>DevTools' <c>ServiceWorker</c> and console payloads read as Electron's service worker values.</summary>
internal static class ServiceWorkerProtocol
{
    /// <summary>A <c>ServiceWorkerVersionRunningStatus</c>; null for a value the protocol does not define.</summary>
    internal static ServiceWorkerRunningStatus? RunningStatus(string? value) => value switch
    {
        "starting" => ServiceWorkerRunningStatus.Starting,
        "running" => ServiceWorkerRunningStatus.Running,
        "stopping" => ServiceWorkerRunningStatus.Stopping,
        "stopped" => ServiceWorkerRunningStatus.Stopped,
        _ => null,
    };

    /// <summary>Electron's <c>runningStatus</c> string (<c>service_worker_converter.cc</c>).</summary>
    internal static string Name(ServiceWorkerRunningStatus status) => status switch
    {
        ServiceWorkerRunningStatus.Starting => "starting",
        ServiceWorkerRunningStatus.Running => "running",
        ServiceWorkerRunningStatus.Stopping => "stopping",
        _ => "stopped",
    };

    /// <summary>The level Chromium reports for a <c>Runtime.consoleAPICalled</c> type, as V8's
    /// <c>V8ConsoleMessage::createForConsoleAPI</c> picks it and Blink's <c>V8MessageLevelToMessageLevel</c> maps it.</summary>
    internal static ConsoleMessageLevel ConsoleApiLevel(string? type) => type switch
    {
        "debug" or "count" or "timeEnd" => ConsoleMessageLevel.Debug,
        "error" or "assert" => ConsoleMessageLevel.Error,
        "warning" => ConsoleMessageLevel.Warning,
        _ => ConsoleMessageLevel.Info,
    };

    /// <summary>A <c>Log.LogEntry</c> level.</summary>
    internal static ConsoleMessageLevel LogLevel(string? level) => level switch
    {
        "verbose" => ConsoleMessageLevel.Debug,
        "warning" => ConsoleMessageLevel.Warning,
        "error" => ConsoleMessageLevel.Error,
        _ => ConsoleMessageLevel.Info,
    };

    /// <summary>Electron's <c>source</c> for a <c>Log.LogEntry</c> source (<c>MessageSourceToString</c>).</summary>
    internal static string LogSource(string? source) => source switch
    {
        "xml" or "javascript" or "network" or "storage" or "rendering" or "security" or "deprecation" or "worker"
            or "violation" or "intervention" or "recommendation" => source,
        _ => "other",
    };

    /// <summary>The console message of a <c>Runtime.consoleAPICalled</c>; null for <c>console.clear()</c>, which
    /// V8 never reports to Blink.</summary>
    internal static ServiceWorkerConsoleMessageEventArgs? FromConsoleApi(JsonElement args, long versionId)
    {
        var type = args.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "clear") return null;
        var text = new StringBuilder();
        if (args.TryGetProperty("args", out var values))
        {
            var first = true;
            foreach (var value in values.EnumerateArray())
            {
                if (!first) text.Append(' ');
                first = false;
                AppendValue(text, value);
            }
        }
        var (url, line) = TopFrame(args);
        return new ServiceWorkerConsoleMessageEventArgs(text.ToString(), versionId, "console-api", ConsoleApiLevel(type), url, line);
    }

    /// <summary>The console message of a <c>Log.entryAdded</c>.</summary>
    internal static ServiceWorkerConsoleMessageEventArgs FromLogEntry(JsonElement args, long versionId)
    {
        var entry = args.GetProperty("entry");
        var url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        // InspectorLogAgent sends Blink's 1-based line less one, and leaves it out when Blink has none.
        var line = entry.TryGetProperty("lineNumber", out var l) ? l.GetInt32() + 1 : 0;
        return new ServiceWorkerConsoleMessageEventArgs(entry.GetProperty("text").GetString() ?? "", versionId,
            LogSource(entry.TryGetProperty("source", out var s) ? s.GetString() : null),
            LogLevel(entry.TryGetProperty("level", out var v) ? v.GetString() : null), url, line);
    }

    /// <summary>A console event's time, in milliseconds since the epoch; 0 when it has none.</summary>
    internal static double Timestamp(string method, JsonElement args)
    {
        var holder = method == "Log.entryAdded" && args.TryGetProperty("entry", out var entry) ? entry : args;
        return holder.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0;
    }

    // V8 reports the top frame's URL and its 1-based line; the protocol's call frames are 0-based.
    private static (string Url, int Line) TopFrame(JsonElement args)
    {
        if (!args.TryGetProperty("stackTrace", out var stack) || !stack.TryGetProperty("callFrames", out var frames)) return ("", 0);
        foreach (var frame in frames.EnumerateArray())
            return (frame.GetProperty("url").GetString() ?? "", frame.GetProperty("lineNumber").GetInt32() + 1);
        return ("", 0);
    }

    // V8ValueStringBuilder over a RemoteObject: exact for primitives, functions, dates and regexps; arrays come from the
    // object preview, which drops nested values' contents (drift SW6).
    private static void AppendValue(StringBuilder text, JsonElement value, bool inArray = false)
    {
        var type = value.TryGetProperty("type", out var t) ? t.GetString() : null;
        var subtype = value.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        switch (type)
        {
            case "string":
                text.Append(value.GetProperty("value").GetString());
                return;
            case "number":
                if (value.TryGetProperty("unserializableValue", out var special)) text.Append(special.GetString() == "-0" ? "0" : special.GetString());
                else text.Append(value.GetProperty("value").GetRawText());
                return;
            case "boolean":
                text.Append(value.GetProperty("value").GetBoolean() ? "true" : "false");
                return;
            case "undefined":
                if (!inArray) text.Append("undefined");
                return;
            case "bigint":
                text.Append(value.GetProperty("unserializableValue").GetString());
                return;
            case "symbol" or "function":
                text.Append(Description(value));
                return;
        }
        switch (subtype)
        {
            case "null":
                if (!inArray) text.Append("null");
                return;
            case "array":
                AppendPreview(text, value);
                return;
            case "proxy":
                text.Append("[object Proxy]");
                return;
            case "date" or "regexp":
                text.Append(Description(value));
                return;
            case "error":
                // The description is the stack; ToString() is its first part, up to the frames.
                var stack = Description(value);
                var frames = stack.IndexOf("\n    at ", StringComparison.Ordinal);
                text.Append(frames < 0 ? stack : stack[..frames]);
                return;
        }
        // ObjectProtoToString: a built-in's tag is its class name; a plain or class instance object is "Object".
        var className = subtype is null ? "Object" : value.TryGetProperty("className", out var c) ? c.GetString() : "Object";
        text.Append("[object ").Append(className).Append(']');
    }

    private static void AppendPreview(StringBuilder text, JsonElement array)
    {
        if (!array.TryGetProperty("preview", out var preview) || !preview.TryGetProperty("properties", out var properties)) return;
        var first = true;
        foreach (var property in properties.EnumerateArray())
        {
            if (!int.TryParse(property.GetProperty("name").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            if (!first) text.Append(',');
            first = false;
            var type = property.GetProperty("type").GetString();
            var subtype = property.TryGetProperty("subtype", out var s) ? s.GetString() : null;
            var shown = property.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            if (type is "undefined" || subtype is "null") continue;
            if (type is "string" or "number" or "boolean" or "bigint" or "symbol" or "function" || subtype is "date" or "regexp") text.Append(shown);
            else if (subtype != "array") text.Append("[object Object]");
        }
    }

    private static string Description(JsonElement value) => value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
}

/// <summary>A version as DevTools' <c>ServiceWorker.workerVersionUpdated</c> last described it.</summary>
internal sealed class ServiceWorkerVersionRecord
{
    public required long VersionId { get; init; }
    public required long RegistrationId { get; init; }
    public required string ScriptUrl { get; init; }
    public ServiceWorkerRunningStatus RunningStatus { get; set; }
    public string Status { get; set; } = "new";
    public string? TargetId { get; set; }
}

/// <summary>What one DevTools update changed, in the order Electron's observer calls would see it.</summary>
internal abstract record ServiceWorkerChange
{
    internal sealed record RunningStatusChanged(long VersionId, ServiceWorkerRunningStatus Status) : ServiceWorkerChange;
    internal sealed record VersionRedundant(long VersionId) : ServiceWorkerChange;
    internal sealed record RegistrationCompleted(string Scope) : ServiceWorkerChange;
    internal sealed record TargetChanged(long VersionId) : ServiceWorkerChange;
}

/// <summary>A session's registrations and versions, rebuilt from DevTools' <c>ServiceWorker</c> events, as
/// <c>ServiceWorkerContextWrapper</c> keeps them for Electron.</summary>
/// <remarks>Not thread-safe; <see cref="ServiceWorkers"/> locks around it.</remarks>
internal sealed class ServiceWorkerState
{
    private readonly Dictionary<long, string> _registrations = [];
    private readonly Dictionary<long, ServiceWorkerVersionRecord> _versions = [];
    // ServiceWorkerContextWrapper::running_service_workers_: added when started, removed only once stopped.
    private readonly HashSet<long> _running = [];
    private bool _registrationsListed;
    private bool _versionsListed;

    /// <summary>Whether both of the first lists DevTools sends after <c>ServiceWorker.enable</c> have come.</summary>
    internal bool IsListed => _registrationsListed && _versionsListed;

    internal IReadOnlyCollection<long> Running => _running;

    internal ServiceWorkerVersionRecord? Version(long versionId) => _versions.GetValueOrDefault(versionId);

    internal string? Scope(long registrationId) => _registrations.GetValueOrDefault(registrationId);

    internal IEnumerable<ServiceWorkerVersionRecord> Versions => _versions.Values;

    /// <summary>The registration of exactly this scope, as <c>FindRegistrationForScope</c> matches it.</summary>
    internal long? RegistrationFor(string scope)
    {
        foreach (var (id, s) in _registrations)
            if (s == scope) return id;
        return null;
    }

    /// <summary>The registration's active version: the one DevTools shows activating or activated.</summary>
    internal ServiceWorkerVersionRecord? ActiveVersion(long registrationId)
        => _versions.Values.FirstOrDefault(v => v.RegistrationId == registrationId && v.Status is "activating" or "activated");

    /// <summary>Applies <c>ServiceWorker.workerRegistrationUpdated</c>.</summary>
    internal List<ServiceWorkerChange> ApplyRegistrations(JsonElement args)
    {
        var changes = new List<ServiceWorkerChange>();
        _registrationsListed = true;
        foreach (var registration in args.GetProperty("registrations").EnumerateArray())
        {
            var id = long.Parse(registration.GetProperty("registrationId").GetString()!, CultureInfo.InvariantCulture);
            var scope = registration.GetProperty("scopeURL").GetString() ?? "";
            if (registration.GetProperty("isDeleted").GetBoolean())
            {
                _registrations.Remove(id);
                continue;
            }
            _registrations[id] = scope;
            // OnNewLiveRegistration (a new registration, or one reloaded from storage) comes before anything can start
            // its worker; the register job resolves (OnRegistrationCompleted) with a worker running.
            if (_versions.Values.Any(v => v.RegistrationId == id && v.RunningStatus is ServiceWorkerRunningStatus.Starting or ServiceWorkerRunningStatus.Running))
                changes.Add(new ServiceWorkerChange.RegistrationCompleted(scope));
        }
        return changes;
    }

    /// <summary>Applies <c>ServiceWorker.workerVersionUpdated</c>.</summary>
    internal List<ServiceWorkerChange> ApplyVersions(JsonElement args)
    {
        var changes = new List<ServiceWorkerChange>();
        var listing = !_versionsListed;
        _versionsListed = true;
        foreach (var version in args.GetProperty("versions").EnumerateArray())
        {
            var id = long.Parse(version.GetProperty("versionId").GetString()!, CultureInfo.InvariantCulture);
            var running = ServiceWorkerProtocol.RunningStatus(version.GetProperty("runningStatus").GetString()) ?? ServiceWorkerRunningStatus.Stopped;
            var status = version.GetProperty("status").GetString() ?? "new";
            var targetId = version.TryGetProperty("targetId", out var t) ? t.GetString() : null;
            if (!_versions.TryGetValue(id, out var record))
            {
                record = new ServiceWorkerVersionRecord
                {
                    VersionId = id,
                    RegistrationId = long.Parse(version.GetProperty("registrationId").GetString()!, CultureInfo.InvariantCulture),
                    ScriptUrl = version.GetProperty("scriptURL").GetString() ?? "",
                    RunningStatus = listing ? running : ServiceWorkerRunningStatus.Stopped,
                    Status = listing ? status : "new",
                };
                _versions[id] = record;
                if (listing && running is ServiceWorkerRunningStatus.Running or ServiceWorkerRunningStatus.Stopping) _running.Add(id);
            }

            if (record.Status != status)
            {
                var wasRedundant = record.Status == "redundant";
                record.Status = status;
                if (status == "redundant" && !wasRedundant && !listing) changes.Add(new ServiceWorkerChange.VersionRedundant(id));
            }
            if (record.TargetId != targetId)
            {
                record.TargetId = targetId;
                changes.Add(new ServiceWorkerChange.TargetChanged(id));
            }
            if (record.RunningStatus != running)
            {
                record.RunningStatus = running;
                if (running == ServiceWorkerRunningStatus.Running) _running.Add(id);
                else if (running == ServiceWorkerRunningStatus.Stopped) _running.Remove(id);
                changes.Add(new ServiceWorkerChange.RunningStatusChanged(id, running));
            }
            // ServiceWorkerContextWatcher forgets a version once it is stopped and redundant; so does the context.
            if (record.Status == "redundant" && record.RunningStatus == ServiceWorkerRunningStatus.Stopped) _versions.Remove(id);
        }
        return changes;
    }
}

/// <summary>Whether a console event reaches <see cref="ServiceWorkers.ConsoleMessage"/>.</summary>
/// <remarks><c>Runtime.enable</c> and <c>Log.enable</c> replay what the worker logged before; Electron's listeners
/// only see what is logged while they listen, and each message once.</remarks>
internal sealed class ServiceWorkerConsoleFilter
{
    private readonly Dictionary<long, double> _last = [];

    /// <summary>When the first listener was added, in milliseconds since the epoch.</summary>
    internal double Since { get; set; }

    internal bool Accept(long versionId, double timestamp)
    {
        if (timestamp < Since) return false;
        if (_last.TryGetValue(versionId, out var last) && timestamp < last) return false;
        _last[versionId] = timestamp;
        return true;
    }

    internal void Forget(long versionId) => _last.Remove(versionId);
}

/// <summary>Electron's <c>ServiceWorkerMain::FinishExternalRequest</c> errors, for a task's end.</summary>
internal static class ServiceWorkerTaskRules
{
    /// <summary>Whether <c>StartingExternalRequest</c> accepts a task: the version exists and has not begun stopping.</summary>
    internal static bool CanStart(ServiceWorkerRunningStatus? status)
        => status is ServiceWorkerRunningStatus.Starting or ServiceWorkerRunningStatus.Running;

    /// <summary>The error <c>FinishedExternalRequest</c> reports, or null for success.</summary>
    /// <param name="status">The version's running status; null when the version is gone.</param>
    /// <param name="pending">Whether this task is still open.</param>
    internal static string? EndError(ServiceWorkerRunningStatus? status, bool pending) => status switch
    {
        null => "Service worker was not found",
        ServiceWorkerRunningStatus.Stopped => "Service worker is no longer running",
        // ServiceWorkerVersion::FinishExternalRequest: an unknown id is an error only while starting.
        ServiceWorkerRunningStatus.Starting when !pending => "Unknown external request UUID",
        _ => null,
    };
}
