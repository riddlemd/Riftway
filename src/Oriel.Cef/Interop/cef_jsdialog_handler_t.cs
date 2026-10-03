namespace Oriel.Cef.Interop;

public unsafe partial struct cef_jsdialog_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_jsdialog_handler_t *, struct _cef_browser_t *, const cef_string_t *, cef_jsdialog_type_t, const cef_string_t *, const cef_string_t *, struct _cef_jsdialog_callback_t *, int *)")]
    public delegate* unmanaged[Cdecl]<cef_jsdialog_handler_t*, cef_browser_t*, cef_string_utf16_t*, cef_jsdialog_type_t, cef_string_utf16_t*, cef_string_utf16_t*, cef_jsdialog_callback_t*, int*, int> on_jsdialog;

    [NativeTypeName("int (*)(struct _cef_jsdialog_handler_t *, struct _cef_browser_t *, const cef_string_t *, int, struct _cef_jsdialog_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_jsdialog_handler_t*, cef_browser_t*, cef_string_utf16_t*, int, cef_jsdialog_callback_t*, int> on_before_unload_dialog;

    [NativeTypeName("void (*)(struct _cef_jsdialog_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_jsdialog_handler_t*, cef_browser_t*, void> on_reset_dialog_state;

    [NativeTypeName("void (*)(struct _cef_jsdialog_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_jsdialog_handler_t*, cef_browser_t*, void> on_dialog_closed;
}
