namespace Oriel.Cef.Interop;

public unsafe partial struct cef_media_router_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_registration_t *(*)(struct _cef_media_router_t *, struct _cef_media_observer_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_router_t*, cef_media_observer_t*, cef_registration_t*> add_observer;

    [NativeTypeName("struct _cef_media_source_t *(*)(struct _cef_media_router_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_router_t*, cef_string_utf16_t*, cef_media_source_t*> get_source;

    [NativeTypeName("void (*)(struct _cef_media_router_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_router_t*, void> notify_current_sinks;

    [NativeTypeName("void (*)(struct _cef_media_router_t *, struct _cef_media_source_t *, struct _cef_media_sink_t *, struct _cef_media_route_create_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_router_t*, cef_media_source_t*, cef_media_sink_t*, cef_media_route_create_callback_t*, void> create_route;

    [NativeTypeName("void (*)(struct _cef_media_router_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_router_t*, void> notify_current_routes;
}
