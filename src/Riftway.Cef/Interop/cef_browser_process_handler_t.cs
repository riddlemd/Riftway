namespace Riftway.Cef.Interop;

public unsafe partial struct cef_browser_process_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_browser_process_handler_t *, cef_preferences_type_t, struct _cef_preference_registrar_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_process_handler_t*, cef_preferences_type_t, cef_preference_registrar_t*, void> on_register_custom_preferences;

    [NativeTypeName("void (*)(struct _cef_browser_process_handler_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_process_handler_t*, void> on_context_initialized;

    [NativeTypeName("void (*)(struct _cef_browser_process_handler_t *, struct _cef_command_line_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_process_handler_t*, cef_command_line_t*, void> on_before_child_process_launch;

    [NativeTypeName("int (*)(struct _cef_browser_process_handler_t *, struct _cef_command_line_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_process_handler_t*, cef_command_line_t*, cef_string_utf16_t*, int> on_already_running_app_relaunch;

    [NativeTypeName("void (*)(struct _cef_browser_process_handler_t *, int64_t)")]
    public delegate* unmanaged[Cdecl]<cef_browser_process_handler_t*, long, void> on_schedule_message_pump_work;

    [NativeTypeName("struct _cef_client_t *(*)(struct _cef_browser_process_handler_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_process_handler_t*, cef_client_t*> get_default_client;

    [NativeTypeName("struct _cef_request_context_handler_t *(*)(struct _cef_browser_process_handler_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_process_handler_t*, cef_request_context_handler_t*> get_default_request_context_handler;
}
