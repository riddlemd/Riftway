namespace Oriel.Cef.Interop;

public unsafe partial struct cef_run_context_menu_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_run_context_menu_callback_t *, int, cef_event_flags_t)")]
    public delegate* unmanaged[Cdecl]<cef_run_context_menu_callback_t*, int, cef_event_flags_t, void> cont;

    [NativeTypeName("void (*)(struct _cef_run_context_menu_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_run_context_menu_callback_t*, void> cancel;
}
