namespace Oriel.Cef.Interop;

public unsafe partial struct cef_media_route_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_media_route_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_route_t*, cef_string_utf16_t*> get_id;

    [NativeTypeName("struct _cef_media_source_t *(*)(struct _cef_media_route_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_route_t*, cef_media_source_t*> get_source;

    [NativeTypeName("struct _cef_media_sink_t *(*)(struct _cef_media_route_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_route_t*, cef_media_sink_t*> get_sink;

    [NativeTypeName("void (*)(struct _cef_media_route_t *, const void *, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_media_route_t*, void*, nuint, void> send_route_message;

    [NativeTypeName("void (*)(struct _cef_media_route_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_route_t*, void> terminate;
}
