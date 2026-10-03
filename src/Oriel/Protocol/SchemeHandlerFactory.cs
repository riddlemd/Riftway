using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary><c>cef_scheme_handler_factory_t</c>: one <see cref="ResourceHandler"/> per request.</summary>
internal sealed unsafe class SchemeHandlerFactory
{
    private readonly Func<ProtocolRequest, CancellationToken, Task<ProtocolResponse>> _handler;

    public SchemeHandlerFactory(Func<ProtocolRequest, CancellationToken, Task<ProtocolResponse>> handler)
    {
        _handler = handler;
        Native = NativeObject.Create<cef_scheme_handler_factory_t>(this);
        Native->create = &Create;
    }

    public cef_scheme_handler_factory_t* Native { get; }

    // Runs on CEF's IO thread, which all networking shares: snapshot and return, never wait.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_resource_handler_t* Create(
        cef_scheme_handler_factory_t* self, cef_browser_t* browser, cef_frame_t* frame,
        cef_string_utf16_t* schemeName, cef_request_t* request)
    {
        try
        {
            var owner = NativeObject.Owner<SchemeHandlerFactory>(self);
            return new ResourceHandler(Snapshot(request), owner._handler).Native;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Oriel] Protocol request could not be read: {e}");
            return null;
        }
        finally
        {
            NativeObject.Release(browser);
            NativeObject.Release(frame);
            NativeObject.Release(request);
        }
    }

    private static ProtocolRequest Snapshot(cef_request_t* request)
    {
        var url = CefString.ReadUserFree(request->get_url(request));
        var method = CefString.ReadUserFree(request->get_method(request));
        var referrer = CefString.ReadUserFree(request->get_referrer_url(request));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var map = CefNative.cef_string_multimap_alloc();
        request->get_header_map(request, map);
        var count = CefNative.cef_string_multimap_size(map);
        for (nuint i = 0; i < count; i++)
        {
            cef_string_utf16_t key = default;
            cef_string_utf16_t value = default;
            if (CefNative.cef_string_multimap_key(map, i, &key) == 0 || CefNative.cef_string_multimap_value(map, i, &value) == 0)
            {
                CefString.Clear(&key);
                continue;
            }

            var name = CefString.Read(&key);
            var text = CefString.Read(&value);
            headers[name] = headers.TryGetValue(name, out var existing) ? $"{existing}, {text}" : text;
            CefString.Clear(&key);
            CefString.Clear(&value);
        }
        CefNative.cef_string_multimap_free(map);

        byte[]? body = null;
        var files = new List<string>();
        var postData = request->get_post_data(request);
        if (postData is not null)
        {
            var elementCount = postData->get_element_count(postData);
            var elements = new cef_post_data_element_t*[(int)elementCount];
            using var buffer = new MemoryStream();
            fixed (cef_post_data_element_t** first = elements)
            {
                postData->get_elements(postData, &elementCount, first);
            }

            foreach (var element in elements)
            {
                if (element is null) continue;
                switch (element->get_type(element))
                {
                    case cef_postdataelement_type_t.PDE_TYPE_BYTES:
                        var size = element->get_bytes_count(element);
                        var bytes = new byte[(int)size];
                        fixed (byte* data = bytes) element->get_bytes(element, size, data);
                        buffer.Write(bytes);
                        break;
                    case cef_postdataelement_type_t.PDE_TYPE_FILE:
                        files.Add(CefString.ReadUserFree(element->get_file(element)));
                        break;
                }
                NativeObject.Release(element);
            }

            body = buffer.ToArray();
            NativeObject.Release(postData);
        }

        return new ProtocolRequest(url, method, referrer.Length == 0 ? null : referrer, headers, body, files);
    }
}
