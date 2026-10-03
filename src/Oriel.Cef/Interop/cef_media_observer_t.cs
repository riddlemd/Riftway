namespace Oriel.Cef.Interop;

public unsafe partial struct cef_media_observer_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_media_observer_t *, size_t, struct _cef_media_sink_t *const *)")]
    public delegate* unmanaged[Cdecl]<cef_media_observer_t*, nuint, cef_media_sink_t**, void> on_sinks;

    [NativeTypeName("void (*)(struct _cef_media_observer_t *, size_t, struct _cef_media_route_t *const *)")]
    public delegate* unmanaged[Cdecl]<cef_media_observer_t*, nuint, cef_media_route_t**, void> on_routes;

    [NativeTypeName("void (*)(struct _cef_media_observer_t *, struct _cef_media_route_t *, cef_media_route_connection_state_t)")]
    public delegate* unmanaged[Cdecl]<cef_media_observer_t*, cef_media_route_t*, cef_media_route_connection_state_t, void> on_route_state_changed;

    [NativeTypeName("void (*)(struct _cef_media_observer_t *, struct _cef_media_route_t *, const void *, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_media_observer_t*, cef_media_route_t*, void*, nuint, void> on_route_message_received;
}
