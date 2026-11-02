namespace Riftway.Cef.Interop;

public unsafe partial struct cef_component_update_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_component_update_callback_t *, const cef_string_t *, cef_component_update_error_t)")]
    public delegate* unmanaged[Cdecl]<cef_component_update_callback_t*, cef_string_utf16_t*, cef_component_update_error_t, void> on_complete;
}
