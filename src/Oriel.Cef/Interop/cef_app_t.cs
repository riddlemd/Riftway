namespace Oriel.Cef.Interop;

public unsafe partial struct cef_app_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_app_t *, const cef_string_t *, struct _cef_command_line_t *)")]
    public delegate* unmanaged[Cdecl]<cef_app_t*, cef_string_utf16_t*, cef_command_line_t*, void> on_before_command_line_processing;

    [NativeTypeName("void (*)(struct _cef_app_t *, struct _cef_scheme_registrar_t *)")]
    public delegate* unmanaged[Cdecl]<cef_app_t*, cef_scheme_registrar_t*, void> on_register_custom_schemes;

    [NativeTypeName("struct _cef_resource_bundle_handler_t *(*)(struct _cef_app_t *)")]
    public delegate* unmanaged[Cdecl]<cef_app_t*, cef_resource_bundle_handler_t*> get_resource_bundle_handler;

    [NativeTypeName("struct _cef_browser_process_handler_t *(*)(struct _cef_app_t *)")]
    public delegate* unmanaged[Cdecl]<cef_app_t*, cef_browser_process_handler_t*> get_browser_process_handler;

    [NativeTypeName("struct _cef_render_process_handler_t *(*)(struct _cef_app_t *)")]
    public delegate* unmanaged[Cdecl]<cef_app_t*, cef_render_process_handler_t*> get_render_process_handler;
}
