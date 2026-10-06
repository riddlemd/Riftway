using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oriel;

/// <summary>A message that arrived on a <see cref="MessagePortMain"/>; Electron's <c>MessageEvent</c>.</summary>
/// <param name="Data">The message, as JSON (drift I1).</param>
/// <param name="Ports">Ports sent with it.</param>
public sealed record MessagePortEvent(JsonNode? Data, IReadOnlyList<MessagePortMain> Ports);

/// <summary>Two entangled ports; Electron's <c>MessageChannelMain</c>.</summary>
public sealed class MessageChannelMain
{
    /// <summary>Creates the pair.</summary>
    public MessageChannelMain()
    {
        Port1 = new MessagePortMain();
        Port2 = new MessagePortMain();
        PortLink.Entangle(Port1, Port2);
    }

    /// <summary>One end.</summary>
    public MessagePortMain Port1 { get; }

    /// <summary>The other end.</summary>
    public MessagePortMain Port2 { get; }
}

/// <summary>One end of a channel in the main process; Electron's <c>MessagePortMain</c>. Messages wait until
/// <see cref="Start"/>; events are raised on the UI thread (in a utility process, Oriel's port thread).</summary>
public sealed class MessagePortMain
{
    private readonly Queue<MessagePortEvent> _queue = [];
    private bool _started;
    // Set when the port is sent away, at the call, so a second send throws there and then.
    private int _moved;

    internal MessagePortMain() { }

    // UI thread only.
    internal PortLink? Link { get; set; }

    /// <summary>A message arrived. Electron's <c>message</c> event.</summary>
    public event EventHandler<MessagePortEvent>? Message;

    /// <summary>The other end closed. Electron's <c>close</c> event.</summary>
    public event EventHandler? Closed;

    /// <summary>Sends <paramref name="message"/> to the other end, with ports to move there. Electron's
    /// <c>port.postMessage()</c>.</summary>
    /// <exception cref="InvalidOperationException">A port to move was already moved, or is this port.</exception>
    public void PostMessage(JsonNode? message, IReadOnlyList<MessagePortMain>? transfer = null)
    {
        var data = message?.ToJsonString() ?? "null";
        var ports = PortLink.CheckTransfer(transfer, this);
        if (Volatile.Read(ref _moved) != 0) return;
        PortThread.Run(() => Link?.Send(this, data, ports));
    }

    /// <summary>Delivers the messages waiting and those that follow. Electron's <c>port.start()</c>.</summary>
    public void Start() => PortThread.Run(() =>
    {
        _started = true;
        while (_queue.TryDequeue(out var queued)) Raise(queued);
    });

    /// <summary>Disentangles the port; the other end gets <see cref="Closed"/>. Electron's <c>port.close()</c>.</summary>
    public void Close() => PortThread.Run(() => Link?.Close(this));

    // UI thread.
    internal void Receive(MessagePortEvent message)
    {
        if (_started) Raise(message);
        else _queue.Enqueue(message);
    }

    internal void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);

    internal bool TryMarkMoved() => Interlocked.Exchange(ref _moved, 1) == 0;

    private void Raise(MessagePortEvent message)
    {
        try { Message?.Invoke(this, message); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A MessagePortMain listener threw: {e}"); }
    }
}

/// <summary>An end of a link: a port here, or a port bridged into a page's preload under an id.</summary>
internal abstract record PortEnd;

internal sealed record LocalEnd(MessagePortMain Port) : PortEnd;

internal sealed record RemoteEnd(WebContents Page, FrameState Frame, string Id) : PortEnd
{
    // The preload context the port lives in, once known; it dies with that document.
    public int? Context { get; set; }
}

// A port moved over a wire to another process, which holds the far end under the same id.
internal sealed record WireEnd(PortWire Wire, string Id) : PortEnd;

/// <summary>Two entangled ends. Every member runs on the UI thread.</summary>
internal sealed class PortLink
{
    private static readonly Dictionary<string, PortLink> s_remote = new(StringComparer.Ordinal);
    private PortEnd _a;
    private PortEnd _b;
    private bool _closed;

    private PortLink(PortEnd a, PortEnd b)
    {
        _a = a;
        _b = b;
        Attach(a);
        Attach(b);
    }

    internal static void Entangle(MessagePortMain one, MessagePortMain two) => _ = new PortLink(new LocalEnd(one), new LocalEnd(two));

    // A port a page made: its preload holds it under id; this side gets a port of its own.
    internal static MessagePortMain FromPage(WebContents page, FrameState frame, int context, string id)
    {
        var port = new MessagePortMain();
        _ = new PortLink(new RemoteEnd(page, frame, id) { Context = context }, new LocalEnd(port));
        return port;
    }

    internal static IReadOnlyList<MessagePortMain> CheckTransfer(IReadOnlyList<MessagePortMain>? transfer, MessagePortMain? source)
    {
        if (transfer is null || transfer.Count == 0) return [];
        for (var i = 0; i < transfer.Count; i++)
        {
            if (transfer[i] is null || ReferenceEquals(transfer[i], source) || transfer.Take(i).Contains(transfer[i]) || !transfer[i].TryMarkMoved())
                throw new InvalidOperationException($"Port at index {i} is already neutered or is the source port");
        }
        return [.. transfer];
    }

    // Moves a port here into a page: its link now ends in the page's preload under a new id.
    internal static string MoveToPage(MessagePortMain port, WebContents page, FrameState frame)
    {
        var link = port.Link ?? throw new InvalidOperationException("Port is already neutered");
        var id = Guid.NewGuid().ToString("N");
        port.Link = null;
        link.Replace(port, new RemoteEnd(page, frame, id));
        return id;
    }

