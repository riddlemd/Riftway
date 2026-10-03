namespace Oriel.Cef.Interop;

public unsafe partial struct cef_resource_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_resource_handler_t *, struct _cef_request_t *, int *, struct _cef_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_handler_t*, cef_request_t*, int*, cef_callback_t*, int> open;

    [NativeTypeName("int (*)(struct _cef_resource_handler_t *, struct _cef_request_t *, struct _cef_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_handler_t*, cef_request_t*, cef_callback_t*, int> process_request;

    [NativeTypeName("void (*)(struct _cef_resource_handler_t *, struct _cef_response_t *, int64_t *, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_handler_t*, cef_response_t*, long*, cef_string_utf16_t*, void> get_response_headers;

    [NativeTypeName("int (*)(struct _cef_resource_handler_t *, int64_t, int64_t *, struct _cef_resource_skip_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_handler_t*, long, long*, cef_resource_skip_callback_t*, int> skip;

    [NativeTypeName("int (*)(struct _cef_resource_handler_t *, void *, int, int *, struct _cef_resource_read_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_handler_t*, void*, int, int*, cef_resource_read_callback_t*, int> read;

    [NativeTypeName("int (*)(struct _cef_resource_handler_t *, void *, int, int *, struct _cef_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_handler_t*, void*, int, int*, cef_callback_t*, int> read_response;

    [NativeTypeName("void (*)(struct _cef_resource_handler_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_handler_t*, void> cancel;
}
