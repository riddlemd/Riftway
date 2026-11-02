namespace Riftway.Cef.Interop;

public unsafe partial struct cef_keyboard_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_keyboard_handler_t *, struct _cef_browser_t *, const cef_key_event_t *, cef_event_handle_t, int *)")]
    public delegate* unmanaged[Cdecl]<cef_keyboard_handler_t*, cef_browser_t*, cef_key_event_t*, void*, int*, int> on_pre_key_event;

    [NativeTypeName("int (*)(struct _cef_keyboard_handler_t *, struct _cef_browser_t *, const cef_key_event_t *, cef_event_handle_t)")]
    public delegate* unmanaged[Cdecl]<cef_keyboard_handler_t*, cef_browser_t*, cef_key_event_t*, void*, int> on_key_event;
}
