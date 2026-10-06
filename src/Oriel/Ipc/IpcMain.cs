using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;

namespace Oriel;

/// <summary>A message from a page: Electron's <c>IpcMainEvent</c>.</summary>
public class IpcMainEvent
{
    internal IpcMainEvent(WebContents sender, WebFrameMain? senderFrame, IReadOnlyList<JsonElement> args)
    {
        Sender = sender;
        SenderFrame = senderFrame;
        Args = args;
    }

    /// <summary>The web contents that sent the message.</summary>
    public WebContents Sender { get; }

    /// <summary>The frame that sent the message, or null if it has gone. Electron's <c>event.senderFrame</c>.</summary>
    public WebFrameMain? SenderFrame { get; }

    /// <summary>Sends a message back to the frame that sent this one (its <c>ipcRenderer.on(channel)</c>).
    /// Electron's <c>event.reply()</c>.</summary>
    public void Reply(string channel, params JsonNode?[] args)
    {
        if (SenderFrame is { } frame) frame.Send(channel, args);
        else Sender.Send(channel, args);
    }

    /// <summary>The message arguments, as sent by the page.</summary>
    public IReadOnlyList<JsonElement> Args { get; }

    /// <summary>Ports the page sent with <c>ipcRenderer.postMessage</c>. Electron's <c>event.ports</c>.</summary>
    public IReadOnlyList<MessagePortMain> Ports { get; init; } = [];

    /// <summary>The value <c>ipcRenderer.sendSync</c> returns in the page; set it, now or later, to answer. Left unset, the
    /// page gets <c>{ error: 'reply was never sent' }</c> once this event is collected, as in Electron. Has no effect on
    /// other messages. Electron's <c>event.returnValue</c>.</summary>
    public JsonNode? ReturnValue
    {
        get => _returnValue;
        set
        {
            _returnValue = value;
            _syncReply?.Send(value);
        }
    }

    private JsonNode? _returnValue;
    private SyncReply? _syncReply;
    private SyncReplySentinel? _sentinel;

    internal void ExpectReply(SyncReply reply)
    {
        _syncReply = reply;
        _sentinel = new SyncReplySentinel(reply);
    }
}

/// <summary>An <c>ipcRenderer.invoke</c> call: Electron's <c>IpcMainInvokeEvent</c>.</summary>
public sealed class IpcMainInvokeEvent : IpcMainEvent
{
    internal IpcMainInvokeEvent(WebContents sender, WebFrameMain? senderFrame, IReadOnlyList<JsonElement> args) : base(sender, senderFrame, args) { }
}

/// <summary>An <c>ipc-message</c>: a page's <c>ipcRenderer.send</c>, raised before any IPC listener sees it.</summary>
/// <param name="Channel">The channel.</param>
/// <param name="Event">The message.</param>
public sealed record IpcMessageEventArgs(string Channel, IpcMainEvent Event);

