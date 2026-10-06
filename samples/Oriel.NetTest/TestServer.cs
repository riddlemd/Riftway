using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>A request as the test server read it off the wire.</summary>
sealed record RecordedRequest(string Method, string Path, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body, bool Chunked)
{
    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>A raw HTTP/1.1 server on loopback: one request per connection, so tests see exactly what was sent.</summary>
sealed class TestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Dictionary<string, Func<RecordedRequest, NetworkStream, Task>> _routes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();

    public TestServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public int Port { get; }

    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public void Route(string path, Func<RecordedRequest, NetworkStream, Task> handler) => _routes[path] = handler;

    public RecordedRequest? Last(string path) => Requests.LastOrDefault(r => r.Path == path);

    public static Task Respond(NetworkStream stream, int status, string reason, IEnumerable<(string, string)>? headers, byte[]? body)
    {
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
        foreach (var (name, value) in headers ?? []) head.Append($"{name}: {value}\r\n");
        head.Append($"Content-Length: {body?.Length ?? 0}\r\nConnection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(head.ToString()).Concat(body ?? []).ToArray();
        return stream.WriteAsync(bytes).AsTask();
    }

    public static Task Text(NetworkStream stream, string text, int status = 200, string reason = "OK", params (string, string)[] headers)
        => Respond(stream, status, reason, headers.Append(("Content-Type", "text/plain")), Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequest(stream);
                if (request is null) return;
                Requests.Enqueue(request);
                if (_routes.TryGetValue(request.Path, out var route)) await route(request, stream);
                else await Respond(stream, 404, "Not Found", null, null);
                await stream.FlushAsync();
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<RecordedRequest?> ReadRequest(NetworkStream stream)
    {
        var headerBytes = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(headerBytes))
        {
            if (await stream.ReadAsync(one) == 0) return null;
            headerBytes.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString([.. headerBytes]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines[0].Split(' ');
        var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Select(p => new KeyValuePair<string, string>(p[0], p[1].Trim())).ToList();
        string? Header(string n) => headers.FirstOrDefault(h => h.Key.Equals(n, StringComparison.OrdinalIgnoreCase)).Value;
        var chunked = Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;
        var body = new MemoryStream();
        if (chunked)
        {
            while (true)
            {
                var sizeLine = await ReadLine(stream);
                var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                if (size == 0)
                {
                    await ReadLine(stream);
                    break;
                }
                await ReadExactly(stream, body, size);
                await ReadLine(stream);
            }
        }
        else if (int.TryParse(Header("Content-Length"), out var length) && length > 0)
        {
            await ReadExactly(stream, body, length);
        }
        var path = parts[1].Split('?')[0];
        return new RecordedRequest(parts[0], path, headers, body.ToArray(), chunked);
    }

    private static bool EndsWithBlankLine(List<byte> bytes)
        => bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n';

    private static async Task<string> ReadLine(NetworkStream stream)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            if (one[0] == '\n') break;
            if (one[0] != '\r') line.Add(one[0]);
        }
        return Encoding.ASCII.GetString([.. line]);
    }

    private static async Task ReadExactly(NetworkStream stream, MemoryStream into, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        into.Write(buffer);
    }
}
