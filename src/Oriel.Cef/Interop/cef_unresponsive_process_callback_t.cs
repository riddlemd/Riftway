namespace Oriel.Cef.Interop;

public unsafe partial struct cef_unresponsive_process_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_unresponsive_process_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_unresponsive_process_callback_t*, void> wait;

    [NativeTypeName("void (*)(struct _cef_unresponsive_process_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_unresponsive_process_callback_t*, void> terminate;
}
