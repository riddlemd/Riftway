using System.Text.Json;

namespace Riftway;

/// <summary>A service worker's running status. Electron's <c>runningStatus</c>.</summary>
public enum ServiceWorkerRunningStatus
{
    /// <summary><c>starting</c>.</summary>
    Starting,

    /// <summary><c>running</c>.</summary>
    Running,

    /// <summary><c>stopping</c>.</summary>
    Stopping,

    /// <summary><c>stopped</c>.</summary>
    Stopped,
}

/// <summary>A running service worker. Electron's <c>ServiceWorkerInfo</c>, as <c>getAllRunning()</c> fills it.</summary>
/// <param name="ScriptUrl">The script the worker runs.</param>
/// <param name="Scope">The base URL the worker is registered for.</param>
/// <param name="RenderProcessId">Electron's child process ID: always 0, as DevTools names no process (drift SW2).</param>
/// <remarks>No <c>versionId</c>: Electron's documentation lists one, but its <c>ServiceWorkerRunningInfoToDict</c> never
/// sets it; the version ID is the key of <see cref="ServiceWorkers.GetAllRunning"/>.</remarks>
public sealed record ServiceWorkerInfo(string ScriptUrl, string Scope, int RenderProcessId);

/// <summary>A message a service worker logged. Electron's <c>console-message</c> details.</summary>
/// <param name="Message">The message: the console call's arguments as strings, joined by spaces.</param>
/// <param name="VersionId">The version that logged it.</param>
/// <param name="Source"><c>console-api</c> for console calls, else Blink's source (<c>network</c>, <c>security</c>, ...).</param>
/// <param name="Level">Its level: Electron's 0 to 3.</param>
/// <param name="SourceUrl">The script it came from.</param>
/// <param name="LineNumber">The line it came from.</param>
public sealed record ServiceWorkerConsoleMessageEventArgs(string Message, long VersionId, string Source, ConsoleMessageLevel Level, string SourceUrl, int LineNumber);

/// <summary>A registration completed. Electron's <c>registration-completed</c> details.</summary>
/// <param name="Scope">The base URL the worker is registered for.</param>
public sealed record ServiceWorkerRegistrationCompletedEventArgs(string Scope);

/// <summary>A version's running status changed. Electron's <c>running-status-changed</c> details.</summary>
public sealed record ServiceWorkerRunningStatusChangedEventArgs(long VersionId, ServiceWorkerRunningStatus RunningStatus);

/// <summary>The DevTools session <see cref="ServiceWorkers"/> drives: the session's hidden page.</summary>
internal interface IServiceWorkerDevTools
{
    Task<JsonElement> SendAsync(string method, Action<System.Text.Json.Utf8JsonWriter>? writeParams = null, string? sessionId = null);
}

/// <summary>A session's service workers. Electron's <c>ses.serviceWorkers</c> (<c>ServiceWorkers</c>).</summary>
/// <remarks>Read through DevTools' <c>ServiceWorker</c> domain on the session's hidden page, from the first use of
/// <see cref="Session.ServiceWorkers"/>: until its first lists arrive the methods see no workers (drift SW1). Events are
/// raised on the UI thread.</remarks>
public sealed class ServiceWorkers
{
    private readonly Lock _gate = new();
    private readonly IServiceWorkerDevTools _devTools;
    private readonly ServiceWorkerState _state = new();
    private readonly ServiceWorkerConsoleFilter _consoleFilter = new();
    // ServiceWorkerMain's version ID map: a destroyed wrapper leaves it, and the next lookup makes a new one.
    private readonly Dictionary<long, ServiceWorkerMain> _workers = [];
    private readonly Dictionary<long, Attachment> _attachments = [];
    private readonly Dictionary<string, long> _sessions = [];
    private readonly List<(long VersionId, TaskCompletionSource<ServiceWorkerMain> Done)> _starts = [];
    private readonly TaskCompletionSource _listed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EventHandler<ServiceWorkerConsoleMessageEventArgs>? _consoleMessage;

