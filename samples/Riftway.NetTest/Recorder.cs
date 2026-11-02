using System.Text;
using Riftway;

/// <summary>Records a request's and its response's events in order (consecutive data chunks as one "data"),
/// and the body.</summary>
sealed class Recorder
{
    private readonly Lock _gate = new();
    private readonly List<string> _events = [];
    private readonly MemoryStream _body = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastEvent = Environment.TickCount64;

    public Recorder(ClientRequest request, Action<IncomingMessage>? onResponse = null)
    {
        Request = request;
        request.Finish += (_, _) => Add("finish");
        request.Close += (_, _) => { Add("close"); _closed.TrySetResult(); };
        request.Aborted += (_, _) => Add("abort");
        request.Error += (_, e) => { Add("error:" + e.Message); Errors.Add(e); };
        request.Redirect += (_, r) => { Add("redirect"); Redirects.Add(r); };
        request.Response += (_, response) =>
        {
            Add("response");
            Response = response;
            response.Data += (_, chunk) =>
            {
                lock (_gate)
                {
                    if (_events.Count == 0 || _events[^1] != "data") _events.Add("data");
                    _body.Write(chunk);
                    DataEvents++;
                    _lastEvent = Environment.TickCount64;
                }
            };
            response.End += (_, _) => Add("end");
            response.Error += (_, e) => Add("rerror:" + e.Message);
            response.Close += (_, _) => Add("rclose");
            onResponse?.Invoke(response);
        };
    }

    public ClientRequest Request { get; }
    public IncomingMessage? Response { get; private set; }
    public List<Exception> Errors { get; } = [];
    public List<RedirectEventArgs> Redirects { get; } = [];
    public List<AuthInfo> Logins { get; } = [];
    /// <summary>Handles login; set it to subscribe, since having a handler at all changes the outcome.</summary>
    public Action<LoginEventArgs>? OnLogin
    {
        get => null;
        set => Request.Login += (_, l) => { Add("login"); Logins.Add(l.AuthInfo); value?.Invoke(l); };
    }
    public int DataEvents { get; private set; }

    public string Events
    {
        get { lock (_gate) return string.Join(",", _events); }
    }

    public string BodyText
    {
        get { lock (_gate) return Encoding.UTF8.GetString(_body.ToArray()); }
    }

    public byte[] Body
    {
        get { lock (_gate) return _body.ToArray(); }
    }

    /// <summary>Waits for the request's close and then for the events to settle (300 ms quiet), at most 10 s.</summary>
    public async Task<Recorder> Settle()
    {
        await Task.WhenAny(_closed.Task, Task.Delay(10000));
        var deadline = Environment.TickCount64 + 10000;
        while (Environment.TickCount64 < deadline)
        {
            long last;
            lock (_gate) last = _lastEvent;
            if (Environment.TickCount64 - last > 300) break;
            await Task.Delay(50);
        }
        return this;
    }

    private void Add(string name)
    {
        lock (_gate)
        {
            _events.Add(name);
            _lastEvent = Environment.TickCount64;
        }
    }
}
