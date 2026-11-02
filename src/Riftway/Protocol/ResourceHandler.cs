using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary><c>cef_resource_handler_t</c> for one request: runs the app's handler off-thread and streams
/// its response back.</summary>
/// <remarks>
/// CEF's order: <c>open</c> (pool thread) → <c>skip</c> (pool, only for a Range request, with the first
/// byte CEF already parsed) → <c>get_response_headers</c> (IO thread) → <c>read</c> until 0 (pool) →
/// <c>cancel</c> (IO thread, on every teardown, success included). The pool sequence may block, so
/// <c>skip</c> and <c>read</c> are synchronous.
/// </remarks>
internal sealed unsafe class ResourceHandler
{
    private const int ErrFailed = -2;

    // Each request's lifecycle on stderr, for diagnosing requests that never finish.
    private static readonly bool s_trace = Environment.GetEnvironmentVariable("RIFTWAY_TRACE_PROTOCOL") == "1";

    private readonly ProtocolRequest _request;
    private readonly Func<ProtocolRequest, CancellationToken, Task<ProtocolResponse>> _handler;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _gate = new();

    private ProtocolResponse? _response;
    private Stream? _body;
    private long? _totalLength;
    private ByteRange? _range;
    private long _remaining = -1;
    private bool _cancelled;

    public ResourceHandler(ProtocolRequest request, Func<ProtocolRequest, CancellationToken, Task<ProtocolResponse>> handler)
    {
        _request = request;
        _handler = handler;
        Native = NativeObject.Create<cef_resource_handler_t>(this);
        Native->open = &Open;
        Native->skip = &Skip;
        Native->get_response_headers = &GetResponseHeaders;
        Native->read = &Read;
        Native->cancel = &Cancel;
    }

