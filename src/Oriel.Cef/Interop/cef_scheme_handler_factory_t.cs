namespace Oriel.Cef.Interop;

public unsafe partial struct cef_scheme_handler_factory_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_resource_handler_t *(*)(struct _cef_scheme_handler_factory_t *, struct _cef_browser_t *, struct _cef_frame_t *, const cef_string_t *, struct _cef_request_t *)")]
    public delegate* unmanaged[Cdecl]<cef_scheme_handler_factory_t*, cef_browser_t*, cef_frame_t*, cef_string_utf16_t*, cef_request_t*, cef_resource_handler_t*> create;
}
