using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Which requests a <see cref="WebRequest"/> listener sees; Electron's <c>WebRequestFilter</c>.</summary>
public sealed record WebRequestFilter
{
    /// <summary>URL match patterns such as <c>*://*.example.com/*</c> or <c>&lt;all_urls&gt;</c>; empty for every URL.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>URL match patterns to leave out.</summary>
    public IReadOnlyList<string> ExcludeUrls { get; init; } = [];

    /// <summary>Resource types such as <c>mainFrame</c>, <c>script</c> or <c>xhr</c>; empty for every type.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    // Electron's ResourceTypes table: "other" is a type a request can have but not one a filter may name.
    private static readonly HashSet<string> s_types = new(StringComparer.Ordinal)
        { "cspReport", "font", "image", "mainFrame", "media", "object", "ping", "script", "stylesheet", "subFrame", "webSocket", "xhr" };

    /// <summary>The filter as a test; throws <see cref="ArgumentException"/> for an invalid pattern or type, as Electron does.</summary>
    internal Func<string, string, bool> Compile()
    {
        var urls = Urls.Select(UrlPattern.Parse).ToArray();
        var excluded = ExcludeUrls.Select(UrlPattern.Parse).ToArray();
        if (Types.FirstOrDefault(t => !s_types.Contains(t)) is { } invalid) throw new ArgumentException($"Invalid type {invalid}", nameof(Types));
        var types = Types.ToHashSet(StringComparer.Ordinal);
        return (url, type) => (types.Count == 0 || types.Contains(type))
            && (urls.Length == 0 || urls.Any(p => p.Matches(url)))
            && !excluded.Any(p => p.Matches(url));
    }
}

/// <summary>A request as a <see cref="WebRequest"/> listener sees it; Electron's listener <c>details</c>. Fields an event
/// does not carry are null.</summary>
public sealed record WebRequestDetails
{
    /// <summary>The request's id, the same in each of its events.</summary>
    public required ulong Id { get; init; }

    /// <summary>The URL.</summary>
    public required string Url { get; init; }

    /// <summary>The HTTP method.</summary>
    public required string Method { get; init; }

    /// <summary>Electron's resource type: <c>mainFrame</c>, <c>subFrame</c>, <c>stylesheet</c>, <c>script</c>, <c>image</c>,
    /// <c>font</c>, <c>object</c>, <c>xhr</c>, <c>ping</c>, <c>cspReport</c>, <c>media</c> or <c>other</c>.</summary>
    public required string ResourceType { get; init; }

    /// <summary>The referrer; empty for none.</summary>
    public required string Referrer { get; init; }

    /// <summary>When the event happened, in milliseconds since the Unix epoch.</summary>
    public required double Timestamp { get; init; }

    /// <summary>The origin that issued the request, such as <c>https://example.com</c>, or <c>null</c> for an opaque one;
    /// kept from the original request across redirects, and absent for requests the browser started itself.</summary>
    public string? InitiatorOrigin { get; init; }

    /// <summary>The page that made the request; null for a request without one, such as a service worker's.</summary>
    public WebContents? WebContents { get; init; }

    /// <summary>The <see cref="Oriel.WebContents.Id"/> of the page that made the request; null without one.</summary>
    public int? WebContentsId { get; init; }

    /// <summary>The frame that made the request; null without one, or when the frame is no longer known.</summary>
    public WebFrameMain? Frame { get; init; }

    /// <summary>The request's body; null for a request without one. Blob and stream bodies are left out: CEF does not
    /// list them.</summary>
    public IReadOnlyList<UploadData>? UploadData { get; init; }

