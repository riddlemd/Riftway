namespace Riftway.Cef.Interop;

public unsafe partial struct cef_focus_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_focus_handler_t *, struct _cef_browser_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_focus_handler_t*, cef_browser_t*, int, void> on_take_focus;

    [NativeTypeName("int (*)(struct _cef_focus_handler_t *, struct _cef_browser_t *, cef_focus_source_t)")]
    public delegate* unmanaged[Cdecl]<cef_focus_handler_t*, cef_browser_t*, cef_focus_source_t, int> on_set_focus;

    [NativeTypeName("void (*)(struct _cef_focus_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_focus_handler_t*, cef_browser_t*, void> on_got_focus;
}
