using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A cookie as CEF reports it, copied out of CEF's memory; times in CEF's microseconds since 1601.</summary>
public sealed record CefCookie(
    string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly,
    long? Expires, cef_cookie_same_site_t SameSite, long Creation = 0);

/// <summary><c>cef_request_context_handler_t</c>: tells when a request context can be configured, and hooks the
/// context's requests that have no page.</summary>
public sealed unsafe class RequestContextHandler
{
    public RequestContextHandler()
    {
        Native = NativeObject.Create<cef_request_context_handler_t>(this);
        Native->on_request_context_initialized = &OnRequestContextInitialized;
        Native->get_resource_request_handler = &GetResourceRequestHandler;
    }

    public cef_request_context_handler_t* Native { get; }

    /// <summary>Hooks for requests without a browser (service and shared workers); null for none.</summary>
    public ResourceRequestHandler? Resource { get; init; }

    /// <summary>Which page-less requests reach <see cref="Resource"/>; null for all. Runs on the IO thread.</summary>
    public Func<bool>? Wants { get; init; }

    // A request with a browser only comes here when the browser's own handler declined it.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_resource_request_handler_t* GetResourceRequestHandler(cef_request_context_handler_t* self, cef_browser_t* browser,
        cef_frame_t* frame, cef_request_t* request, int isNavigation, int isDownload, cef_string_utf16_t* requestInitiator, int* disableDefaultHandling)
    {
        var owner = NativeObject.Owner<RequestContextHandler>(self);
        var wants = browser is null && owner.Resource is not null
            && (owner.Wants is not { } predicate || Guard.Run(predicate, false));
        if (wants) HeaderMap.NoteInitiator(request, requestInitiator, isNavigation != 0, browser is not null);
        NativeObject.Release(request);
        if (frame is not null) NativeObject.Release(frame);
        if (browser is not null) NativeObject.Release(browser);
        if (!wants) return null;
        var handler = owner.Resource!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    /// <summary>UI thread; the context argument is only valid during the call.</summary>
    public Action<nint>? Initialized { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRequestContextInitialized(cef_request_context_handler_t* self, cef_request_context_t* context)
    {
        var owner = NativeObject.Owner<RequestContextHandler>(self);
        Guard.Run(() => owner.Initialized?.Invoke((nint)context));
        NativeObject.Release(context);
    }
}

/// <summary><c>cef_completion_callback_t</c> as a task.</summary>
public sealed unsafe class CompletionCallback : IReleaseObserver
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CompletionCallback()
    {
        Native = NativeObject.Create<cef_completion_callback_t>(this);
        Native->on_complete = &OnComplete;
    }

    public cef_completion_callback_t* Native { get; }

    public Task Task => _done.Task;

    // CEF may drop the callback without calling it, e.g. while shutting down.
    void IReleaseObserver.Released() => _done.TrySetException(new InvalidOperationException("CEF dropped the operation."));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnComplete(cef_completion_callback_t* self) => NativeObject.Owner<CompletionCallback>(self)._done.TrySetResult();
}

/// <summary><c>cef_net_log_callback_t</c> (patch 0006): completes with the error text, empty on success.</summary>
public sealed unsafe class NetLogCallback : IReleaseObserver
{
    private readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NetLogCallback()
    {
        Native = NativeObject.Create<cef_net_log_callback_t>(this);
        Native->on_net_log_complete = &OnComplete;
    }

    public cef_net_log_callback_t* Native { get; }

    public Task<string> Task => _done.Task;

    void IReleaseObserver.Released() => _done.TrySetException(new InvalidOperationException("CEF dropped the operation."));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnComplete(cef_net_log_callback_t* self, cef_string_utf16_t* error)
        => NativeObject.Owner<NetLogCallback>(self)._done.TrySetResult(CefString.Read(error));
}

/// <summary><c>cef_cookie_visitor_t</c>: collects every cookie, then completes.</summary>
public sealed unsafe class CookieVisitor : IReleaseObserver
{
    private readonly List<CefCookie> _cookies = [];
    private readonly TaskCompletionSource<IReadOnlyList<CefCookie>> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CookieVisitor()
    {
        Native = NativeObject.Create<cef_cookie_visitor_t>(this);
        Native->visit = &Visit;
    }

    public cef_cookie_visitor_t* Native { get; }

    public Task<IReadOnlyList<CefCookie>> Task => _done.Task;