    /// <summary>The request headers (<c>onBeforeSendHeaders</c>, <c>onSendHeaders</c>).</summary>
    public IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }

    /// <summary>The response's status code.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The response's status line, e.g. <c>HTTP/1.1 200 OK</c>.</summary>
    public string? StatusLine { get; init; }

    /// <summary>The response headers, each name with its values.</summary>
    public IReadOnlyDictionary<string, string[]>? ResponseHeaders { get; init; }

    /// <summary>Where a redirect goes (<c>onBeforeRedirect</c>).</summary>
    public string? RedirectUrl { get; init; }

    /// <summary>The network error, e.g. <c>net::ERR_NAME_NOT_RESOLVED</c> (<c>onErrorOccurred</c>).</summary>
    public string? Error { get; init; }

    /// <summary>Whether the response came from the cache. CEF does not say: always false.</summary>
    public bool FromCache { get; init; }
}

/// <summary>One element of a request's body; Electron's <c>UploadData</c>.</summary>
public sealed record UploadData
{
    /// <summary><c>rawData</c> for bytes, <c>file</c> for a file.</summary>
    public required string Type { get; init; }

    /// <summary>The bytes sent (<c>rawData</c>).</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>The path of the file sent (<c>file</c>).</summary>
    public string? File { get; init; }
}

/// <summary>An <c>OnBeforeRequest</c> listener's answer; Electron's callback response.</summary>
public sealed record WebRequestResponse
{
    /// <summary>Stops the request.</summary>
    public bool Cancel { get; init; }

    /// <summary>Sends the request to this URL instead.</summary>
    public string? RedirectUrl { get; init; }
}

/// <summary>An <c>OnBeforeSendHeaders</c> listener's answer; Electron's <c>BeforeSendResponse</c>.</summary>
public sealed record BeforeSendHeadersResponse
{
    /// <summary>Stops the request.</summary>
    public bool Cancel { get; init; }

    /// <summary>Replaces the request's headers; null keeps them.</summary>
    public IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }
}

/// <summary>Watches and changes a session's requests; Electron's <c>ses.webRequest</c>. Each event has one listener,
/// replaced by the next one set; null removes it. Listeners run on the UI thread.</summary>
public sealed class WebRequest
{
    private volatile Listener<Func<WebRequestDetails, Task<WebRequestResponse>>>? _beforeRequest;
    private volatile Listener<Func<WebRequestDetails, Task<BeforeSendHeadersResponse>>>? _beforeSendHeaders;
    private volatile Listener<Action<WebRequestDetails>>? _sendHeaders;
    private volatile Listener<Action<WebRequestDetails>>? _headersReceived;
    private volatile Listener<Action<WebRequestDetails>>? _responseStarted;
    private volatile Listener<Action<WebRequestDetails>>? _beforeRedirect;
    private volatile Listener<Action<WebRequestDetails>>? _completed;
    private volatile Listener<Action<WebRequestDetails>>? _errorOccurred;

    // Requests a listener cancelled, so their error is Electron's rather than CEF's ERR_ABORTED.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _blocked = new();
    // Requests a listener redirected: Chromium calls that redirect "Internal Redirect", CEF "Temporary Redirect".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _redirected = new();

    internal WebRequest() { }

    /// <summary>A response carried <c>Set-Cookie</c>; IO thread.</summary>
    internal Action? SetCookieSeen { get; set; }

