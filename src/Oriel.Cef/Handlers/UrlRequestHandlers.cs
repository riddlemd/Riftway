using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A server's request for credentials; answer through the callback, which carries a reference the
/// receiver must release once it has answered.</summary>
public unsafe delegate bool UrlRequestAuthCallback(bool isProxy, string host, int port, string realm, string scheme, cef_auth_callback_t* callback);

/// <summary><c>cef_urlrequest_client_t</c>: progress, body chunks, credentials and completion of one
/// <c>cef_urlrequest_t</c>. CEF calls it on the thread that created the request.</summary>
public sealed unsafe class UrlRequestClient
{
    public UrlRequestClient()
    {
        Native = NativeObject.Create<cef_urlrequest_client_t>(this);
        Native->on_request_complete = &OnRequestComplete;
        Native->on_upload_progress = &OnUploadProgress;
        Native->on_download_progress = &OnDownloadProgress;
        Native->on_download_data = &OnDownloadData;
        Native->get_auth_credentials = &GetAuthCredentials;
    }

    public cef_urlrequest_client_t* Native { get; }

    /// <summary>The request finished, failed or was cancelled; the pointer is valid during the call.</summary>
    public Action<nint>? Completed { get; set; }

    /// <summary>Bytes sent so far and the total (-1 when unknown).</summary>
    public Action<long, long>? UploadProgress { get; set; }

    /// <summary>The request; bytes received so far and the total (-1 when unknown).</summary>
    public Action<nint, long, long>? DownloadProgress { get; set; }

    /// <summary>The request and a chunk of the body, copied out of CEF's buffer.</summary>
    public Action<nint, byte[]>? DownloadData { get; set; }

    /// <summary>Returns true when it will answer the callback; false cancels the challenge.</summary>
    public UrlRequestAuthCallback? AuthCredentials { get; set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRequestComplete(cef_urlrequest_client_t* self, cef_urlrequest_t* request)
    {
        var owner = NativeObject.Owner<UrlRequestClient>(self);
        Guard.Run(() => owner.Completed?.Invoke((nint)request));
        NativeObject.Release(request);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUploadProgress(cef_urlrequest_client_t* self, cef_urlrequest_t* request, long current, long total)
    {
        var owner = NativeObject.Owner<UrlRequestClient>(self);
        Guard.Run(() => owner.UploadProgress?.Invoke(current, total));
        NativeObject.Release(request);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDownloadProgress(cef_urlrequest_client_t* self, cef_urlrequest_t* request, long current, long total)
    {
        var owner = NativeObject.Owner<UrlRequestClient>(self);
        Guard.Run(() => owner.DownloadProgress?.Invoke((nint)request, current, total));
        NativeObject.Release(request);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDownloadData(cef_urlrequest_client_t* self, cef_urlrequest_t* request, void* data, nuint length)
    {
        var owner = NativeObject.Owner<UrlRequestClient>(self);
        var chunk = new ReadOnlySpan<byte>(data, checked((int)length)).ToArray();
        Guard.Run(() => owner.DownloadData?.Invoke((nint)request, chunk));
        NativeObject.Release(request);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetAuthCredentials(cef_urlrequest_client_t* self, int isProxy, cef_string_utf16_t* host, int port,
        cef_string_utf16_t* realm, cef_string_utf16_t* scheme, cef_auth_callback_t* callback)
    {
        var owner = NativeObject.Owner<UrlRequestClient>(self);
        var h = CefString.Read(host);
        var r = CefString.Read(realm);
        var s = CefString.Read(scheme);
        var handled = owner.AuthCredentials is { } answer && Guard.Run(() => answer(isProxy != 0, h, port, r, s, callback), false);
        if (!handled) NativeObject.Release(callback);
        return handled ? 1 : 0;
    }
}

/// <summary>Reads and writes CEF string multimaps (header maps).</summary>
public static unsafe class CefStringMultimap
{
    /// <summary>The map's entries in CEF's order.</summary>
    public static List<KeyValuePair<string, string>> Read(cef_string_multimap_t* map)
    {
        var entries = new List<KeyValuePair<string, string>>();
        var size = CefNative.cef_string_multimap_size(map);
        for (nuint i = 0; i < size; i++)
        {
            cef_string_utf16_t key = default, value = default;
            CefNative.cef_string_multimap_key(map, i, &key);
            CefNative.cef_string_multimap_value(map, i, &value);
            entries.Add(new(CefString.Read(&key), CefString.Read(&value)));
            CefString.Clear(&key);
            CefString.Clear(&value);
        }
        return entries;
    }

    /// <summary>A new map holding <paramref name="entries"/>; the caller frees it with <c>cef_string_multimap_free</c>.</summary>
    public static cef_string_multimap_t* Create(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var map = CefNative.cef_string_multimap_alloc();
        foreach (var (name, value) in entries)
        {
            cef_string_utf16_t key = default, text = default;
            CefString.Set(&key, name);
            CefString.Set(&text, value);
            CefNative.cef_string_multimap_append(map, &key, &text);
            CefString.Clear(&key);
            CefString.Clear(&text);
        }
        return map;
    }
}
