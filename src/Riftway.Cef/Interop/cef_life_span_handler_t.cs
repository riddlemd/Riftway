namespace Riftway.Cef.Interop;

public unsafe partial struct cef_life_span_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_life_span_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, int, const cef_string_t *, const cef_string_t *, cef_window_open_disposition_t, int, const cef_popup_features_t *, struct _cef_window_info_t *, struct _cef_client_t **, struct _cef_browser_settings_t *, struct _cef_dictionary_value_t **, int *)")]
    public delegate* unmanaged[Cdecl]<cef_life_span_handler_t*, cef_browser_t*, cef_frame_t*, int, cef_string_utf16_t*, cef_string_utf16_t*, cef_window_open_disposition_t, int, cef_popup_features_t*, cef_window_info_t*, cef_client_t**, cef_browser_settings_t*, cef_dictionary_value_t**, int*, int> on_before_popup;

    [NativeTypeName("void (*)(struct _cef_life_span_handler_t *, struct _cef_browser_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_life_span_handler_t*, cef_browser_t*, int, void> on_before_popup_aborted;

    [NativeTypeName("void (*)(struct _cef_life_span_handler_t *, struct _cef_browser_t *, struct _cef_window_info_t *, struct _cef_client_t **, struct _cef_browser_settings_t *, struct _cef_dictionary_value_t **, int *)")]
    public delegate* unmanaged[Cdecl]<cef_life_span_handler_t*, cef_browser_t*, cef_window_info_t*, cef_client_t**, cef_browser_settings_t*, cef_dictionary_value_t**, int*, void> on_before_dev_tools_popup;

    [NativeTypeName("void (*)(struct _cef_life_span_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_life_span_handler_t*, cef_browser_t*, void> on_after_created;

    [NativeTypeName("int (*)(struct _cef_life_span_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_life_span_handler_t*, cef_browser_t*, int> do_close;

    [NativeTypeName("void (*)(struct _cef_life_span_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_life_span_handler_t*, cef_browser_t*, void> on_before_close;
}
