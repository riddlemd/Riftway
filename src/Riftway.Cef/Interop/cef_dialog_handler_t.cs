namespace Riftway.Cef.Interop;

public unsafe partial struct cef_dialog_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_dialog_handler_t *, struct _cef_browser_t *, cef_file_dialog_mode_t, const cef_string_t *, const cef_string_t *, cef_string_list_t, cef_string_list_t, cef_string_list_t, struct _cef_file_dialog_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_dialog_handler_t*, cef_browser_t*, cef_file_dialog_mode_t, cef_string_utf16_t*, cef_string_utf16_t*, cef_string_list_t*, cef_string_list_t*, cef_string_list_t*, cef_file_dialog_callback_t*, int> on_file_dialog;
}