/// <summary>Handlers and listeners for page messages. <see cref="IpcMain"/> holds the app-wide set;
/// <see cref="WebContents.Ipc"/> and <see cref="WebFrameMain.Ipc"/> hold sets for one sender, which Electron asks first.</summary>
/// <remarks>Handlers and listeners run on CEF's UI thread; an async handler may finish on any thread.</remarks>
public sealed class IpcMainScope
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>>> _handlers = [];
    private readonly Dictionary<string, List<(Action<IpcMainEvent> Listener, bool Once)>> _listeners = [];

    internal IpcMainScope() { }

    /// <summary>Answers <c>ipcRenderer.invoke(channel, ...)</c>; a thrown exception rejects the page's promise.</summary>
    /// <remarks>The token is cancelled when the sending page is destroyed, since nobody can receive the answer.</remarks>
    /// <exception cref="InvalidOperationException">The channel already has a handler.</exception>
    public void Handle(string channel, Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>> handler)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_handlers.TryAdd(channel, handler))
                throw new InvalidOperationException($"Attempted to register a second handler for '{channel}'.");
        }
    }

    /// <summary>Answers <c>ipcRenderer.invoke(channel, ...)</c> synchronously.</summary>
    /// <exception cref="InvalidOperationException">The channel already has a handler.</exception>
    public void Handle(string channel, Func<IpcMainInvokeEvent, JsonNode?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Handle(channel, (e, _) => Task.FromResult(handler(e)));
    }

    /// <summary>Answers the next <c>ipcRenderer.invoke(channel, ...)</c> only, then removes itself.
    /// Electron's <c>handleOnce()</c>.</summary>
    /// <exception cref="InvalidOperationException">The channel already has a handler.</exception>
    public void HandleOnce(string channel, Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>>? once = null;
        once = (e, token) =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(channel, out var current) && current == once) _handlers.Remove(channel);
            }
            return handler(e, token);
        };
        Handle(channel, once);
    }

    /// <inheritdoc cref="HandleOnce(string, Func{IpcMainInvokeEvent, CancellationToken, Task{JsonNode}})"/>
    public void HandleOnce(string channel, Func<IpcMainInvokeEvent, JsonNode?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        HandleOnce(channel, (e, _) => Task.FromResult(handler(e)));
    }

    /// <summary>Removes the handler for <paramref name="channel"/>, if any.</summary>
    public void RemoveHandler(string channel)
    {
        lock (_gate) _handlers.Remove(channel);
    }

    /// <summary>Listens for <c>ipcRenderer.send(channel, ...)</c>.</summary>
    public void On(string channel, Action<IpcMainEvent> listener) => AddListener(channel, listener, once: false);

    /// <summary>Listens for the next <c>ipcRenderer.send(channel, ...)</c> only. Electron's <c>once()</c>.</summary>
    public void Once(string channel, Action<IpcMainEvent> listener) => AddListener(channel, listener, once: true);

    /// <summary>Removes every listener for <paramref name="channel"/>, or for every channel when it is null.
    /// Electron's <c>removeAllListeners()</c>.</summary>
    public void RemoveAllListeners(string? channel = null)
    {
        lock (_gate)
        {
            if (channel is null) _listeners.Clear();
            else _listeners.Remove(channel);
        }
    }

    /// <summary>Stops <paramref name="listener"/> from receiving <paramref name="channel"/>.</summary>
    public void RemoveListener(string channel, Action<IpcMainEvent> listener)
    {
        lock (_gate)
        {
            if (_listeners.TryGetValue(channel, out var list) && list.FindIndex(l => l.Listener == listener) is >= 0 and var at) list.RemoveAt(at);
        }
    }

    private void AddListener(string channel, Action<IpcMainEvent> listener, bool once)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            if (!_listeners.TryGetValue(channel, out var list)) _listeners[channel] = list = [];
            list.Add((listener, once));
        }
    }

    internal void Emit(string channel, IpcMainEvent e)
    {
        Action<IpcMainEvent>[] listeners;
        lock (_gate)
        {
            listeners = _listeners.TryGetValue(channel, out var list) ? [.. list.Select(l => l.Listener)] : [];
            // Once-listeners leave before they run, so a message they trigger does not reach them again.
            list?.RemoveAll(l => l.Once);
        }
        foreach (var listener in listeners)
        {
            try { listener(e); }
            catch (Exception ex) { Console.Error.WriteLine($"[Oriel] ipcMain listener for '{channel}' threw: {ex}"); }
        }
    }

    internal bool HasListeners(string channel)
    {
        lock (_gate) return _listeners.TryGetValue(channel, out var list) && list.Count > 0;
    }

    internal Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>>? HandlerFor(string channel)
    {
        lock (_gate) return _handlers.GetValueOrDefault(channel);
    }
}

/// <summary>Messages from pages to the main process: Electron's <c>ipcMain</c>.</summary>
/// <remarks>Handlers and listeners run on CEF's UI thread; an async handler may finish on any thread.</remarks>
public static class IpcMain
{
    internal static IpcMainScope Global { get; } = new();

    /// <inheritdoc cref="IpcMainScope.Handle(string, Func{IpcMainInvokeEvent, CancellationToken, Task{JsonNode}})"/>
    public static void Handle(string channel, Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>> handler) => Global.Handle(channel, handler);

    /// <inheritdoc cref="IpcMainScope.Handle(string, Func{IpcMainInvokeEvent, JsonNode})"/>
    public static void Handle(string channel, Func<IpcMainInvokeEvent, JsonNode?> handler) => Global.Handle(channel, handler);

