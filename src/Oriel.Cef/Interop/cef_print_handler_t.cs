namespace Oriel.Cef.Interop;

public unsafe partial struct cef_print_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_print_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_handler_t*, cef_browser_t*, void> on_print_start;

    [NativeTypeName("void (*)(struct _cef_print_handler_t *, struct _cef_browser_t *, struct _cef_print_settings_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_print_handler_t*, cef_browser_t*, cef_print_settings_t*, int, void> on_print_settings;

    [NativeTypeName("int (*)(struct _cef_print_handler_t *, struct _cef_browser_t *, int, struct _cef_print_dialog_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_handler_t*, cef_browser_t*, int, cef_print_dialog_callback_t*, int> on_print_dialog;

    [NativeTypeName("int (*)(struct _cef_print_handler_t *, struct _cef_browser_t *, const cef_string_t *, const cef_string_t *, struct _cef_print_job_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_handler_t*, cef_browser_t*, cef_string_utf16_t*, cef_string_utf16_t*, cef_print_job_callback_t*, int> on_print_job;

    [NativeTypeName("void (*)(struct _cef_print_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_handler_t*, cef_browser_t*, void> on_print_reset;

    [NativeTypeName("cef_size_t (*)(struct _cef_print_handler_t *, struct _cef_browser_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_print_handler_t*, cef_browser_t*, int, cef_size_t> get_pdf_paper_size;
}
