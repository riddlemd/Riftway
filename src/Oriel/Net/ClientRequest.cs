using System.Text;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>An HTTP request over Chromium's network stack, with the session's cookies, cache, proxy and
/// protocol handlers. Electron's <c>ClientRequest</c>; create one with <see cref="Net.Request(ClientRequestOptions)"/>.</summary>
/// <remarks>
/// <para>Events are raised on the UI thread, in Electron's order. As in Electron (a Node.js Writable with
/// <c>autoDestroy</c>), <see cref="Close"/> follows <see cref="Finish"/> straight after <see cref="End(byte[])"/>, before
/// <see cref="Response"/>; an <see cref="Error"/> after that is still raised.</para>
/// <para>The body is sent when <see cref="End(byte[])"/> is called, also with <see cref="ChunkedEncoding"/>: CEF has no
/// streaming upload, so the body goes with a Content-Length.</para>
/// </remarks>
public sealed unsafe class ClientRequest
{
    // Chromium's URLRequest redirect limit.
    private const int MaxRedirects = 20;

    private readonly Lock _gate = new();
    private readonly Session _session;
    private readonly RedirectMode _redirectMode;
    private readonly RequestCredentials? _credentials;
    private readonly RequestCache? _cache;
    private readonly ReferrerPolicy? _referrerPolicy;
    private readonly string _origin;
    // Insertion-ordered by lower-case name, as Electron keeps them in a JS object.
    private readonly OrderedDictionary<string, KeyValuePair<string, string>> _headers = new(StringComparer.Ordinal);
    private readonly MemoryStream _body = new();

    private string _url;
    private string _method;
    private bool? _chunkedEncoding;
    private bool _started;
    private bool _firstWrite;
    private bool _wroteBody;
    private bool _ended;
    private bool _aborted;
    private bool _destroyed;
    // Set once the request has failed or been aborted: nothing more goes on the wire.
    private bool _cancelled;
    private bool _waitingForRedirect;
    private bool _followRedirect;
    private int _redirects;
    private int _hop;
    private nint _urlRequest;
    private UrlRequestClient? _client;
    private IncomingMessage? _response;
    private UploadProgress? _uploadProgress;

