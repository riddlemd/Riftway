using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A cookie as CEF reports it, copied out of CEF's memory.</summary>
public sealed record CefCookie(
    string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly,
    long? Expires, cef_cookie_same_site_t SameSite);

/// <summary><c>cef_request_context_handler_t</c>: tells when a request context can be configured.</summary>
public sealed unsafe class RequestContextHandler
{
    public RequestContextHandler()
    {
        Native = NativeObject.Create<cef_request_context_handler_t>(this);
        Native->on_request_context_initialized = &OnRequestContextInitialized;
    }

    public cef_request_context_handler_t* Native { get; }

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
            cookie->has_expires != 0 ? cookie->expires.val : null, cookie->same_site);
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
