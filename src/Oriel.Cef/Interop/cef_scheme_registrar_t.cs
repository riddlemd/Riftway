namespace Oriel.Cef.Interop;

public unsafe partial struct cef_scheme_registrar_t
{
    public cef_base_scoped_t @base;

    [NativeTypeName("int (*)(struct _cef_scheme_registrar_t *, const cef_string_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_scheme_registrar_t*, cef_string_utf16_t*, int, int> add_custom_scheme;
}
