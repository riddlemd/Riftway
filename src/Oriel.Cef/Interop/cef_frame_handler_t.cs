namespace Oriel.Cef.Interop;

public unsafe partial struct cef_frame_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_frame_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_handler_t*, cef_browser_t*, cef_frame_t*, void> on_frame_created;

    [NativeTypeName("void (*)(struct _cef_frame_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_handler_t*, cef_browser_t*, cef_frame_t*, void> on_frame_destroyed;

    [NativeTypeName("void (*)(struct _cef_frame_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_frame_handler_t*, cef_browser_t*, cef_frame_t*, int, void> on_frame_attached;

    [NativeTypeName("void (*)(struct _cef_frame_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_handler_t*, cef_browser_t*, cef_frame_t*, void> on_frame_detached;

    [NativeTypeName("void (*)(struct _cef_frame_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_handler_t*, cef_browser_t*, cef_frame_t*, cef_frame_t*, void> on_main_frame_changed;
}
