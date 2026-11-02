namespace Riftway.Cef.Interop;

public unsafe partial struct cef_jsdialog_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_jsdialog_callback_t *, int, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_jsdialog_callback_t*, int, cef_string_utf16_t*, void> cont;
}