    // Moves a port here to another port here, as one sent through a channel arrives as a new object.
    private static MessagePortMain MoveLocal(MessagePortMain port)
    {
        var link = port.Link ?? throw new InvalidOperationException("Port is already neutered");
        var moved = new MessagePortMain();
        port.Link = null;
        link.Replace(port, new LocalEnd(moved));
        return moved;
    }

    // A port that arrived over a wire under id: this side gets a port of its own.
    internal static MessagePortMain FromWire(PortWire wire, string id)
    {
        var port = new MessagePortMain();
        _ = new PortLink(new WireEnd(wire, id), new LocalEnd(port));
        return port;
    }

    // Moves a port here over a wire: its link now ends in the other process under a new id.
    internal static string MoveToWire(MessagePortMain port, PortWire wire)
    {
        var link = port.Link ?? throw new InvalidOperationException("Port is already neutered");
        var id = Guid.NewGuid().ToString("N");
        port.Link = null;
        link.Replace(port, new WireEnd(wire, id));
        return id;
    }

    internal static PortLink? ForRemote(string id) => s_remote.GetValueOrDefault(id);

    // The page's document is gone, and with it every port its preload held.
    internal static void ContextGone(WebContents page, int context)
    {
        foreach (var link in s_remote.Values.Distinct().ToList())
            foreach (var end in new[] { link._a, link._b })
                if (end is RemoteEnd remote && ReferenceEquals(remote.Page, page) && remote.Context == context) link.Close(end);
    }

    // From the end held by sender, to the other end.
    internal void Send(object sender, string data, IReadOnlyList<MessagePortMain> transfer)
    {
        if (_closed) return;
        var target = IsEnd(_a, sender) ? _b : _a;
        switch (target)
        {
            case LocalEnd local:
                var ports = transfer.Select(MoveLocal).ToList();
                local.Port.Receive(new MessagePortEvent(JsonNode.Parse(data), ports));
                break;
            case RemoteEnd remote:
                var ids = transfer.Select(p => MoveToPage(p, remote.Page, remote.Frame)).ToList();
                remote.Page.DeliverPort(remote, new JsonObject { ["t"] = "port-msg", ["id"] = remote.Id, ["data"] = JsonNode.Parse(data), ["ports"] = new JsonArray([.. ids.Select(i => (JsonNode?)i)]) });
                break;
            case WireEnd wire:
                wire.Wire.SendPort(wire.Id, data, [.. transfer.Select(p => MoveToWire(p, wire.Wire))]);
                break;
        }
    }

    // A message from the far end of wire's id, carrying ports moved across with it.
    internal void FromWireEnd(PortWire wire, string id, string data, IReadOnlyList<string> portIds)
    {
        if (WireEndFor(wire, id) is not { } origin) return;
        var transfer = portIds.Select(p => FromWire(wire, p)).ToList();
        Send(origin, data, transfer);
    }

    // The far end of wire's id closed, or its process is gone.
    internal void CloseWireEnd(PortWire wire, string id)
    {
        if (WireEndFor(wire, id) is { } end) Close(end);
    }

    private WireEnd? WireEndFor(PortWire wire, string id)
        => new[] { _a, _b }.OfType<WireEnd>().FirstOrDefault(w => ReferenceEquals(w.Wire, wire) && w.Id == id);

    // A message from the page's end with id, carrying ports the page made.
    internal void FromPageEnd(string id, JsonElement data, IReadOnlyList<(string Id, int Context)> portIds, RemoteEnd origin)
    {
        var transfer = portIds.Select(p => FromPage(origin.Page, origin.Frame, p.Context, p.Id)).ToList();
        Send(id, data.GetRawText(), transfer);
    }

    internal void Close(object closer)
    {
        if (_closed) return;
        _closed = true;
        foreach (var end in new[] { _a, _b })
        {
            Detach(end);
            if (IsEnd(end, closer)) continue;
            if (end is LocalEnd local) local.Port.RaiseClosed();
            else if (end is RemoteEnd remote) remote.Page.DeliverPort(remote, new JsonObject { ["t"] = "port-close", ["id"] = remote.Id });
            else if (end is WireEnd wire) wire.Wire.SendPortClose(wire.Id);
        }
    }

    private void Replace(MessagePortMain old, PortEnd replacement)
    {
        if (IsEnd(_a, old)) _a = replacement;
        else _b = replacement;
        Attach(replacement);
    }

    private void Attach(PortEnd end)
    {
        if (end is LocalEnd local) local.Port.Link = this;
        else if (end is RemoteEnd remote) s_remote[remote.Id] = this;
        else if (end is WireEnd wire) wire.Wire.Attach(wire.Id, this);
    }

    private static void Detach(PortEnd end)
    {
        if (end is LocalEnd local) local.Port.Link = null;
        else if (end is RemoteEnd remote) s_remote.Remove(remote.Id);
        else if (end is WireEnd wire) wire.Wire.Detach(wire.Id);
    }

    private static bool IsEnd(PortEnd end, object holder) => end switch
    {
        LocalEnd local => ReferenceEquals(local.Port, holder),
        RemoteEnd remote => holder is string id && id == remote.Id || ReferenceEquals(remote, holder),
        WireEnd wire => ReferenceEquals(wire, holder),
        _ => false,
    };

    internal RemoteEnd? RemoteEnd(string id) => new[] { _a, _b }.OfType<RemoteEnd>().FirstOrDefault(r => r.Id == id);
}
