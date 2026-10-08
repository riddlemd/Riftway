using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;

namespace Oriel;

/// <summary>The Chrome DevTools Protocol for one page. Electron's <c>webContents.debugger</c>.</summary>
/// <remarks>Oriel drives the page over the same protocol session, so <see cref="Message"/> also carries events of the
/// domains Oriel enables itself (<c>Page</c>, <c>Runtime</c>), and disabling those domains breaks Oriel's IPC,
/// frames and script execution for the page.</remarks>
public sealed class Debugger
{
    // DevTools' own list (content::DevToolsAgentHost::IsSupportedProtocolVersion).
    private static readonly string[] s_versions = ["1.0", "1.1", "1.2", "1.3"];

    private readonly DevToolsChannel _channel;
    private readonly Func<bool> _hasTarget;
    private readonly HashSet<TaskCompletionSource<JsonNode?>> _pending = [];
    private readonly List<Action> _queued = [];
    private volatile bool _attached;

    internal Debugger(DevToolsChannel channel, Func<bool> hasTarget)
    {
        _channel = channel;
        _hasTarget = hasTarget;
        _channel.AnyEventReceived += OnEvent;
    }

    /// <summary>A protocol event, while attached. Raised on the UI thread. Electron's <c>message</c> event.</summary>
    public event EventHandler<DebuggerMessageEventArgs>? Message;

    /// <summary>The page went away while attached. Raised on the UI thread. Electron's <c>detach</c> event.</summary>
    public event EventHandler<DebuggerDetachedEventArgs>? Detached;

    /// <summary>Starts a debugging session. Electron's <c>debugger.attach()</c>.</summary>
    /// <param name="protocolVersion">The protocol version required, e.g. <c>1.3</c>; null takes any.</param>
    /// <exception cref="InvalidOperationException">Already attached.</exception>
    /// <exception cref="ArgumentException">The version is not supported.</exception>
    public void Attach(string? protocolVersion = null)
    {
        if (protocolVersion is not null && !s_versions.Contains(protocolVersion))
            throw new ArgumentException("Requested protocol version is not supported.", nameof(protocolVersion));
        if (_attached) throw new InvalidOperationException("Another debugger is already attached to this target.");
        _attached = true;
    }

    /// <summary>Whether a debugging session is attached. Electron's <c>debugger.isAttached()</c>.</summary>
    public bool IsAttached() => _attached;

    /// <summary>Ends the debugging session, if any; commands still waiting fail. Electron's <c>debugger.detach()</c>.</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        CefThread.RunOnUi(() => FailPending("Debugger detached."));
    }

    /// <summary>Sends a protocol command and returns its result. Electron's <c>debugger.sendCommand()</c>.</summary>
    /// <param name="method">The method, e.g. <c>Network.enable</c>.</param>
    /// <param name="commandParams">The method's parameters.</param>
    /// <param name="sessionId">A child session from a flattened <c>Target.attachToTarget</c>; null for the page.</param>
    /// <remarks>As Electron's promise, the task fails (it does not throw) when not attached or when the protocol returns an
    /// error: <see cref="InvalidOperationException"/> or <see cref="DebuggerCommandException"/>.</remarks>
    public Task<JsonNode?> SendCommandAsync(string method, JsonObject? commandParams = null, string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        if (!_attached) return Task.FromException<JsonNode?>(new InvalidOperationException("Debugger is not attached to a target."));

        var done = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Serialised now: the caller may change the object after this returns.
        var parameters = commandParams?.ToJsonString();
        CefThread.RunOnUi(() =>
        {
            _pending.Add(done);
            void Send() => _channel.Send(method, parameters is null ? null : w => WriteMembers(w, parameters), (result, error) =>
            {
                if (!_pending.Remove(done)) return;
                if (error is not null) done.TrySetException(new DebuggerCommandException(method, error));
                else done.TrySetResult(result.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(result.GetRawText()));
            }, sessionId);
            if (_hasTarget()) Send();
            else _queued.Add(Send);
        });
        return done.Task;
    }

    // UI thread: the page's browser exists; commands sent before it go now.
    internal void OnTargetCreated()
    {
        var queued = _queued.ToArray();
        _queued.Clear();
        foreach (var send in queued) send();
    }

    // UI thread: the page's browser is gone.
    internal void OnTargetDestroyed()
    {
        _queued.Clear();
        FailPending("Target closed.");
        if (!_attached) return;
        _attached = false;
        Detached?.Invoke(this, new DebuggerDetachedEventArgs("target closed"));
    }

    private void FailPending(string reason)
    {
        foreach (var done in _pending) done.TrySetException(new InvalidOperationException(reason));
        _pending.Clear();
    }

    private void OnEvent(string method, JsonElement parameters, string? sessionId)
    {
        if (!_attached || Message is not { } handler) return;
        var node = parameters.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(parameters.GetRawText());
        handler(this, new DebuggerMessageEventArgs(method, node, sessionId));
    }

    private static void WriteMembers(Utf8JsonWriter writer, string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject()) property.WriteTo(writer);
    }
}

/// <summary>A protocol event. Electron's <c>message</c> event arguments.</summary>
/// <param name="Method">The event, e.g. <c>Network.requestWillBeSent</c>.</param>
/// <param name="Params">The event's parameters.</param>
/// <param name="SessionId">The child session it came from; null for the page's own.</param>
public sealed record DebuggerMessageEventArgs(string Method, JsonNode? Params, string? SessionId);

/// <summary>Why a debugging session ended. Electron's <c>detach</c> event arguments.</summary>
public sealed record DebuggerDetachedEventArgs(string Reason);

/// <summary>A protocol command that returned an error.</summary>
public sealed class DebuggerCommandException(string method, string message) : Exception($"{method}: {message}")
{
    /// <summary>The command that failed.</summary>
    public string Method { get; } = method;

    /// <summary>The protocol's error message.</summary>
    public string ProtocolMessage { get; } = message;
}
