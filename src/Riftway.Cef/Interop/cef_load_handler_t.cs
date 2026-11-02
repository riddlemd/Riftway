namespace Riftway.Cef.Interop;

public unsafe partial struct cef_load_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_load_handler_t *, struct _cef_browser_t *, int, int, int)")]
    public delegate* unmanaged[Cdecl]<cef_load_handler_t*, cef_browser_t*, int, int, int, void> on_loading_state_change;

    [NativeTypeName("void (*)(struct _cef_load_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, cef_transition_type_t)")]
    public delegate* unmanaged[Cdecl]<cef_load_handler_t*, cef_browser_t*, cef_frame_t*, cef_transition_type_t, void> on_load_start;

    [NativeTypeName("void (*)(struct _cef_load_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_load_handler_t*, cef_browser_t*, cef_frame_t*, int, void> on_load_end;

    [NativeTypeName("void (*)(struct _cef_load_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, cef_errorcode_t, const cef_string_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_load_handler_t*, cef_browser_t*, cef_frame_t*, cef_errorcode_t, cef_string_utf16_t*, cef_string_utf16_t*, void> on_load_error;
}
