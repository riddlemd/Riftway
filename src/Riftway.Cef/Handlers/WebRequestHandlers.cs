using Riftway.Cef.Interop;

namespace Riftway.Cef.Handlers;

/// <summary>A request as CEF reported it; CEF's request object is valid only during its callback.</summary>
public sealed record RequestInfo(ulong Id, string Url, string Method, string Referrer, cef_resource_type_t ResourceType,
    IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    /// <summary>The body's elements CEF represents (bytes and files); null for a request without a body.</summary>
    public IReadOnlyList<UploadElement>? Upload { get; init; }

    /// <summary>The requesting frame's name and URL, then each parent's up to the main frame; null without a frame.</summary>
    public IReadOnlyList<(string Name, string Url)>? FramePath { get; init; }

    /// <summary>The origin that issued the request, as CEF gave it when the request began; null for one the browser
    /// started itself.</summary>
    public string? Initiator { get; init; }
}

/// <summary>One element of a request body: bytes, or a file's path.</summary>
public sealed record UploadElement(byte[]? Bytes, string? File);

/// <summary>A response as CEF reported it.</summary>
public sealed record ResponseInfo(int Status, string StatusText, cef_errorcode_t Error, IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>What to do with a request before it is sent: cancel it, send it to another URL, or replace its headers.</summary>
public sealed record RequestDecision(bool Cancel = false, string? RedirectUrl = null, IReadOnlyList<KeyValuePair<string, string>>? Headers = null);

/// <summary>Reads and writes CEF's header multimaps.</summary>
public static unsafe class HeaderMap
{
    // CEF names a request's initiator only when it picks the request's handler; ids are unique in the process. The
    // first answer stays, as Electron keeps the original initiator across redirects.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> s_initiators = new();

    /// <summary>Remembers <paramref name="requestInitiator"/> for <paramref name="request"/>; IO thread.</summary>
    public static void NoteInitiator(cef_request_t* request, cef_string_utf16_t* requestInitiator, bool isNavigation, bool hasPage)
    {
        if (requestInitiator is null || CefString.Read(requestInitiator) is not { Length: > 0 } initiator) return;
        // CEF serializes a missing initiator as an opaque "null"; Chromium leaves it out for navigations the browser
        // starts and requests without a page, which a page's own requests never are.
        if (initiator == "null" && (isNavigation || !hasPage)) return;
        s_initiators.TryAdd(request->get_identifier(request), initiator);
    }

    /// <summary>Forgets a finished request's initiator.</summary>
    public static void ForgetInitiator(ulong id) => s_initiators.TryRemove(id, out _);

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

    internal static RequestInfo Info(cef_request_t* request, cef_frame_t* frame) => new(
        request->get_identifier(request),
        CefString.ReadUserFree(request->get_url(request)),
        CefString.ReadUserFree(request->get_method(request)),
        CefString.ReadUserFree(request->get_referrer_url(request)),
        request->get_resource_type(request),
        Read(request))
    {
        Upload = Upload(request),
        FramePath = frame is null ? null : FramePath(frame),
        Initiator = s_initiators.TryGetValue(request->get_identifier(request), out var initiator) ? initiator : null,
    };

    // Blob and stream bodies are elements CEF leaves out (has_excluded_elements): they cannot be listed.
    private static List<UploadElement>? Upload(cef_request_t* request)
    {
        var postData = request->get_post_data(request);
        if (postData is null) return null;
        var count = postData->get_element_count(postData);
        var elements = new cef_post_data_element_t*[(int)count];
        fixed (cef_post_data_element_t** first = elements) postData->get_elements(postData, &count, first);
        var upload = new List<UploadElement>();
        foreach (var element in elements)
        {
            if (element is null) continue;
            switch (element->get_type(element))
            {
                case cef_postdataelement_type_t.PDE_TYPE_BYTES:
                    var size = element->get_bytes_count(element);
                    var bytes = new byte[(int)size];
                    fixed (byte* data = bytes) element->get_bytes(element, size, data);
                    upload.Add(new UploadElement(bytes, null));
                    break;
                case cef_postdataelement_type_t.PDE_TYPE_FILE:
                    upload.Add(new UploadElement(null, CefString.ReadUserFree(element->get_file(element))));
                    break;
            }
            NativeObject.Release(element);
        }
        NativeObject.Release(postData);
        return upload;
    }

    /// <summary>The frame's name and current URL, then its parent's, up to the main frame. Borrows the frame.</summary>
    public static IReadOnlyList<(string Name, string Url)> FramePath(cef_frame_t* frame)
    {
        var path = new List<(string, string)>();
        NativeObject.AddRef(frame);
        for (var current = frame; current is not null;)
        {
            path.Add((CefString.ReadUserFree(current->get_name(current)), CefString.ReadUserFree(current->get_url(current))));
            var parent = current->get_parent(current);
            NativeObject.Release(current);
            current = parent;
        }
        return path;
    }

    internal static ResponseInfo Info(cef_response_t* response) => new(
        response->get_status(response),
        CefString.ReadUserFree(response->get_status_text(response)),
        response->get_error(response),
        Read(response));
}
