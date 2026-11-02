namespace Riftway.Cef.Interop;

public unsafe partial struct cef_client_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_audio_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_audio_handler_t*> get_audio_handler;

    [NativeTypeName("struct _cef_command_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_command_handler_t*> get_command_handler;

    [NativeTypeName("struct _cef_context_menu_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_context_menu_handler_t*> get_context_menu_handler;

    [NativeTypeName("struct _cef_dialog_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_dialog_handler_t*> get_dialog_handler;

    [NativeTypeName("struct _cef_display_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_display_handler_t*> get_display_handler;

    [NativeTypeName("struct _cef_download_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_download_handler_t*> get_download_handler;

    [NativeTypeName("struct _cef_drag_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_drag_handler_t*> get_drag_handler;

    [NativeTypeName("struct _cef_find_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_find_handler_t*> get_find_handler;

    [NativeTypeName("struct _cef_focus_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_focus_handler_t*> get_focus_handler;

    [NativeTypeName("struct _cef_frame_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_frame_handler_t*> get_frame_handler;

    [NativeTypeName("struct _cef_permission_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_permission_handler_t*> get_permission_handler;

    [NativeTypeName("struct _cef_jsdialog_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_jsdialog_handler_t*> get_jsdialog_handler;

    [NativeTypeName("struct _cef_keyboard_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_keyboard_handler_t*> get_keyboard_handler;

    [NativeTypeName("struct _cef_life_span_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_life_span_handler_t*> get_life_span_handler;

    [NativeTypeName("struct _cef_load_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_load_handler_t*> get_load_handler;

    [NativeTypeName("struct _cef_print_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_print_handler_t*> get_print_handler;

    [NativeTypeName("struct _cef_render_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_render_handler_t*> get_render_handler;

    [NativeTypeName("struct _cef_request_handler_t *(*)(struct _cef_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_request_handler_t*> get_request_handler;

    [NativeTypeName("int (*)(struct _cef_client_t *, struct _cef_browser_t *, struct _cef_frame_t *, cef_process_id_t, struct _cef_process_message_t *)")]
    public delegate* unmanaged[Cdecl]<cef_client_t*, cef_browser_t*, cef_frame_t*, cef_process_id_t, cef_process_message_t*, int> on_process_message_received;
}
