namespace Riftway.Cef.Interop;

public unsafe partial struct cef_pdf_print_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_pdf_print_callback_t *, const cef_string_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_pdf_print_callback_t*, cef_string_utf16_t*, int, void> on_pdf_print_finished;
}