    /// <inheritdoc cref="IpcMainScope.HandleOnce(string, Func{IpcMainInvokeEvent, CancellationToken, Task{JsonNode}})"/>
    public static void HandleOnce(string channel, Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>> handler) => Global.HandleOnce(channel, handler);

    /// <inheritdoc cref="IpcMainScope.HandleOnce(string, Func{IpcMainInvokeEvent, JsonNode})"/>
    public static void HandleOnce(string channel, Func<IpcMainInvokeEvent, JsonNode?> handler) => Global.HandleOnce(channel, handler);

    /// <inheritdoc cref="IpcMainScope.RemoveHandler"/>
    public static void RemoveHandler(string channel) => Global.RemoveHandler(channel);

    /// <inheritdoc cref="IpcMainScope.On"/>
    public static void On(string channel, Action<IpcMainEvent> listener) => Global.On(channel, listener);

    /// <inheritdoc cref="IpcMainScope.Once"/>
    public static void Once(string channel, Action<IpcMainEvent> listener) => Global.Once(channel, listener);

    /// <inheritdoc cref="IpcMainScope.RemoveAllListeners"/>
    public static void RemoveAllListeners(string? channel = null) => Global.RemoveAllListeners(channel);

    /// <inheritdoc cref="IpcMainScope.RemoveListener"/>
    public static void RemoveListener(string channel, Action<IpcMainEvent> listener) => Global.RemoveListener(channel, listener);

    // Electron's ipc-dispatch order: the page's ipc-message (for ipcRenderer.send only, not postMessage), then the
    // frame's listeners, the page's, and ipcMain's.
    internal static void DispatchSend(WebContents sender, WebFrameMain? senderFrame, string channel, IReadOnlyList<JsonElement> args,
        IReadOnlyList<MessagePortMain>? ports = null)
    {
        var e = new IpcMainEvent(sender, senderFrame, args) { Ports = ports ?? [] };
        if (ports is null) sender.RaiseIpcMessage(channel, e);
        senderFrame?.Ipc.Emit(channel, e);
        sender.Ipc.Emit(channel, e);
        Global.Emit(channel, e);
    }

    // Electron's ipc-dispatch for a sync message: a warning when nothing listens, then ipc-message-sync, the frame's
    // listeners, the page's and ipcMain's; the reply waits for a listener to set returnValue.
    internal static Task<string> DispatchSync(WebContents sender, WebFrameMain? senderFrame, string channel, IReadOnlyList<JsonElement> args)
    {
        var reply = new SyncReply();
        Dispatch(new IpcMainEvent(sender, senderFrame, args));
        return reply.Sent;

        // Its own method, so the event is unreachable once the listeners are done with it.
        void Dispatch(IpcMainEvent e)
        {
            e.ExpectReply(reply);
            if (!sender.HasIpcMessageSyncListeners && senderFrame?.Ipc.HasListeners(channel) != true && !sender.Ipc.HasListeners(channel) && !Global.HasListeners(channel))
                Console.Error.WriteLine($"WebContents #{sender.Id} called ipcRenderer.sendSync() with '{channel}' channel without listeners.");
            sender.RaiseIpcMessageSync(channel, e);
            senderFrame?.Ipc.Emit(channel, e);
            sender.Ipc.Emit(channel, e);
            Global.Emit(channel, e);
        }
    }

    // The first of the frame's, the page's and ipcMain's handlers for the channel answers, as Electron's.
    internal static async Task<(bool Ok, JsonNode? Value, string? Error)> DispatchInvokeAsync(
        WebContents sender, WebFrameMain? senderFrame, string channel, IReadOnlyList<JsonElement> args)
    {
        var handler = senderFrame?.Ipc.HandlerFor(channel) ?? sender.Ipc.HandlerFor(channel) ?? Global.HandlerFor(channel);

        // Electron's wording, so page code written for Electron matches on it unchanged.
        if (handler is null) return (false, null, $"Error invoking remote method '{channel}': Error: No handler registered for '{channel}'");

        try
        {
            return (true, await handler(new IpcMainInvokeEvent(sender, senderFrame, args), sender.DestroyedToken).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Error invoking remote method '{channel}': {ex.Message}");
        }
    }
}
