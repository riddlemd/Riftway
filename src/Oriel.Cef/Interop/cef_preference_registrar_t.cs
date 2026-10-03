namespace Oriel.Cef.Interop;

public unsafe partial struct cef_preference_registrar_t
{
    public cef_base_scoped_t @base;

    [NativeTypeName("int (*)(struct _cef_preference_registrar_t *, const cef_string_t *, struct _cef_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_preference_registrar_t*, cef_string_utf16_t*, cef_value_t*, int> add_preference;
}
