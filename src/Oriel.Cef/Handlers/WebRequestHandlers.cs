using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A request as CEF reported it; CEF's request object is valid only during its callback.</summary>
public sealed record RequestInfo(ulong Id, string Url, string Method, string Referrer, cef_resource_type_t ResourceType,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>A response as CEF reported it.</summary>
public sealed record ResponseInfo(int Status, string StatusText, cef_errorcode_t Error, IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>What to do with a request before it is sent: cancel it, send it to another URL, or replace its headers.</summary>
public sealed record RequestDecision(bool Cancel = false, string? RedirectUrl = null, IReadOnlyList<KeyValuePair<string, string>>? Headers = null);

/// <summary>Reads and writes CEF's header multimaps.</summary>
public static unsafe class HeaderMap
{
    public static IReadOnlyList<KeyValuePair<string, string>> Read(cef_request_t* request)
        => Read(map => request->get_header_map(request, map));

    public static IReadOnlyList<KeyValuePair<string, string>> Read(cef_response_t* response)
        => Read(map => response->get_header_map(response, map));

    private delegate void Fill(cef_string_multimap_t* map);

    private static IReadOnlyList<KeyValuePair<string, string>> Read(Fill fill)
    {
        var map = CefNative.cef_string_multimap_alloc();
        try
        {
            fill(map);
            var count = CefNative.cef_string_multimap_size(map);
            var headers = new List<KeyValuePair<string, string>>((int)count);
            for (nuint i = 0; i < count; i++)
            {
                cef_string_utf16_t key = default, value = default;
                if (CefNative.cef_string_multimap_key(map, i, &key) != 0 && CefNative.cef_string_multimap_value(map, i, &value) != 0)
                    headers.Add(new(CefString.Read(&key), CefString.Read(&value)));
                CefString.Clear(&key);
                CefString.Clear(&value);
            }
            return headers;
        }
        finally { CefNative.cef_string_multimap_free(map); }
    }

    public static void Write(cef_request_t* request, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var map = CefNative.cef_string_multimap_alloc();
        foreach (var (name, value) in headers)
        {
            cef_string_utf16_t key = default, text = default;
            CefString.Set(&key, name);
            CefString.Set(&text, value);
            _ = CefNative.cef_string_multimap_append(map, &key, &text);
            CefString.Clear(&key);
            CefString.Clear(&text);
        }
        request->set_header_map(request, map);
        CefNative.cef_string_multimap_free(map);
    }

    internal static RequestInfo Info(cef_request_t* request) => new(
        request->get_identifier(request),
        CefString.ReadUserFree(request->get_url(request)),
        CefString.ReadUserFree(request->get_method(request)),
        CefString.ReadUserFree(request->get_referrer_url(request)),
        request->get_resource_type(request),
        Read(request));

    internal static ResponseInfo Info(cef_response_t* response) => new(
        response->get_status(response),
        CefString.ReadUserFree(response->get_status_text(response)),
        response->get_error(response),
        Read(response));
}
