namespace Riftway.Cef.Interop;

public unsafe partial struct cef_display_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_frame_t*, cef_string_utf16_t*, void> on_address_change;

    [NativeTypeName("void (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_string_utf16_t*, void> on_title_change;

    [NativeTypeName("void (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, cef_string_list_t)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_string_list_t*, void> on_favicon_urlchange;

    [NativeTypeName("void (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, int, void> on_fullscreen_mode_change;

    [NativeTypeName("int (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_string_utf16_t*, int> on_tooltip;

    [NativeTypeName("void (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_string_utf16_t*, void> on_status_message;

    [NativeTypeName("int (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, cef_log_severity_t, const cef_string_t *, const cef_string_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_log_severity_t, cef_string_utf16_t*, cef_string_utf16_t*, int, int> on_console_message;

    [NativeTypeName("int (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, const cef_size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_size_t*, int> on_auto_resize;

    [NativeTypeName("void (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, double)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, double, void> on_loading_progress_change;

    [NativeTypeName("int (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, cef_cursor_handle_t, cef_cursor_type_t, const cef_cursor_info_t *)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, void*, cef_cursor_type_t, cef_cursor_info_t*, int> on_cursor_change;

    [NativeTypeName("void (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, int, int)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, int, int, void> on_media_access_change;

    [NativeTypeName("int (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, const cef_rect_t *)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_rect_t*, int> on_contents_bounds_change;

    [NativeTypeName("int (*)(struct _cef_display_handler_t *, struct _cef_browser_t *, cef_rect_t *)")]
    public delegate* unmanaged[Cdecl]<cef_display_handler_t*, cef_browser_t*, cef_rect_t*, int> get_root_window_screen_rect;
}