    internal ServiceWorkers(IServiceWorkerDevTools devTools) => _devTools = devTools;

    /// <summary>A service worker logged something. Electron's <c>console-message</c> event.</summary>
    /// <remarks>While any handler is added, Riftway attaches DevTools to every starting or running worker of the session,
    /// which Chromium then never stops for being idle (drift SW3).</remarks>
    public event EventHandler<ServiceWorkerConsoleMessageEventArgs>? ConsoleMessage
    {
        add
        {
            List<Action> actions;
            lock (_gate)
            {
                if (_consoleMessage is null) _consoleFilter.Since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _consoleMessage += value;
                actions = UpdateAllAttachments();
            }
            foreach (var action in actions) action();
        }
        remove
        {
            List<Action> actions;
            lock (_gate)
            {
                _consoleMessage -= value;
                actions = UpdateAllAttachments();
            }
            foreach (var action in actions) action();
        }
    }

    /// <summary>A registration completed. Electron's <c>registration-completed</c> event.</summary>
    /// <remarks>DevTools reports registrations without saying why; see drift SW4 for when it is raised.</remarks>
    public event EventHandler<ServiceWorkerRegistrationCompletedEventArgs>? RegistrationCompleted;

    /// <summary>A version's running status changed. Electron's <c>running-status-changed</c> event.</summary>
    public event EventHandler<ServiceWorkerRunningStatusChangedEventArgs>? RunningStatusChanged;

    /// <summary>The running workers, by version ID. Electron's <c>serviceWorkers.getAllRunning()</c>.</summary>
    /// <remarks>As Electron's, a worker is listed from when it started until it has stopped.</remarks>
    public IReadOnlyDictionary<long, ServiceWorkerInfo> GetAllRunning()
    {
        lock (_gate) return _state.Running.ToDictionary(id => id, InfoLocked);
    }

    /// <summary>The running worker of a version. Electron's <c>serviceWorkers.getInfoFromVersionID()</c>.</summary>
    /// <exception cref="InvalidOperationException">No running worker has that version.</exception>
    public ServiceWorkerInfo GetInfoFromVersionId(long versionId)
    {
        lock (_gate)
        {
            if (!_state.Running.Contains(versionId)) throw new InvalidOperationException("Could not find service worker with that version_id");
            return InfoLocked(versionId);
        }
    }

    /// <summary>Electron's deprecated <c>serviceWorkers.getFromVersionID()</c>: <see cref="GetInfoFromVersionId"/>
    /// after a warning.</summary>
    /// <exception cref="InvalidOperationException">No running worker has that version.</exception>
    [Obsolete("Use GetInfoFromVersionId, as Electron deprecates serviceWorkers.getFromVersionID.")]
    public ServiceWorkerInfo GetFromVersionId(long versionId)
    {
        // util::EmitWarning on every call, not once.
        Console.Error.WriteLine("[Riftway] ServiceWorkersDeprecateGetFromVersionID: The session.serviceWorkers.getFromVersionID API is deprecated, use session.serviceWorkers.getInfoFromVersionID instead.");
        return GetInfoFromVersionId(versionId);
    }

    /// <summary>The worker of a version; null when the version is gone or redundant. Electron's
    /// <c>serviceWorkers.getWorkerFromVersionID()</c>.</summary>
    public ServiceWorkerMain? GetWorkerFromVersionId(long versionId)
    {
        lock (_gate) return WorkerLocked(versionId);
    }

