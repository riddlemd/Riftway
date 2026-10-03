namespace Oriel.Cef.Interop;

public unsafe partial struct cef_setting_observer_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_setting_observer_t *, const cef_string_t *, const cef_string_t *, cef_content_setting_types_t)")]
    public delegate* unmanaged[Cdecl]<cef_setting_observer_t*, cef_string_utf16_t*, cef_string_utf16_t*, cef_content_setting_types_t, void> on_setting_changed;
}
