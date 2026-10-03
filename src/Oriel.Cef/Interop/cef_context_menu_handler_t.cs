namespace Oriel.Cef.Interop;

public unsafe partial struct cef_context_menu_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_context_menu_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, struct _cef_context_menu_params_t *, struct _cef_menu_model_t *)")]
    public delegate* unmanaged[Cdecl]<cef_context_menu_handler_t*, cef_browser_t*, cef_frame_t*, cef_context_menu_params_t*, cef_menu_model_t*, void> on_before_context_menu;

    [NativeTypeName("int (*)(struct _cef_context_menu_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, struct _cef_context_menu_params_t *, struct _cef_menu_model_t *, struct _cef_run_context_menu_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_context_menu_handler_t*, cef_browser_t*, cef_frame_t*, cef_context_menu_params_t*, cef_menu_model_t*, cef_run_context_menu_callback_t*, int> run_context_menu;

    [NativeTypeName("int (*)(struct _cef_context_menu_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, struct _cef_context_menu_params_t *, int, cef_event_flags_t)")]
    public delegate* unmanaged[Cdecl]<cef_context_menu_handler_t*, cef_browser_t*, cef_frame_t*, cef_context_menu_params_t*, int, cef_event_flags_t, int> on_context_menu_command;

    [NativeTypeName("void (*)(struct _cef_context_menu_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_context_menu_handler_t*, cef_browser_t*, cef_frame_t*, void> on_context_menu_dismissed;

    [NativeTypeName("int (*)(struct _cef_context_menu_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, const cef_point_t *, const cef_size_t *, cef_quick_menu_edit_state_flags_t, struct _cef_run_quick_menu_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_context_menu_handler_t*, cef_browser_t*, cef_frame_t*, cef_point_t*, cef_size_t*, cef_quick_menu_edit_state_flags_t, cef_run_quick_menu_callback_t*, int> run_quick_menu;

    [NativeTypeName("int (*)(struct _cef_context_menu_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, int, cef_event_flags_t)")]
    public delegate* unmanaged[Cdecl]<cef_context_menu_handler_t*, cef_browser_t*, cef_frame_t*, int, cef_event_flags_t, int> on_quick_menu_command;

    [NativeTypeName("void (*)(struct _cef_context_menu_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_context_menu_handler_t*, cef_browser_t*, cef_frame_t*, void> on_quick_menu_dismissed;
}
