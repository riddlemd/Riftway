namespace Riftway.Cef.Interop;

public unsafe partial struct cef_resolve_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_resolve_callback_t *, cef_errorcode_t, cef_string_list_t)")]
    public delegate* unmanaged[Cdecl]<cef_resolve_callback_t*, cef_errorcode_t, cef_string_list_t*, void> on_resolve_completed;
}
