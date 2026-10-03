namespace Oriel.Cef.Interop;

public unsafe partial struct cef_auth_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_auth_callback_t *, const cef_string_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_auth_callback_t*, cef_string_utf16_t*, cef_string_utf16_t*, void> cont;

    [NativeTypeName("void (*)(struct _cef_auth_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_auth_callback_t*, void> cancel;
}