    /// <summary>Starts the active worker of exactly <paramref name="scope"/>, or finds it running. Electron's
    /// <c>serviceWorkers.startWorkerForScope()</c>.</summary>
    /// <remarks>The task fails with <see cref="InvalidOperationException"/> when the scope has no registration with an
    /// active worker, or the worker fails to start.</remarks>
    public async Task<ServiceWorkerMain> StartWorkerForScopeAsync(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await _listed.Task.ConfigureAwait(false);
        var spec = Gurl.Spec(scope);
        TaskCompletionSource<ServiceWorkerMain> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var active = _state.RegistrationFor(spec) is { } registration ? _state.ActiveVersion(registration) : null;
            if (active is null) throw StartFailed();
            if (active.RunningStatus == ServiceWorkerRunningStatus.Running) return WorkerLocked(active.VersionId) ?? throw StartFailed();
            _starts.Add((active.VersionId, done));
        }
        try { await _devTools.SendAsync("ServiceWorker.startWorker", w => w.WriteString("scopeURL", spec)).ConfigureAwait(false); }
        catch (Exception)
        {
            lock (_gate) _starts.RemoveAll(s => s.Done == done);
            throw StartFailed();
        }
        return await done.Task.ConfigureAwait(false);
    }

    private static InvalidOperationException StartFailed() => new("Failed to start service worker.");

    private ServiceWorkerInfo InfoLocked(long versionId)
    {
        var version = _state.Version(versionId);
        return new ServiceWorkerInfo(version?.ScriptUrl ?? "", version is null ? "" : _state.Scope(version.RegistrationId) ?? "", 0);
    }

    // ServiceWorkerMain::From: the existing wrapper, else one for a live version that is not redundant.
    private ServiceWorkerMain? WorkerLocked(long versionId)
    {
        if (_workers.TryGetValue(versionId, out var worker)) return worker;
        if (_state.Version(versionId) is not { Status: not "redundant" } version) return null;
        worker = new ServiceWorkerMain(this, versionId, version.ScriptUrl, _state.Scope(version.RegistrationId) ?? "");
        _workers[versionId] = worker;
        return worker;
    }

    // UI thread: every DevTools event of the session's hidden page and of the workers it attached to.
    internal void OnDevToolsEvent(string method, JsonElement args, string? sessionId)
    {
        switch (method)
        {
            case "ServiceWorker.workerRegistrationUpdated":
            case "ServiceWorker.workerVersionUpdated":
                OnServiceWorkerEvent(method, args);
                break;
            case "Runtime.consoleAPICalled":
            case "Log.entryAdded":
                if (sessionId is not null) OnConsoleEvent(method, args, sessionId);
                break;
            case "Target.detachedFromTarget":
                if (args.TryGetProperty("sessionId", out var detached)) OnDetached(detached.GetString()!);
                break;
        }
    }

    private void OnServiceWorkerEvent(string method, JsonElement args)
    {
        var raise = new List<Action>();
        var actions = new List<Action>();
        lock (_gate)
        {
            var changes = method == "ServiceWorker.workerRegistrationUpdated" ? _state.ApplyRegistrations(args) : _state.ApplyVersions(args);
            foreach (var change in changes)
            {
                switch (change)
                {
                    case ServiceWorkerChange.RunningStatusChanged(var id, var status):
                        if (_workers.TryGetValue(id, out var worker)) worker.OnRunningStatusChangedLocked(_state.Version(id), _state);
                        if (status == ServiceWorkerRunningStatus.Stopped) DropAttachmentLocked(id, actions);
                        else actions.AddRange(UpdateAttachmentLocked(id));
                        SettleStartsLocked(id, status);
                        var details = new ServiceWorkerRunningStatusChangedEventArgs(id, status);
                        raise.Add(() => RunningStatusChanged?.Invoke(this, details));
                        break;
                    case ServiceWorkerChange.VersionRedundant(var id):
                        if (_workers.TryGetValue(id, out var redundant)) redundant.OnVersionRedundantLocked(_state.Version(id));
                        // Chromium keeps a redundant worker running while DevTools is attached; Electron's stops.
                        DropAttachmentLocked(id, actions);
                        SettleStartsLocked(id, ServiceWorkerRunningStatus.Stopped);
                        break;
                    case ServiceWorkerChange.TargetChanged(var id):
                        actions.AddRange(UpdateAttachmentLocked(id));
                        break;
                    case ServiceWorkerChange.RegistrationCompleted(var scope):
                        var completed = new ServiceWorkerRegistrationCompletedEventArgs(scope);
                        raise.Add(() => RegistrationCompleted?.Invoke(this, completed));
                        break;
                }
            }
            if (_state.IsListed) _listed.TrySetResult();
        }
        foreach (var action in actions) action();
        foreach (var r in raise)
        {
            try { r(); }
            catch (Exception e) { Console.Error.WriteLine($"[Riftway] A service worker event handler failed: {e}"); }
        }
    }

    internal void Forget(ServiceWorkerMain worker)
    {
        if (_workers.TryGetValue(worker.VersionId, out var current) && current == worker) _workers.Remove(worker.VersionId);
    }

    private void SettleStartsLocked(long versionId, ServiceWorkerRunningStatus status)
    {
        for (var i = _starts.Count - 1; i >= 0; i--)
        {
            var (id, done) = _starts[i];
            if (id != versionId) continue;
            if (status == ServiceWorkerRunningStatus.Running)
            {
                if (WorkerLocked(id) is { } worker) done.TrySetResult(worker);
                else done.TrySetException(StartFailed());
            }
            else if (status == ServiceWorkerRunningStatus.Stopped) done.TrySetException(StartFailed());
            else continue;
            _starts.RemoveAt(i);
        }
    }

    private void OnConsoleEvent(string method, JsonElement args, string sessionId)
    {
        ServiceWorkerConsoleMessageEventArgs? message;
        EventHandler<ServiceWorkerConsoleMessageEventArgs>? handler;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var versionId) || (handler = _consoleMessage) is null) return;
            if (!_consoleFilter.Accept(versionId, ServiceWorkerProtocol.Timestamp(method, args))) return;
            message = method == "Log.entryAdded" ? ServiceWorkerProtocol.FromLogEntry(args, versionId) : ServiceWorkerProtocol.FromConsoleApi(args, versionId);
        }
        if (message is null) return;
        try { handler(this, message); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] A service worker console-message handler failed: {e}"); }
    }

    // Attachments: a DevTools session on a worker carries its console, and keeps it from stopping when idle, which
    // is what StartTask needs.
    private sealed class Attachment
    {
        public string? SessionId;
        public bool ConsoleEnabled;
    }

    private List<Action> UpdateAllAttachments()
    {
        var actions = new List<Action>();
        foreach (var version in _state.Versions.ToList()) actions.AddRange(UpdateAttachmentLocked(version.VersionId));
        return actions;
    }

    private bool WantsConsole(ServiceWorkerVersionRecord version)
        => _consoleMessage is not null && version.RunningStatus is ServiceWorkerRunningStatus.Starting or ServiceWorkerRunningStatus.Running;

    private List<Action> UpdateAttachmentLocked(long versionId)
    {
        var actions = new List<Action>();
        var version = _state.Version(versionId);
        var tasks = _workers.TryGetValue(versionId, out var worker) ? worker.PendingTasks : 0;
        var wanted = version is { Status: not "redundant", TargetId: not null } && (tasks > 0 || WantsConsole(version));
        _attachments.TryGetValue(versionId, out var attachment);
        if (!wanted)
        {
            if (attachment?.SessionId is { } sessionId)
            {
                _attachments.Remove(versionId);
                _sessions.Remove(sessionId);
                actions.Add(() => Ignore(_devTools.SendAsync("Target.detachFromTarget", w => w.WriteString("sessionId", sessionId))));
            }
            return actions;
        }
        if (attachment is null)
        {
            attachment = new Attachment();
            _attachments[versionId] = attachment;
            var targetId = version!.TargetId!;
            actions.Add(() => _ = AttachAsync(versionId, targetId, attachment));
            return actions;
        }
        if (attachment.SessionId is { } session && !attachment.ConsoleEnabled && WantsConsole(version!))
        {
            attachment.ConsoleEnabled = true;
            actions.Add(() =>
            {
                Ignore(_devTools.SendAsync("Runtime.enable", sessionId: session));
                Ignore(_devTools.SendAsync("Log.enable", sessionId: session));
            });
        }
        return actions;
    }

    private async Task AttachAsync(long versionId, string targetId, Attachment attachment)
    {
        string sessionId;
        try
        {
            var result = await _devTools.SendAsync("Target.attachToTarget", w =>
            {
                w.WriteString("targetId", targetId);
                w.WriteBoolean("flatten", true);
            }).ConfigureAwait(false);
            sessionId = result.GetProperty("sessionId").GetString()!;
        }
        catch (Exception)
        {
            // The worker stopped first: its target is gone, and a new run brings a new one.
            lock (_gate) if (_attachments.TryGetValue(versionId, out var current) && current == attachment) _attachments.Remove(versionId);
            return;
        }
        List<Action> actions;
        lock (_gate)
        {
            if (!_attachments.TryGetValue(versionId, out var current) || current != attachment)
            {
                actions = [() => Ignore(_devTools.SendAsync("Target.detachFromTarget", w => w.WriteString("sessionId", sessionId)))];
            }
            else
            {
                attachment.SessionId = sessionId;
                _sessions[sessionId] = versionId;
                actions = UpdateAttachmentLocked(versionId);
            }
        }
        foreach (var action in actions) action();
    }

    private void DropAttachmentLocked(long versionId, List<Action> actions)
    {
        if (_workers.TryGetValue(versionId, out var worker)) worker.ClearTasksLocked();
        _consoleFilter.Forget(versionId);
        if (!_attachments.Remove(versionId, out var attachment) || attachment.SessionId is not { } sessionId) return;
        _sessions.Remove(sessionId);
        actions.Add(() => Ignore(_devTools.SendAsync("Target.detachFromTarget", w => w.WriteString("sessionId", sessionId))));
    }

    private void OnDetached(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out var versionId)) return;
            if (_attachments.TryGetValue(versionId, out var attachment) && attachment.SessionId == sessionId) _attachments.Remove(versionId);
        }
    }

    private static void Ignore(Task task) => task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    internal ServiceWorkerTask StartTask(ServiceWorkerMain worker)
    {
        ServiceWorkerTask task;
        List<Action> actions;
        lock (_gate)
        {
            if (worker.IsDestroyed()) throw new InvalidOperationException("ServiceWorkerMain is destroyed");
            if (!ServiceWorkerTaskRules.CanStart(_state.Version(worker.VersionId)?.RunningStatus))
                throw new InvalidOperationException("Unable to start service worker task.");
            task = new ServiceWorkerTask(this, worker);
            worker.AddTaskLocked(task);
            actions = UpdateAttachmentLocked(worker.VersionId);
        }
        foreach (var action in actions) action();
        return task;
    }

    internal void EndTask(ServiceWorkerMain worker, ServiceWorkerTask task)
    {
        string? error;
        bool destroyed;
        List<Action> actions;
        lock (_gate)
        {
            var pending = worker.RemoveTaskLocked(task);
            actions = UpdateAttachmentLocked(worker.VersionId);
            error = ServiceWorkerTaskRules.EndError(_state.Version(worker.VersionId)?.RunningStatus, pending);
            destroyed = worker.IsDestroyed();
        }
        foreach (var action in actions) action();
        // As Electron: the task is released first, so a doomed worker can stop.
        if (destroyed) throw new InvalidOperationException("ServiceWorkerMain is destroyed");
        if (error is not null) throw new InvalidOperationException(error);
    }

    internal Lock Gate => _gate;
}

