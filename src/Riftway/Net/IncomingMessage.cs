using Riftway.Cef;

namespace Riftway;

/// <summary>The response to a <see cref="ClientRequest"/>: status, headers and a readable body. Electron's
/// <c>IncomingMessage</c>.</summary>
/// <remarks>Events are raised on the UI thread. As with a Node.js stream, the body is held until a
/// <see cref="Data"/> handler is added (or <see cref="Resume"/> is called), and <see cref="End"/> follows only
/// once it has been read.</remarks>
public sealed class IncomingMessage
{
    private readonly Lock _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private EventHandler<byte[]>? _data;
    private bool _flowing;
    private bool _complete;
    private bool _endRaised;
    private bool _destroyed;

    internal IncomingMessage(int statusCode, string statusMessage, IReadOnlyList<KeyValuePair<string, string>> rawHeaders)
    {
        StatusCode = statusCode;
        StatusMessage = statusMessage;
        RawHeaderList = rawHeaders;
        Headers = NetRules.FlattenHeaders(rawHeaders);
        RawHeaders = [.. rawHeaders.SelectMany(h => new[] { h.Key, h.Value })];
    }

    /// <summary>The HTTP status code. Electron's <c>response.statusCode</c>.</summary>
    public int StatusCode { get; }

    /// <summary>The HTTP status text. Electron's <c>response.statusMessage</c>.</summary>
    public string StatusMessage { get; }

    /// <summary>The headers: lower-case names; one value each, joined with <c>", "</c> except for Node's
    /// single-valued headers (first kept) and <c>set-cookie</c> (every value). Electron's <c>response.headers</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <summary>Names and values alternating, names as sent. Electron's <c>response.rawHeaders</c>.</summary>
    public IReadOnlyList<string> RawHeaders { get; }

    internal IReadOnlyList<KeyValuePair<string, string>> RawHeaderList { get; }

    /// <summary>A chunk of the body. Adding a handler starts the flow. Electron's <c>data</c>.</summary>
    public event EventHandler<byte[]>? Data
    {
        add
        {
            lock (_gate)
            {
                _data += value;
                _flowing = true;
            }
            CefThread.PostUi(Flush);
        }
        remove
        {
            lock (_gate) _data -= value;
        }
    }

    /// <summary>The whole body has been read. Electron's <c>end</c>.</summary>
    public event EventHandler? End;

    /// <summary>Reading the body failed (e.g. the connection dropped). Electron's <c>error</c>.</summary>
    public event EventHandler<Exception>? Error;

    /// <summary>The response is done: after <see cref="End"/>, an <see cref="Error"/>, or the request being aborted.
    /// Node's <c>close</c>.</summary>
    public event EventHandler? Close;

    /// <summary>Starts the flow without a <see cref="Data"/> handler, discarding the body. Node's <c>resume()</c>.</summary>
    public void Resume()
    {
        lock (_gate) _flowing = true;
        CefThread.PostUi(Flush);
    }

    // UI thread, from the request.
    internal void Push(byte[] chunk)
    {
        lock (_gate)
        {
            if (_destroyed || _complete) return;
            _pending.Enqueue(chunk);
        }
        Flush();
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_destroyed) return;
            _complete = true;
        }
        Flush();
    }

    // Node's destroy(err): error (if any) and close on the next tick; the rest of the body is dropped.
    internal void Destroy(Exception? error)
    {
        lock (_gate)
        {
            if (_destroyed) return;
            _destroyed = true;
            _pending.Clear();
        }
        CefThread.PostUi(() =>
        {
            if (error is not null) Error?.Invoke(this, error);
            Close?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Flush()
    {
        while (true)
        {
            byte[]? chunk = null;
            EventHandler<byte[]>? handler;
            var end = false;
            lock (_gate)
            {
                if (!_flowing || _destroyed || _endRaised) return;
                if (_pending.Count > 0) chunk = _pending.Dequeue();
                else if (_complete) end = _endRaised = true;
                else return;
                handler = _data;
            }
            if (chunk is not null)
            {
                handler?.Invoke(this, chunk);
                continue;
            }
            if (end)
            {
                End?.Invoke(this, EventArgs.Empty);
                // A Readable destroys itself after end (autoDestroy), which emits close on the next tick.
                lock (_gate) _destroyed = true;
                CefThread.PostUi(() => Close?.Invoke(this, EventArgs.Empty));
                return;
            }
        }
    }
}
