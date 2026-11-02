namespace Riftway.Cef.Interop;

public unsafe partial struct cef_request_context_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_request_context_handler_t *, struct _cef_request_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_request_context_handler_t*, cef_request_context_t*, void> on_request_context_initialized;

    [NativeTypeName("struct _cef_resource_request_handler_t *(*)(struct _cef_request_context_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, struct _cef_request_t *, int, int, const cef_string_t *, int *)")]
    public delegate* unmanaged[Cdecl]<cef_request_context_handler_t*, cef_browser_t*, cef_frame_t*, cef_request_t*, int, int, cef_string_utf16_t*, int*, cef_resource_request_handler_t*> get_resource_request_handler;
}
