using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Oriel;

/// <summary>What <see cref="Net.FetchAsync"/> sends: the parts of fetch's <c>RequestInit</c> Electron honours.</summary>
public sealed record FetchInit
{
    /// <summary>The method; GET when null.</summary>
    public string? Method { get; init; }

    /// <summary>Request headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The body.</summary>
    public byte[]? Body { get; init; }

    /// <summary>The redirect mode. In <see cref="RedirectMode.Manual"/> mode Electron's fetch fails on a redirect
    /// ("Redirect was cancelled"), as there is nobody to follow it.</summary>
    public RedirectMode Redirect { get; init; }

    /// <summary>The credentials mode; fetch's default (same-origin, which without an Origin header is include) when null.</summary>
    public RequestCredentials? Credentials { get; init; }

    /// <summary>The cache mode.</summary>
    public RequestCache? Cache { get; init; }

    /// <summary>The referrer policy.</summary>
    public ReferrerPolicy? ReferrerPolicy { get; init; }

    /// <summary>fetch's request mode (<c>cors</c>, <c>no-cors</c>, <c>same-origin</c>, <c>navigate</c>), sent as
    /// Sec-Fetch-Mode; cors (the default) only with an Origin header.</summary>
    public string? Mode { get; init; }

    /// <summary>Skip the session's protocol handlers. Electron's non-standard <c>bypassCustomProtocolHandlers</c>.</summary>
    public bool BypassCustomProtocolHandlers { get; init; }

    /// <summary>Aborts the request. fetch's <c>signal</c>.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>A response to <see cref="Net.FetchAsync"/>: fetch's <c>Response</c>.</summary>
public sealed class FetchResponse
{
    private readonly Channel<byte[]>? _chunks;
    private bool _used;

    internal FetchResponse(int status, string statusText, IReadOnlyDictionary<string, string> headers, Channel<byte[]>? chunks)
    {
        Status = status;
        StatusText = statusText;
        Headers = headers;
        _chunks = chunks;
    }

    /// <summary>The status code. Response's <c>status</c>.</summary>
    public int Status { get; }

    /// <summary>The status text. Response's <c>statusText</c>.</summary>
    public string StatusText { get; }

    /// <summary>Whether the status is 200–299. Response's <c>ok</c>.</summary>
    public bool Ok => Status is >= 200 and <= 299;

    /// <summary>The headers, lower-case names, repeated values joined with <c>", "</c>. Response's <c>headers</c>.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Whether the body has been read. Response's <c>bodyUsed</c>.</summary>
    public bool BodyUsed => _used;

    /// <summary>The body as a stream, null for a null-body status (101, 204, 205, 304) or a HEAD request; it can
    /// be read once. Response's <c>body</c>.</summary>
    /// <exception cref="InvalidOperationException">The body was already read.</exception>
    public Stream? Body => _chunks is null ? null : new ChunkStream(Take());

    /// <summary>The body. Response's <c>arrayBuffer()</c>.</summary>
    /// <exception cref="NetException">The connection failed mid-body.</exception>
    public async Task<byte[]> ArrayBufferAsync()
    {
        if (_chunks is null) return [];
        var reader = Take();
        using var buffer = new MemoryStream();
        await foreach (var chunk in reader.ReadAllAsync().ConfigureAwait(false)) buffer.Write(chunk);
        return buffer.ToArray();
    }

    /// <summary>The body as UTF-8 text. Response's <c>text()</c>.</summary>
    public async Task<string> TextAsync() => Encoding.UTF8.GetString(await ArrayBufferAsync().ConfigureAwait(false));

    /// <summary>The body parsed as JSON. Response's <c>json()</c>.</summary>
    /// <exception cref="JsonException">The body is not JSON.</exception>
    public async Task<JsonNode?> JsonAsync() => JsonNode.Parse(await ArrayBufferAsync().ConfigureAwait(false));

    private ChannelReader<byte[]> Take()
    {
        if (_used) throw new InvalidOperationException("Body is unusable: Body has already been read");
        _used = true;
        return _chunks!.Reader;
    }

    private sealed class ChunkStream(ChannelReader<byte[]> reader) : Stream
    {
        private ReadOnlyMemory<byte> _current;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current.IsEmpty)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (reader.TryRead(out var chunk)) _current = chunk;
            }
            var n = Math.Min(buffer.Length, _current.Length);
            _current[..n].CopyTo(buffer);
            _current = _current[n..];
            return n;
        }
    }
}

/// <summary>Electron's <c>fetchWithSession</c> (net-fetch.ts): a fetch on top of <see cref="ClientRequest"/>.</summary>
internal static class NetFetch
{
    private static readonly int[] s_nullBodyStatus = [101, 204, 205, 304];

    public static Task<FetchResponse> FetchAsync(Session session, string input, FetchInit? init)
    {
        ArgumentNullException.ThrowIfNull(input);
        init ??= new FetchInit();
        var done = new TaskCompletionSource<FetchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (init.CancellationToken.IsCancellationRequested)
        {
            done.SetCanceled(init.CancellationToken);
            return done.Task;
        }
        var headers = init.Headers ?? new Dictionary<string, string>();
        var origin = headers.FirstOrDefault(h => h.Key.Equals("origin", StringComparison.OrdinalIgnoreCase)).Value;
        // Without an origin, same-origin (fetch's default) can only mean include.
        var credentials = init.Credentials ?? RequestCredentials.SameOrigin;
        if (credentials == RequestCredentials.SameOrigin && origin is null) credentials = RequestCredentials.Include;
        var method = NetRules.NormalizeMethod(init.Method);

        ClientRequest request;
        try
        {
            request = new ClientRequest(new ClientRequestOptions
            {
                Session = session,
                Method = method,
                Url = input,
                Origin = origin,
                Credentials = credentials,
                Cache = init.Cache,
                ReferrerPolicy = init.ReferrerPolicy,
                Redirect = init.Redirect,
                BypassCustomProtocolHandlers = init.BypassCustomProtocolHandlers,
                AllowNonHttpProtocols = true,
            });
            var mode = init.Mode ?? "cors";
            if (mode != "cors" || origin is not null) request.SetHeader("Sec-Fetch-Mode", mode);
            foreach (var (name, value) in headers) request.SetHeader(name, value);
        }
        catch (Exception e)
        {
            done.SetException(e);
            return done.Task;
        }

        var locallyAborted = false;
        var registration = init.CancellationToken.Register(() =>
        {
            locallyAborted = true;
            done.TrySetCanceled(init.CancellationToken);
            request.Abort();
        });

        request.Response += (_, response) =>
        {
            if (locallyAborted) return;
            var flat = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            Channel<byte[]>? chunks = null;
            if (!s_nullBodyStatus.Contains(response.StatusCode) && method != "HEAD")
            {
                var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
                chunks = channel;
                response.Data += (_, chunk) => channel.Writer.TryWrite(chunk);
                response.End += (_, _) => channel.Writer.TryComplete();
                response.Error += (_, error) => channel.Writer.TryComplete(error);
                response.Close += (_, _) => channel.Writer.TryComplete(locallyAborted ? new OperationCanceledException() : null);
            }
            else
            {
                response.Resume();
            }
            done.TrySetResult(new FetchResponse(response.StatusCode, response.StatusMessage, flat, chunks));
        };
        request.Error += (_, error) =>
        {
            registration.Dispose();
            done.TrySetException(error);
        };
        if (init.Body is { } body) request.Write(body);
        request.End();
        return done.Task;
    }
}