    public cef_resource_handler_t* Native { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Open(cef_resource_handler_t* self, cef_request_t* request, int* handleRequest, cef_callback_t* callback)
    {
        NativeObject.Release(request);
        var owner = NativeObject.Owner<ResourceHandler>(self);
        owner.Trace("open");

        // Answered asynchronously: the callback (whose reference we now hold) continues the request.
        *handleRequest = 0;
        var pending = (nint)callback;
        ProtocolDispatch.Run(owner._handler, owner._request, owner._cancellation.Token)
            .ContinueWith(task => owner.Complete(task, pending), TaskScheduler.Default);
        return 1;
    }

    private void Complete(Task<ProtocolResponse> task, nint pendingCallback)
    {
        var callback = (cef_callback_t*)pendingCallback;
        var response = task.IsCompletedSuccessfully
            ? task.Result
            : ProtocolResponse.Text($"Handler for {_request.Url} failed: {task.Exception?.GetBaseException().Message ?? "cancelled"}", statusCode: 500);
        if (!task.IsCompletedSuccessfully && task.Exception is { } error)
            Console.Error.WriteLine($"[Riftway] Protocol handler for {_request.Url} threw: {error.GetBaseException()}");

        lock (_gate)
        {
            if (_cancelled)
            {
                response.Body?.Dispose();
                NativeObject.Release(callback);
                return;
            }

            _response = response;
            _body = response.Body;
            _totalLength = response.Length ?? (_body is { CanSeek: true } ? _body.Length - _body.Position : null);

            // Ranges are only resolvable against a known length; a handler's own 206 is taken as sliced.
            if (response.StatusCode == 200 && _totalLength is { } total)
                _range = ByteRange.Resolve(_request.Headers.GetValueOrDefault("Range"), total);
            _remaining = _range?.Length ?? _totalLength ?? -1;
        }

        Trace($"complete status={response.StatusCode} length={_totalLength?.ToString(CultureInfo.InvariantCulture) ?? "?"} range={_range}");
        callback->cont(callback);
        NativeObject.Release(callback);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Skip(cef_resource_handler_t* self, long bytesToSkip, long* bytesSkipped, cef_resource_skip_callback_t* callback)
    {
        NativeObject.Release(callback);
        var owner = NativeObject.Owner<ResourceHandler>(self);
        lock (owner._gate)
        {
            // A handler that answered 206 already sent only the slice.
            if (owner._response?.StatusCode == 206)
            {
                *bytesSkipped = bytesToSkip;
                return 1;
            }

            var skipped = owner.SkipBody(bytesToSkip);
            owner.Trace($"skip {bytesToSkip} -> {skipped}");
            *bytesSkipped = skipped > 0 ? skipped : ErrFailed;
            return skipped > 0 ? 1 : 0;
        }
    }

    private long SkipBody(long count)
    {
        if (_body is null) return 0;
        try
        {
            if (_body.CanSeek)
            {
                var available = Math.Min(count, _body.Length - _body.Position);
                _body.Seek(available, SeekOrigin.Current);
                return available;
            }

            Span<byte> scratch = stackalloc byte[8192];
            long skipped = 0;
            while (skipped < count)
            {
                var read = _body.Read(scratch[..(int)Math.Min(scratch.Length, count - skipped)]);
                if (read == 0) break;
                skipped += read;
            }
            return skipped;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GetResponseHeaders(cef_resource_handler_t* self, cef_response_t* response, long* responseLength, cef_string_utf16_t* redirectUrl)
    {
        var owner = NativeObject.Owner<ResourceHandler>(self);
        try { owner.WriteHeaders(response, responseLength); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Protocol response headers failed: {e}"); }
        NativeObject.Release(response);
    }

    private void WriteHeaders(cef_response_t* response, long* responseLength)
    {
        ProtocolResponse r;
        lock (_gate) r = _response ?? ProtocolResponse.Status(500);

        var status = _range is null ? r.StatusCode : 206;
        response->set_status(response, status);
        SetString(response, &response->set_status_text, r.StatusText ?? ReasonPhrase(status));

        // CEF drops Content-Type and Content-Length from the header map in favour of these.
        var (mimeType, charset) = SplitContentType(r.ContentType);
        if (mimeType is not null) SetString(response, &response->set_mime_type, mimeType);
        if (charset is not null) SetString(response, &response->set_charset, charset);

        var headers = new Dictionary<string, string>(r.Headers, StringComparer.OrdinalIgnoreCase);
        headers.Remove("Content-Type");
        headers.Remove("Content-Length");
        if (_totalLength is not null) headers.TryAdd("Accept-Ranges", "bytes");
        if (_range is { } range)
            headers["Content-Range"] = string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.End}/{_totalLength}");

        var map = CefNative.cef_string_multimap_alloc();
        foreach (var (name, value) in headers)
        {
            cef_string_utf16_t key = default;
            cef_string_utf16_t text = default;
            CefString.Set(&key, name);
            CefString.Set(&text, value);
            _ = CefNative.cef_string_multimap_append(map, &key, &text);
            CefString.Clear(&key);
            CefString.Clear(&text);
        }
        response->set_header_map(response, map);
        CefNative.cef_string_multimap_free(map);

        *responseLength = _remaining;
        Trace($"headers status={status} responseLength={_remaining}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(cef_resource_handler_t* self, void* dataOut, int bytesToRead, int* bytesRead, cef_resource_read_callback_t* callback)
    {
        NativeObject.Release(callback);
        var owner = NativeObject.Owner<ResourceHandler>(self);
        lock (owner._gate)
        {
            if (owner._body is null || owner._remaining == 0)
            {
                owner.Trace("read done");
                *bytesRead = 0;
                return 0;
            }

            var limit = owner._remaining < 0 ? bytesToRead : (int)Math.Min(bytesToRead, owner._remaining);
            try
            {
                var read = owner._body.Read(new Span<byte>(dataOut, limit));
                if (read == 0) owner.Trace("read eof");
                if (owner._remaining > 0) owner._remaining -= read;
                *bytesRead = read;
                return read > 0 ? 1 : 0;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                *bytesRead = ErrFailed;
                return 0;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Cancel(cef_resource_handler_t* self)
    {
        var owner = NativeObject.Owner<ResourceHandler>(self);
        owner.Trace("cancel");
        owner._cancellation.Cancel();
        lock (owner._gate)
        {
            owner._cancelled = true;
            owner._body?.Dispose();
            owner._body = null;
        }
    }

    private void Trace(string step)
    {
        if (s_trace) Console.Error.WriteLine($"[protocol {Environment.CurrentManagedThreadId}] {_request.Method} {_request.Url} {step}");
    }

    private static void SetString(cef_response_t* response, delegate* unmanaged[Cdecl]<cef_response_t*, cef_string_utf16_t*, void>* setter, string value)
    {
        cef_string_utf16_t text = default;
        CefString.Set(&text, value);
        (*setter)(response, &text);
        CefString.Clear(&text);
    }

    internal static (string? MimeType, string? Charset) SplitContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return (null, null);
        var parts = contentType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var charset = parts.Skip(1)
            .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(kv => kv.Length == 2 && kv[0].Equals("charset", StringComparison.OrdinalIgnoreCase))?[1]
            .Trim('"');
        return (parts.Length > 0 ? parts[0] : null, charset);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        416 => "Range Not Satisfiable",
        500 => "Internal Server Error",
        _ => "",
    };
}

/// <summary>Runs a protocol handler off CEF's threads; separate because async code cannot live in an unsafe type.</summary>
internal static class ProtocolDispatch
{
    public static Task<ProtocolResponse> Run(
        Func<ProtocolRequest, CancellationToken, Task<ProtocolResponse>> handler, ProtocolRequest request, CancellationToken token)
        => Task.Run(async () => await handler(request, token).ConfigureAwait(false) ?? ProtocolResponse.Status(500), token);
}
