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

    /// <summary>The message arguments, as sent by the page.</summary>
    public IReadOnlyList<JsonElement> Args { get; }
}

/// <summary>An <c>ipcRenderer.invoke</c> call: Electron's <c>IpcMainInvokeEvent</c>.</summary>
public sealed class IpcMainInvokeEvent : IpcMainEvent
{
    internal IpcMainInvokeEvent(WebContents sender, WebFrameMain? senderFrame, IReadOnlyList<JsonElement> args) : base(sender, senderFrame, args) { }
}

/// <summary>Messages from pages to the main process: Electron's <c>ipcMain</c>.</summary>
/// <remarks>Handlers and listeners run on CEF's UI thread; an async handler may finish on any thread.</remarks>
public static class IpcMain
{
    private static readonly Lock s_gate = new();
    private static readonly Dictionary<string, Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>>> s_handlers = [];
    private static readonly Dictionary<string, List<Action<IpcMainEvent>>> s_listeners = [];

    /// <summary>Answers <c>ipcRenderer.invoke(channel, ...)</c>; a thrown exception rejects the page's promise.</summary>
    /// <remarks>The token is cancelled when the sending page is destroyed, since nobody can receive the answer.</remarks>
    /// <exception cref="InvalidOperationException">The channel already has a handler.</exception>
    public static void Handle(string channel, Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>> handler)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(handler);
        lock (s_gate)
        {
            if (!s_handlers.TryAdd(channel, handler))
                throw new InvalidOperationException($"Attempted to register a second handler for '{channel}'.");
        }
    }

    /// <summary>Answers <c>ipcRenderer.invoke(channel, ...)</c> synchronously.</summary>
    /// <exception cref="InvalidOperationException">The channel already has a handler.</exception>
    public static void Handle(string channel, Func<IpcMainInvokeEvent, JsonNode?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Handle(channel, (e, _) => Task.FromResult(handler(e)));
    }

    /// <summary>Removes the handler for <paramref name="channel"/>, if any.</summary>
    public static void RemoveHandler(string channel)
    {
        lock (s_gate) s_handlers.Remove(channel);
    }

    /// <summary>Listens for <c>ipcRenderer.send(channel, ...)</c>.</summary>
    public static void On(string channel, Action<IpcMainEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(listener);
        lock (s_gate)
        {
            if (!s_listeners.TryGetValue(channel, out var list)) s_listeners[channel] = list = [];
            list.Add(listener);
        }
    }

    /// <summary>Stops <paramref name="listener"/> from receiving <paramref name="channel"/>.</summary>
    public static void RemoveListener(string channel, Action<IpcMainEvent> listener)
    {
        lock (s_gate)
        {
            if (s_listeners.TryGetValue(channel, out var list)) list.Remove(listener);
        }
    }

    internal static void DispatchSend(WebContents sender, WebFrameMain? senderFrame, string channel, IReadOnlyList<JsonElement> args)
    {
        Action<IpcMainEvent>[] listeners;
        lock (s_gate) listeners = s_listeners.TryGetValue(channel, out var list) ? [.. list] : [];

        var e = new IpcMainEvent(sender, senderFrame, args);
        foreach (var listener in listeners)
        {
            try { listener(e); }
            catch (Exception ex) { Console.Error.WriteLine($"[Oriel] ipcMain listener for '{channel}' threw: {ex}"); }
        }
    }

    internal static async Task<(bool Ok, JsonNode? Value, string? Error)> DispatchInvokeAsync(
        WebContents sender, WebFrameMain? senderFrame, string channel, IReadOnlyList<JsonElement> args)
    {
        Func<IpcMainInvokeEvent, CancellationToken, Task<JsonNode?>>? handler;
        lock (s_gate) s_handlers.TryGetValue(channel, out handler);

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