    private void NoteSetCookie(ResponseInfo response)
    {
        if (SetCookieSeen is { } seen && response.Headers.Any(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))) seen();
    }

    private sealed record Listener<T>(Func<string, string, bool> Matches, T Handle);

    private static Listener<T>? Make<T>(WebRequestFilter? filter, T? listener) where T : class
        => listener is null ? null : new Listener<T>((filter ?? new WebRequestFilter()).Compile(), listener);

    /// <summary>A request is about to start; the answer can cancel or redirect it. Electron's <c>onBeforeRequest</c>.</summary>
    public void OnBeforeRequest(WebRequestFilter? filter, Func<WebRequestDetails, Task<WebRequestResponse>>? listener)
        => _beforeRequest = Make(filter, listener);

    /// <inheritdoc cref="OnBeforeRequest(WebRequestFilter?, Func{WebRequestDetails, Task{WebRequestResponse}}?)"/>
    public void OnBeforeRequest(Func<WebRequestDetails, Task<WebRequestResponse>>? listener) => OnBeforeRequest(null, listener);

    /// <summary>A request's headers are about to be sent; the answer can cancel it or replace them. Electron's
    /// <c>onBeforeSendHeaders</c>.</summary>
    public void OnBeforeSendHeaders(WebRequestFilter? filter, Func<WebRequestDetails, Task<BeforeSendHeadersResponse>>? listener)
        => _beforeSendHeaders = Make(filter, listener);

    /// <inheritdoc cref="OnBeforeSendHeaders(WebRequestFilter?, Func{WebRequestDetails, Task{BeforeSendHeadersResponse}}?)"/>
    public void OnBeforeSendHeaders(Func<WebRequestDetails, Task<BeforeSendHeadersResponse>>? listener) => OnBeforeSendHeaders(null, listener);

    /// <summary>A request is being sent with these headers. Electron's <c>onSendHeaders</c>.</summary>
    public void OnSendHeaders(WebRequestFilter? filter, Action<WebRequestDetails>? listener) => _sendHeaders = Make(filter, listener);

    /// <summary>A response's headers arrived. Electron's <c>onHeadersReceived</c>, but it cannot change or cancel the
    /// response: CEF gives no way to.</summary>
    public void OnHeadersReceived(WebRequestFilter? filter, Action<WebRequestDetails>? listener) => _headersReceived = Make(filter, listener);

    /// <summary>A response's first bytes arrived. Electron's <c>onResponseStarted</c>.</summary>
    public void OnResponseStarted(WebRequestFilter? filter, Action<WebRequestDetails>? listener) => _responseStarted = Make(filter, listener);

    /// <summary>The server redirected a request. Electron's <c>onBeforeRedirect</c>.</summary>
    public void OnBeforeRedirect(WebRequestFilter? filter, Action<WebRequestDetails>? listener) => _beforeRedirect = Make(filter, listener);

    /// <summary>A request finished. Electron's <c>onCompleted</c>.</summary>
    public void OnCompleted(WebRequestFilter? filter, Action<WebRequestDetails>? listener) => _completed = Make(filter, listener);

    /// <summary>A request failed or was cancelled. Electron's <c>onErrorOccurred</c>.</summary>
    public void OnErrorOccurred(WebRequestFilter? filter, Action<WebRequestDetails>? listener) => _errorOccurred = Make(filter, listener);

    internal bool HasListeners => _beforeRequest is not null || _beforeSendHeaders is not null || _sendHeaders is not null
        || _headersReceived is not null || _responseStarted is not null || _beforeRedirect is not null || _completed is not null
        || _errorOccurred is not null;

    /// <summary>IO thread: the decision for a request about to be sent; null when no listener wants it.</summary>
    internal Task<RequestDecision>? Decide(WebContents? page, RequestInfo request)
    {
        if (IsHidden(request.Url)) return null;
        var type = ResourceTypeName(request.ResourceType);
        var before = _beforeRequest is { } b && b.Matches(request.Url, type) ? b : null;
        var headers = _beforeSendHeaders is { } h && h.Matches(request.Url, type) ? h : null;
        if (before is null && headers is null) return null;
        return DecideAsync(page, request, type, before, headers);
    }

    private async Task<RequestDecision> DecideAsync(WebContents? page, RequestInfo request, string type,
        Listener<Func<WebRequestDetails, Task<WebRequestResponse>>>? before, Listener<Func<WebRequestDetails, Task<BeforeSendHeadersResponse>>>? headers)
    {
        if (before is not null)
        {
            var answer = await Ask(before.Handle, page, request, Details(page, request, type)).ConfigureAwait(false);
            if (answer?.Cancel == true) return Block(request);
            if (answer?.RedirectUrl is { Length: > 0 } redirect)
            {
                _redirected[request.Id] = 0;
                return new RequestDecision(RedirectUrl: redirect);
            }
        }
        if (headers is null) return new RequestDecision();
        var sent = await Ask(headers.Handle, page, request, Details(page, request, type) with { RequestHeaders = RequestHeadersOf(request) }).ConfigureAwait(false);
        if (sent?.Cancel == true) return Block(request);
        return new RequestDecision(Headers: sent?.RequestHeaders?.Select(kv => KeyValuePair.Create(kv.Key, kv.Value)).ToList());
    }

    private RequestDecision Block(RequestInfo request)
    {
        _blocked[request.Id] = 0;
        return new RequestDecision(Cancel: true);
    }

    // A listener that throws leaves the request as it was.
    private static async Task<T?> Ask<T>(Func<WebRequestDetails, Task<T>> listener, WebContents? page, RequestInfo request, WebRequestDetails details) where T : class
    {
        var asked = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            try { asked.SetResult(listener(WithFrame(details, page, request))); }
            catch (Exception e) { asked.SetException(e); }
        });
        try { return await (await asked.Task.ConfigureAwait(false)).ConfigureAwait(false); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Oriel] A webRequest listener failed; the request goes on unchanged: {e}");
            return null;
        }
    }

    internal void Sent(WebContents? page, RequestInfo request)
        => Notify(_sendHeaders, page, request, d => d with { RequestHeaders = RequestHeadersOf(request) });

    internal void Redirected(WebContents? page, RequestInfo request, ResponseInfo response, string url)
    {
        NoteSetCookie(response);
        var internalRedirect = _redirected.TryRemove(request.Id, out _);
        Notify(_beforeRedirect, page, request, d => WithResponse(d, internalRedirect ? response with { StatusText = "Internal Redirect" } : response) with { RedirectUrl = url });
    }

    internal void ResponseStarted(WebContents? page, RequestInfo request, ResponseInfo response)
    {
        NoteSetCookie(response);
        Notify(_headersReceived, page, request, d => WithResponse(d, response));
        Notify(_responseStarted, page, request, d => WithResponse(d, response));
    }

    internal void Completed(WebContents? page, RequestInfo request, ResponseInfo response, cef_urlrequest_status_t status)
    {
        var error = _blocked.TryRemove(request.Id, out _) ? "net::ERR_BLOCKED_BY_CLIENT" : ErrorName(response.Error);
        if (status == cef_urlrequest_status_t.UR_SUCCESS) Notify(_completed, page, request, d => WithResponse(d, response) with { Error = "net::OK" });
        else Notify(_errorOccurred, page, request, d => d with { Error = error });
    }

    private static void Notify(Listener<Action<WebRequestDetails>>? listener, WebContents? page, RequestInfo request, Func<WebRequestDetails, WebRequestDetails> shape)
    {
        if (listener is null || IsHidden(request.Url)) return;
        var type = ResourceTypeName(request.ResourceType);
        if (!listener.Matches(request.Url, type)) return;
        var details = shape(Details(page, request, type));
        CefThread.RunOnUi(() =>
        {
            try { listener.Handle(WithFrame(details, page, request)); }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] A webRequest listener failed: {e}"); }
        });
    }

    private static WebRequestDetails Details(WebContents? page, RequestInfo request, string type) => new()
    {
        Id = request.Id,
        Url = request.Url,
        Method = request.Method,
        ResourceType = type,
        Referrer = request.Referrer,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        WebContents = page,
        WebContentsId = page?.Id,
        UploadData = UploadDataOf(request.Upload),
        InitiatorOrigin = OriginOf(request.Initiator),
    };

    /// <summary>The serialized origin Electron reports: scheme, host and any port, or "null" for an opaque one.</summary>
    internal static string? OriginOf(string? initiator)
    {
        if (initiator is null) return null;
        // An opaque "null" is already serialized; "file://" comes out unchanged below.
        if (!Uri.TryCreate(initiator, UriKind.Absolute, out var uri)) return initiator;
        return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    // UI thread, where the page's frames are known.
    private static WebRequestDetails WithFrame(WebRequestDetails details, WebContents? page, RequestInfo request)
        => page is not null && request.FramePath is { } path ? details with { Frame = page.FrameOfPath(path) } : details;

    /// <summary>Electron's <c>uploadData</c> from CEF's body elements.</summary>
    internal static IReadOnlyList<UploadData>? UploadDataOf(IReadOnlyList<UploadElement>? upload) => upload?.Select(e => e.File is { } file
        ? new UploadData { Type = "file", File = file }
        : new UploadData { Type = "rawData", Bytes = e.Bytes ?? [] }).ToList();

    /// <summary>Requests Oriel or Chromium make for themselves, which webRequest leaves out: Oriel's sendSync channel
    /// (Electron's sendSync is not a request at all), DevTools and Chrome's own pages.</summary>
    internal static bool IsHidden(string url)
    {
        var colon = url.IndexOf(':');
        return colon > 0 && url[..colon].ToLowerInvariant() is SyncIpc.Scheme or "devtools" or "chrome" or "chrome-untrusted";
    }

    /// <summary>Hooks for a session's requests without a page; <paramref name="find"/> gives the session's
    /// <see cref="WebRequest"/>, null while there is none.</summary>
    internal static RequestContextHandler PagelessHandler(Func<WebRequest?> find, Action<nint>? initialized = null) => new()
    {
        Initialized = initialized,
        Wants = () => find()?.HasListeners == true,
        Resource = new ResourceRequestHandler
        {
            Decide = request => find()?.Decide(null, request),
            Sent = request => find()?.Sent(null, request),
            Redirected = (request, response, url) => find()?.Redirected(null, request, response, url),
            ResponseStarted = (request, response) => find()?.ResponseStarted(null, request, response),
            Completed = (request, response, status) => find()?.Completed(null, request, response, status),
        },
    };

    private static WebRequestDetails WithResponse(WebRequestDetails details, ResponseInfo response) => details with
    {
        StatusCode = response.Status,
        StatusLine = $"HTTP/1.1 {response.Status} {response.StatusText}".TrimEnd(),
        ResponseHeaders = ResponseHeadersOf(response.Headers),
    };

    internal static IReadOnlyDictionary<string, string> RequestHeadersOf(RequestInfo request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Headers) headers[name] = value;
        return headers;
    }

    internal static IReadOnlyDictionary<string, string[]> ResponseHeadersOf(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (!grouped.TryGetValue(name, out var values)) grouped[name] = values = [];
            values.Add(value);
        }
        return grouped.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Chromium's <c>net::ErrorToString</c>: <c>net::</c> and the error's name.</summary>
    internal static string ErrorName(cef_errorcode_t error) => "net::" + (Enum.IsDefined(error) ? error.ToString() : $"ERR_{(int)error}");

    /// <summary>Electron's resource type names, as Chromium's web request API maps them.</summary>
    internal static string ResourceTypeName(cef_resource_type_t type) => type switch
    {
        cef_resource_type_t.RT_MAIN_FRAME or cef_resource_type_t.RT_NAVIGATION_PRELOAD_MAIN_FRAME => "mainFrame",
        cef_resource_type_t.RT_SUB_FRAME or cef_resource_type_t.RT_NAVIGATION_PRELOAD_SUB_FRAME => "subFrame",
        cef_resource_type_t.RT_STYLESHEET => "stylesheet",
        cef_resource_type_t.RT_SCRIPT or cef_resource_type_t.RT_WORKER or cef_resource_type_t.RT_SHARED_WORKER
            or cef_resource_type_t.RT_SERVICE_WORKER => "script",
        cef_resource_type_t.RT_IMAGE or cef_resource_type_t.RT_FAVICON => "image",
        cef_resource_type_t.RT_FONT_RESOURCE => "font",
        cef_resource_type_t.RT_OBJECT or cef_resource_type_t.RT_PLUGIN_RESOURCE => "object",
        cef_resource_type_t.RT_MEDIA => "media",
        cef_resource_type_t.RT_XHR => "xhr",
        cef_resource_type_t.RT_PING => "ping",
        cef_resource_type_t.RT_CSP_REPORT => "cspReport",
        _ => "other",
    };
}