/// <summary>A version of a service worker script for a scope. Electron's <c>ServiceWorkerMain</c>.</summary>
public sealed class ServiceWorkerMain
{
    private readonly ServiceWorkers _owner;
    private readonly HashSet<ServiceWorkerTask> _tasks = [];
    private string _scriptUrl;
    private string _scope;
    private volatile bool _destroyed;

    internal ServiceWorkerMain(ServiceWorkers owner, long versionId, string scriptUrl, string scope)
    {
        _owner = owner;
        VersionId = versionId;
        _scriptUrl = scriptUrl;
        _scope = scope;
    }

    /// <summary>The version's ID. Electron's <c>serviceWorker.versionId</c>.</summary>
    public long VersionId { get; }

    /// <summary>The registration's scope; "" once destroyed. Electron's <c>serviceWorker.scope</c>.</summary>
    public string Scope
    {
        get { lock (_owner.Gate) return _destroyed ? "" : _scope; }
    }

    /// <summary>The script's URL; "" once destroyed. Electron's <c>serviceWorker.scriptURL</c>.</summary>
    public string ScriptUrl
    {
        get { lock (_owner.Gate) return _destroyed ? "" : _scriptUrl; }
    }

    /// <summary>Whether the version is gone. Electron's <c>serviceWorker.isDestroyed()</c>.</summary>
    public bool IsDestroyed() => _destroyed;

