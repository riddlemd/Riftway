using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Oriel;

/// <summary>The channel between the main process and one utility process: framed JSON over a duplex stream (a named
/// pipe on Windows, a Unix domain socket elsewhere). It carries the parent channel's messages and every port moved
/// across, each under an id that names its link end on both sides. Handlers run on the <see cref="PortThread"/>.</summary>
internal sealed class PortWire
{
    private readonly Task<Stream> _connection;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _writeGate = new();
    private readonly TaskCompletionSource _readDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Port thread only.
    private readonly Dictionary<string, PortLink> _links = new(StringComparer.Ordinal);
    private bool _gone;

    /// <param name="connection">Completes with the connected stream, the handshake done; faults when no peer comes.</param>
    internal PortWire(Task<Stream> connection) => _connection = connection;

    /// <summary>A message on the parent channel, with the ports moved across with it.</summary>
    internal Action<JsonNode?, IReadOnlyList<MessagePortMain>>? Message { get; set; }

    /// <summary>The peer reported a fatal error: location, report.</summary>
    internal Action<string, string>? Fatal { get; set; }

    /// <summary>The peer is gone; every port moved across has closed.</summary>
    internal Action? Gone { get; set; }

    /// <summary>Completes once nothing more will be read; whatever was read is queued on the port thread before it.</summary>
    internal Task ReadCompletion => _readDone.Task;

    internal void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>Sends <paramref name="data"/> on the parent channel, moving <paramref name="transfer"/> across. Port thread.</summary>
    internal void SendMessage(string data, IReadOnlyList<MessagePortMain> transfer)
    {
        if (_gone)
        {
            foreach (var port in transfer) port.Link?.Close(port);
            return;
        }
        var ids = transfer.Select(p => PortLink.MoveToWire(p, this)).ToList();
        Enqueue(new WireFrame("msg", Data: data, Ports: ids));
    }

    internal void SendPort(string id, string data, IReadOnlyList<string> ports) => Enqueue(new WireFrame("port-msg", id, data, ports));

    internal void SendPortClose(string id) => Enqueue(new WireFrame("port-close", id));

    /// <summary>Writes what is queued, then <paramref name="frame"/> if any, before returning: for a process about to end.</summary>
    internal void SendNow(WireFrame? frame)
    {
        // A process failing just after it first used its parent port may still be connecting.
        try { if (!_connection.Wait(TimeSpan.FromSeconds(2))) return; }
        catch (AggregateException) { return; }
        var stream = _connection.Result;
        lock (_writeGate)
        {
            try
            {
                while (_outgoing.Reader.TryRead(out var queued)) WireFrame.Write(stream, queued);
                if (frame is not null) WireFrame.Write(stream, frame.Encode());
                stream.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    internal void Attach(string id, PortLink link) => _links[id] = link;

    internal void Detach(string id) => _links.Remove(id);

    // Port thread. Links whose far end is across this wire; a closed one must not stay registered.
    internal int PortCount => _links.Count;

    /// <summary>Ends the wire from this side; the peer sees end of stream.</summary>
    internal void Close()
    {
        _outgoing.Writer.TryComplete();
        if (_connection.IsCompletedSuccessfully) _connection.Result.Dispose();
    }

    private void Enqueue(WireFrame frame)
    {
        if (!_gone) _outgoing.Writer.TryWrite(frame.Encode());
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            var stream = await _connection.ConfigureAwait(false);
            while (await _outgoing.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                lock (_writeGate)
                {
                    while (_outgoing.Reader.TryRead(out var payload)) WireFrame.Write(stream, payload);
                    stream.Flush();
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) { }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var stream = await _connection.ConfigureAwait(false);
            while (await WireFrame.ReadAsync(stream).ConfigureAwait(false) is { } payload)
            {
                var frame = WireFrame.Decode(payload);
                PortThread.Post(() => Dispatch(frame));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) { }
        finally
        {
            _outgoing.Writer.TryComplete();
            PortThread.Post(OnGone);
            _readDone.TrySetResult();
        }
    }

    private void Dispatch(WireFrame frame)
    {
        if (_gone) return;
        switch (frame.Type)
        {
            case "msg":
                var ports = (frame.Ports ?? []).Select(id => PortLink.FromWire(this, id)).ToList();
                Message?.Invoke(JsonNode.Parse(frame.Data ?? "null"), ports);
                break;
            case "port-msg" when frame.Id is { } id && _links.TryGetValue(id, out var link):
                link.FromWireEnd(this, id, frame.Data ?? "null", frame.Ports ?? []);
                break;
            case "port-close" when frame.Id is { } id && _links.TryGetValue(id, out var link):
                link.CloseWireEnd(this, id);
                break;
            case "fatal":
                Fatal?.Invoke(frame.Location ?? string.Empty, frame.Report ?? string.Empty);
                break;
        }
    }

    // A dead peer closes every port it held, as a dead process does in Electron.
    private void OnGone()
    {
        if (_gone) return;
        _gone = true;
        foreach (var (id, link) in _links.ToList()) link.CloseWireEnd(this, id);
        _links.Clear();
        Gone?.Invoke();
    }
}