    internal ClientRequest(ClientRequestOptions options)
    {
        var url = NetRules.BuildUrl(options);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) throw new ArgumentException("Invalid URL");
        if (!options.AllowNonHttpProtocols && parsed.Scheme is not ("http" or "https"))
            throw new ArgumentException("ClientRequest only supports http: and https: protocols");
        if (options.Credentials == RequestCredentials.SameOrigin && string.IsNullOrEmpty(options.Origin))
            throw new ArgumentException("credentials: same-origin requires origin to be set");
        if (options.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
            {
                NetRules.ValidateHeader(name, value);
                _headers[name.ToLowerInvariant()] = new(name, value);
            }
        }
        _url = parsed.AbsoluteUri;
        _method = NetRules.NormalizeMethod(options.Method);
        _redirectMode = options.Redirect;
        _credentials = options.Credentials;
        _cache = options.Cache;
        _referrerPolicy = options.ReferrerPolicy;
        _origin = options.Origin ?? "";
        _session = options.Session ?? Session.FromPartition(options.Partition ?? "");
    }

    /// <summary>The response's status and headers arrived. Electron's <c>response</c>.</summary>
    public event EventHandler<IncomingMessage>? Response;

    /// <summary>A server or proxy asks for credentials; answer with <see cref="LoginEventArgs.Callback"/>. Without a
    /// handler the challenge is cancelled and the 401 is the response. Electron's <c>login</c>.</summary>
    public event EventHandler<LoginEventArgs>? Login;

    /// <summary>The server redirected; in <see cref="RedirectMode.Manual"/> mode call <see cref="FollowRedirect"/> from
    /// the handler to follow, or the request fails. Electron's <c>redirect</c>.</summary>
    public event EventHandler<RedirectEventArgs>? Redirect;

    /// <summary>The body has been handed over, after <see cref="End(byte[])"/>. Electron's <c>finish</c>.</summary>
    public event EventHandler? Finish;

    /// <summary>The request was aborted. Electron's <c>abort</c>.</summary>
    public event EventHandler? Aborted;

    /// <summary>The request failed; the message is Chromium's error (e.g. <c>net::ERR_CONNECTION_REFUSED</c>) or one
    /// of Electron's. Electron's <c>error</c>.</summary>
    public event EventHandler<Exception>? Error;

    /// <summary>The request object is done. Electron's <c>close</c>.</summary>
    public event EventHandler? Close;

    /// <summary>Whether the body is sent chunked; settable once, before the first write. Electron's
    /// <c>request.chunkedEncoding</c>.</summary>
    /// <exception cref="InvalidOperationException">The request has started, or the value was already set.</exception>
    public bool ChunkedEncoding
    {
        get
        {
            lock (_gate) return _chunkedEncoding ?? false;
        }
        set
        {
            lock (_gate)
            {
                if (_started) throw new InvalidOperationException("chunkedEncoding can only be set before the request is started");
                if (_chunkedEncoding is not null) throw new InvalidOperationException("chunkedEncoding can only be set once");
                _chunkedEncoding = value;
            }
        }
    }

    /// <summary>Adds a header, sent with the name's case as given. Electron's <c>request.setHeader()</c>.</summary>
    /// <exception cref="InvalidOperationException">The body has been written or the request started.</exception>
    /// <exception cref="ArgumentException">The name or value is not valid HTTP.</exception>
    public void SetHeader(string name, string value)
    {
        lock (_gate)
        {
            if (_started || _firstWrite) throw new InvalidOperationException("Can't set headers after they are sent");
            NetRules.ValidateHeader(name, value);
            _headers[name.ToLowerInvariant()] = new(name, value);
        }
    }

    /// <summary>A header set earlier, by any case of its name; null when unset. Electron's <c>request.getHeader()</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null.</exception>
    public string? GetHeader(string name)
    {
        if (name is null) throw new ArgumentException("`name` is required for getHeader(name)");
        lock (_gate) return _headers.TryGetValue(name.ToLowerInvariant(), out var header) ? header.Value : null;
    }

    /// <summary>Removes a header set earlier. Electron's <c>request.removeHeader()</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The body has been written or the request started.</exception>
    public void RemoveHeader(string name)
    {
        if (name is null) throw new ArgumentException("`name` is required for removeHeader(name)");
        lock (_gate)
        {
            if (_started || _firstWrite) throw new InvalidOperationException("Can't remove headers after they are sent");
            _headers.Remove(name.ToLowerInvariant());
        }
    }

    /// <summary>Adds to the body. Electron's <c>request.write()</c>.</summary>
    public void Write(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        lock (_gate)
        {
            if (_destroyed) return;
            if (_ended)
            {
                CefThread.PostUi(() => Error?.Invoke(this, new NetException("write after end")));
                return;
            }
            _firstWrite = true;
            _wroteBody = true;
            // A chunked body starts the request at its first write in Electron.
            if (_chunkedEncoding == true) _started = true;
            _body.Write(chunk);
        }
    }

    /// <summary>Adds text to the body. Electron's <c>request.write(chunk, encoding)</c>; UTF-8 when null.</summary>
    public void Write(string chunk, Encoding? encoding = null) => Write((encoding ?? Encoding.UTF8).GetBytes(chunk));

    /// <summary>Sends the request, with <paramref name="chunk"/> as the last of the body. Electron's <c>request.end()</c>.</summary>
    public ClientRequest End(byte[]? chunk = null)
    {
        if (chunk is not null) Write(chunk);
        lock (_gate)
        {
            if (_destroyed || _ended) return this;
            _ended = true;
        }
        CefThread.PostUi(() =>
        {
            lock (_gate)
            {
                if (_destroyed) return;
                _started = true;
            }
            StartHop(_url, _method, [.. _headers.Values], _wroteBody ? _body.ToArray() : null);
            Finish?.Invoke(this, EventArgs.Empty);
            lock (_gate)
            {
                if (_destroyed) return;
                _destroyed = true;
            }
            CefThread.PostUi(() => Close?.Invoke(this, EventArgs.Empty));
        });
        return this;
    }

    /// <summary>Sends the request with text as the last of the body.</summary>
    public ClientRequest End(string chunk, Encoding? encoding = null) => End((encoding ?? Encoding.UTF8).GetBytes(chunk));

    /// <summary>Cancels the request. Electron's <c>request.abort()</c>.</summary>
    public void Abort()
    {
        bool first;
        lock (_gate)
        {
            first = !_aborted;
            _aborted = true;
        }
        if (first) CefThread.PostUi(() => Aborted?.Invoke(this, EventArgs.Empty));
        CefThread.RunOnUi(() => Die(null));
    }

    /// <summary>Follows the redirect being reported; only valid inside a <see cref="Redirect"/> handler.
    /// Electron's <c>request.followRedirect()</c>.</summary>
    /// <exception cref="InvalidOperationException">No redirect is being reported.</exception>
    public void FollowRedirect()
    {
        lock (_gate)
        {
            if (!_waitingForRedirect) throw new InvalidOperationException("followRedirect() called, but was not waiting for a redirect");
            _followRedirect = true;
        }
    }

    /// <summary>The upload's progress. Electron's <c>request.getUploadProgress()</c>.</summary>
    public UploadProgress GetUploadProgress()
    {
        lock (_gate) return _uploadProgress ?? new UploadProgress(false, false, 0, 0);
    }

    // UI thread. Each hop of a redirect chain is its own cef_urlrequest_t.
    private void StartHop(string url, string method, List<KeyValuePair<string, string>> headers, byte[]? body)
    {
        var hop = ++_hop;
        var flags = NetRules.RequestFlags(_credentials, _cache);
        if (body is { Length: > 0 }) flags |= cef_urlrequest_flags_t.UR_FLAG_REPORT_UPLOAD_PROGRESS;
        lock (_gate)
        {
            if (_cancelled) return;
        }
        var request = CefNative.cef_request_create();
        cef_string_utf16_t text = default;
        CefString.Set(&text, url);
        request->set_url(request, &text);
        request->set_first_party_for_cookies(request, &text);
        CefString.Set(&text, method);
        request->set_method(request, &text);
        // CEF takes the referrer only through set_referrer, as Electron takes it only from the Referer header.
        var referrer = headers.FirstOrDefault(h => h.Key.Equals("referer", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        CefString.Set(&text, referrer);
        request->set_referrer(request, &text, NetRules.ReferrerPolicyOf(_referrerPolicy));
        CefString.Clear(&text);
        var map = CefStringMultimap.Create(headers.Where(h => !h.Key.Equals("referer", StringComparison.OrdinalIgnoreCase)));
        request->set_header_map(request, map);
        CefNative.cef_string_multimap_free(map);
        if (body is { Length: > 0 })
        {
            var post = CefNative.cef_post_data_create();
            var element = CefNative.cef_post_data_element_create();
            fixed (byte* bytes = body) element->set_to_bytes(element, (nuint)body.Length, bytes);
            post->add_element(post, element);
            request->set_post_data(request, post);
        }
        request->set_flags(request, (int)flags);

        var client = new UrlRequestClient();
        client.DownloadProgress = (r, _, _) => { if (hop == _hop) EmitResponse((cef_urlrequest_t*)r); };
        client.DownloadData = (r, chunk) =>
        {
            if (hop != _hop) return;
            EmitResponse((cef_urlrequest_t*)r);
            _response?.Push(chunk);
        };
        client.UploadProgress = (current, total) =>
        {
            lock (_gate) _uploadProgress = new UploadProgress(true, true, current, total);
        };
        client.AuthCredentials = (isProxy, host, port, realm, scheme, callback) => OnAuth(hop, isProxy, host, port, realm, scheme, callback);
        client.Completed = r => { if (hop == _hop) OnComplete((cef_urlrequest_t*)r, url, method, headers, body); };
        _client = client;

        // Both references go to CEF; the client lives as long as CEF keeps it.
        var created = CefNative.cef_urlrequest_create(request, client.Native, _session.AddRefNative());
        lock (_gate) _urlRequest = (nint)created;
    }

    private void EmitResponse(cef_urlrequest_t* request)
    {
        if (_response is not null) return;
        var response = request->get_response(request);
        if (response is null) return;
        try
        {
            var status = response->get_status(response);
            if (status == 0 || IsRedirect(response)) return;
            var map = CefNative.cef_string_multimap_alloc();
            response->get_header_map(response, map);
            var raw = CefStringMultimap.Read(map);
            CefNative.cef_string_multimap_free(map);
            _response = new IncomingMessage(status, CefString.ReadUserFree(response->get_status_text(response)), raw);
        }
        finally
        {
            NativeObject.Release(response);
        }
        Response?.Invoke(this, _response);
    }

    private static bool IsRedirect(cef_response_t* response)
    {
        var status = response->get_status(response);
        if (status is not (301 or 302 or 303 or 307 or 308)) return false;
        cef_string_utf16_t name = default;
        CefString.Set(&name, "Location");
        var location = CefString.ReadUserFree(response->get_header_by_name(response, &name));
        CefString.Clear(&name);
        return location.Length > 0;
    }

    private void OnComplete(cef_urlrequest_t* request, string url, string method, List<KeyValuePair<string, string>> headers, byte[]? body)
    {
        nint own;
        lock (_gate)
        {
            if (_cancelled) return;
            own = _urlRequest;
            _urlRequest = 0;
        }
        NativeObject.Release((cef_urlrequest_t*)own);
        var status = request->get_request_status(request);
        var response = request->get_response(request);
        try
        {
            if (response is not null && IsRedirect(response) && _response is null)
            {
                OnRedirect(response, url, method, headers, body);
                return;
            }
        }
        finally
        {
            NativeObject.Release(response);
        }
        if (status == cef_urlrequest_status_t.UR_SUCCESS)
        {
            EmitResponse(request);
            _response?.Complete();
            return;
        }
        var error = new NetException(NetRules.ErrorString((int)request->get_request_error(request)));
        _response?.Destroy(error);
        Die(error);
    }

    private void OnRedirect(cef_response_t* response, string url, string method, List<KeyValuePair<string, string>> headers, byte[]? body)
    {
        var status = response->get_status(response);
        var map = CefNative.cef_string_multimap_alloc();
        response->get_header_map(response, map);
        var raw = CefStringMultimap.Read(map);
        CefNative.cef_string_multimap_free(map);
        var location = raw.First(h => h.Key.Equals("location", StringComparison.OrdinalIgnoreCase)).Value;

        if (++_redirects > MaxRedirects)
        {
            Die(new NetException("net::ERR_TOO_MANY_REDIRECTS"));
            return;
        }
        if (NetRules.RedirectUrl(url, location) is not { } newUrl)
        {
            Die(new NetException("net::ERR_INVALID_REDIRECT"));
            return;
        }
        var newMethod = NetRules.RedirectMethod(method, status);
        // The network stack refuses a redirect out of http(s) before anyone hears of it.
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !newUrl.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            && !newUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            Die(new NetException("net::ERR_UNSAFE_REDIRECT"));
            return;
        }

        if (_redirectMode == RedirectMode.Error)
        {
            Die(new NetException("Attempted to redirect, but redirect policy was 'error'"));
            return;
        }
        var args = new RedirectEventArgs(status, newMethod, newUrl, NetRules.GroupHeaders(raw));
        lock (_gate)
        {
            _waitingForRedirect = true;
            _followRedirect = false;
        }
        try
        {
            Redirect?.Invoke(this, args);
        }
        finally
        {
            bool follow, aborted;
            lock (_gate)
            {
                _waitingForRedirect = false;
                follow = _followRedirect || _redirectMode == RedirectMode.Follow;
                aborted = _aborted;
            }
            if (!follow && !aborted) Die(new NetException("Redirect was cancelled"));
        }
        lock (_gate)
        {
            if (_cancelled) return;
        }
        if (!NetRules.IsSafeRedirectTarget(url, newUrl))
        {
            Die(new NetException("net::ERR_UNSAFE_REDIRECT"));
            return;
        }
        var (kept, clearBody) = NetRules.RedirectHeaders(headers, method, newMethod, url, newUrl);
        StartHop(newUrl, newMethod, kept, clearBody ? null : body);
    }

    private bool OnAuth(int hop, bool isProxy, string host, int port, string realm, string scheme, cef_auth_callback_t* callback)
    {
        // With credentials omitted CEF never asks, as Chromium raises no challenge then.
        if (hop != _hop || Login is not { } login) return false;
        var pending = (nint)callback;
        var answered = 0;
        var args = new LoginEventArgs(new AuthInfo(isProxy, scheme, host, port, realm), (user, password) =>
        {
            if (Interlocked.Exchange(ref answered, 1) != 0) return;
            var cb = (cef_auth_callback_t*)pending;
            if (user is not null && password is not null)
            {
                cef_string_utf16_t u = default, p = default;
                CefString.Set(&u, user);
                CefString.Set(&p, password);
                cb->cont(cb, &u, &p);
                CefString.Clear(&u);
                CefString.Clear(&p);
            }
            else
            {
                cb->cancel(cb);
            }
            NativeObject.Release(cb);
        });
        login(this, args);
        return true;
    }

    // Electron's _die: an error after the request object closed is still raised; then the network side stops.
    private void Die(Exception? error)
    {
        bool raiseNow, destroy;
        nint urlRequest;
        lock (_gate)
        {
            raiseNow = _destroyed && error is not null;
            destroy = !_destroyed;
            _destroyed = true;
            urlRequest = _urlRequest;
            _urlRequest = 0;
            _cancelled = true;
        }
        if (raiseNow) Error?.Invoke(this, error!);
        if (destroy)
        {
            CefThread.PostUi(() =>
            {
                if (error is not null) Error?.Invoke(this, error);
                Close?.Invoke(this, EventArgs.Empty);
            });
        }
        // Later callbacks of this hop are ignored.
        _hop = -1;
        if (urlRequest != 0)
        {
            var request = (cef_urlrequest_t*)urlRequest;
            request->cancel(request);
            NativeObject.Release(request);
        }
        _response?.Destroy(error);
    }
}