    /// <summary>Electron's <c>serviceWorker.send()</c>: not supported, as no script of Riftway's runs in a service
    /// worker to receive it (drift SW5).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> is null.</exception>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void Send(string channel, params object?[] args)
    {
        if (channel is null) throw new ArgumentNullException(nameof(channel), "Missing required channel argument");
        throw new NotSupportedException("CEF runs no preload script in a service worker, so nothing there can receive a message (drift SW5).");
    }

    /// <summary>Keeps the worker from stopping while idle until the task ends. Electron's <c>serviceWorker.startTask()</c>.</summary>
    /// <remarks>Riftway holds a DevTools session on the worker for the task (drift SW3).</remarks>
    /// <exception cref="InvalidOperationException">The worker is destroyed, or is not starting or running.</exception>
    public ServiceWorkerTask StartTask() => _owner.StartTask(this);

    internal int PendingTasks => _tasks.Count;

    internal void AddTaskLocked(ServiceWorkerTask task) => _tasks.Add(task);

    internal bool RemoveTaskLocked(ServiceWorkerTask task) => _tasks.Remove(task);

    // A stopped worker drops its external requests (ServiceWorkerVersion::CleanUpExternalRequest).
    internal void ClearTasksLocked() => _tasks.Clear();

    // ServiceWorkerMain::OnRunningStatusChanged: re-read the version, destroy once it is gone. Electron's other case,
    // redundant and stopped, is the same here: the state forgets such a version.
    internal void OnRunningStatusChangedLocked(ServiceWorkerVersionRecord? version, ServiceWorkerState state)
    {
        if (_destroyed) return;
        if (version is null)
        {
            Destroy();
            return;
        }
        _scriptUrl = version.ScriptUrl;
        _scope = state.Scope(version.RegistrationId) ?? _scope;
    }

    // ServiceWorkerMain::OnVersionRedundant: content reports no stop for a version that never ran, so destroy now.
    internal void OnVersionRedundantLocked(ServiceWorkerVersionRecord? version)
    {
        if (version is not { RunningStatus: ServiceWorkerRunningStatus.Running }) Destroy();
    }

    private void Destroy()
    {
        if (_destroyed) return;
        _destroyed = true;
        _tasks.Clear();
        _owner.Forget(this);
    }
}

/// <summary>A task keeping a service worker alive. Electron's <c>startTask()</c> result.</summary>
public sealed class ServiceWorkerTask
{
    private readonly ServiceWorkers _owner;
    private readonly ServiceWorkerMain _worker;

    internal ServiceWorkerTask(ServiceWorkers owner, ServiceWorkerMain worker)
    {
        _owner = owner;
        _worker = worker;
    }

    /// <summary>Ends the task. Electron's <c>end()</c>.</summary>
    /// <exception cref="InvalidOperationException">The worker is destroyed, gone or stopped, or the task already ended
    /// while the worker was starting; the messages are Electron's.</exception>
    public void End() => _owner.EndTask(_worker, this);
}