    // With no cookies to visit, CEF only releases the visitor.
    void IReleaseObserver.Released()
    {
        lock (_cookies) _done.TrySetResult([.. _cookies]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Visit(cef_cookie_visitor_t* self, cef_cookie_t* cookie, int count, int total, int* deleteCookie)
    {
        var owner = NativeObject.Owner<CookieVisitor>(self);
        *deleteCookie = 0;
        var copy = new CefCookie(
            CefString.Read(&cookie->name), CefString.Read(&cookie->value), CefString.Read(&cookie->domain),
            CefString.Read(&cookie->path), cookie->secure != 0, cookie->httponly != 0,
            cookie->has_expires != 0 ? cookie->expires.val : null, cookie->same_site, cookie->creation.val);
        lock (owner._cookies) owner._cookies.Add(copy);
        return 1;
    }
}

/// <summary><c>cef_set_cookie_callback_t</c>: whether the cookie was stored.</summary>
public sealed unsafe class SetCookieCallback : IReleaseObserver
{
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SetCookieCallback()
    {
        Native = NativeObject.Create<cef_set_cookie_callback_t>(this);
        Native->on_complete = &OnComplete;
    }

    public cef_set_cookie_callback_t* Native { get; }

    public Task<bool> Task => _done.Task;

    void IReleaseObserver.Released() => _done.TrySetResult(false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnComplete(cef_set_cookie_callback_t* self, int success)
        => NativeObject.Owner<SetCookieCallback>(self)._done.TrySetResult(success != 0);
}

/// <summary><c>cef_delete_cookies_callback_t</c>: how many cookies were deleted.</summary>
public sealed unsafe class DeleteCookiesCallback : IReleaseObserver
{
    private readonly TaskCompletionSource<int> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DeleteCookiesCallback()
    {
        Native = NativeObject.Create<cef_delete_cookies_callback_t>(this);
        Native->on_complete = &OnComplete;
    }

    public cef_delete_cookies_callback_t* Native { get; }

    public Task<int> Task => _done.Task;

    void IReleaseObserver.Released() => _done.TrySetException(new InvalidOperationException("CEF dropped the operation."));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnComplete(cef_delete_cookies_callback_t* self, int deleted)
        => NativeObject.Owner<DeleteCookiesCallback>(self)._done.TrySetResult(deleted);
}

/// <summary><c>cef_resolve_callback_t</c>: a host's addresses, or CEF's error.</summary>
public sealed unsafe class ResolveCallback : IReleaseObserver
{
    private readonly TaskCompletionSource<(cef_errorcode_t Error, IReadOnlyList<string> Addresses)> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ResolveCallback()
    {
        Native = NativeObject.Create<cef_resolve_callback_t>(this);
        Native->on_resolve_completed = &OnResolveCompleted;
    }

    public cef_resolve_callback_t* Native { get; }

    public Task<(cef_errorcode_t Error, IReadOnlyList<string> Addresses)> Task => _done.Task;

    void IReleaseObserver.Released() => _done.TrySetException(new InvalidOperationException("CEF dropped the operation."));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResolveCompleted(cef_resolve_callback_t* self, cef_errorcode_t error, cef_string_list_t* addresses)
    {
        var owner = NativeObject.Owner<ResolveCallback>(self);
        var list = new List<string>();
        if (addresses is not null)
        {
            for (nuint i = 0; i < CefNative.cef_string_list_size(addresses); i++)
            {
                cef_string_utf16_t address = default;
                if (CefNative.cef_string_list_value(addresses, i, &address) != 0) list.Add(CefString.Read(&address));
                CefString.Clear(&address);
            }
        }
        owner._done.TrySetResult((error, list));
    }
}

/// <summary><c>cef_resolve_proxy_callback_t</c> (patch 0008): completes with the net error and the PAC-format proxy list.</summary>
public sealed unsafe class ResolveProxyCallback : IReleaseObserver
{
    private readonly TaskCompletionSource<(cef_errorcode_t Error, string Proxy)> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ResolveProxyCallback()
    {
        Native = NativeObject.Create<cef_resolve_proxy_callback_t>(this);
        Native->on_resolve_proxy_completed = &OnResolveProxyCompleted;
    }

    public cef_resolve_proxy_callback_t* Native { get; }

    public Task<(cef_errorcode_t Error, string Proxy)> Task => _done.Task;

    void IReleaseObserver.Released() => _done.TrySetException(new InvalidOperationException("CEF dropped the operation."));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResolveProxyCompleted(cef_resolve_proxy_callback_t* self, cef_errorcode_t error, cef_string_utf16_t* proxy)
        => NativeObject.Owner<ResolveProxyCallback>(self)._done.TrySetResult((error, CefString.Read(proxy)));
}

/// <summary><c>cef_gpu_info_callback_t</c> (patch 0013): completes with the GPU information as JSON, or fails with
/// CEF's error.</summary>
public sealed unsafe class GpuInfoCallback : IReleaseObserver
{
    private readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly delegate*<cef_dictionary_value_t*, string> _toJson;

    public GpuInfoCallback() : this(&CefPatchedApi.DictionaryJson)
    {
    }

    internal GpuInfoCallback(delegate*<cef_dictionary_value_t*, string> toJson)
    {
        _toJson = toJson;
        Native = NativeObject.Create<cef_gpu_info_callback_t>(this);
        Native->on_gpu_info = &OnGpuInfo;
    }

    public cef_gpu_info_callback_t* Native { get; }

    public Task<string> Task => _done.Task;

    void IReleaseObserver.Released() => _done.TrySetException(new InvalidOperationException("CEF dropped the operation."));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGpuInfo(cef_gpu_info_callback_t* self, cef_dictionary_value_t* info, cef_string_utf16_t* error)
    {
        var owner = NativeObject.Owner<GpuInfoCallback>(self);
        if (info == null)
        {
            owner._done.TrySetException(new InvalidOperationException(CefString.Read(error)));
            return;
        }
        try { owner._done.TrySetResult(owner._toJson(info)); }
        catch (Exception e) { owner._done.TrySetException(e); }
    }
}

/// <summary><c>cef_end_tracing_callback_t</c>: completes with the trace file's path.</summary>
public sealed unsafe class EndTracingCallback : IReleaseObserver
{
    private readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EndTracingCallback()
    {
        Native = NativeObject.Create<cef_end_tracing_callback_t>(this);
        Native->on_end_tracing_complete = &OnEndTracingComplete;
    }

    public cef_end_tracing_callback_t* Native { get; }

    public Task<string> Task => _done.Task;

    void IReleaseObserver.Released() => _done.TrySetException(new InvalidOperationException("CEF dropped the operation."));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEndTracingComplete(cef_end_tracing_callback_t* self, cef_string_utf16_t* tracingFile)
        => NativeObject.Owner<EndTracingCallback>(self)._done.TrySetResult(CefString.Read(tracingFile));
}
