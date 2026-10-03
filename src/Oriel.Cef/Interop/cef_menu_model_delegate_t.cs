namespace Oriel.Cef.Interop;

public unsafe partial struct cef_menu_model_delegate_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_menu_model_delegate_t *, struct _cef_menu_model_t *, int, cef_event_flags_t)")]
    public delegate* unmanaged[Cdecl]<cef_menu_model_delegate_t*, cef_menu_model_t*, int, cef_event_flags_t, void> execute_command;

    [NativeTypeName("void (*)(struct _cef_menu_model_delegate_t *, struct _cef_menu_model_t *, const cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_menu_model_delegate_t*, cef_menu_model_t*, cef_point_t*, void> mouse_outside_menu;

    [NativeTypeName("void (*)(struct _cef_menu_model_delegate_t *, struct _cef_menu_model_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_menu_model_delegate_t*, cef_menu_model_t*, int, void> unhandled_open_submenu;

    [NativeTypeName("void (*)(struct _cef_menu_model_delegate_t *, struct _cef_menu_model_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_menu_model_delegate_t*, cef_menu_model_t*, int, void> unhandled_close_submenu;

    [NativeTypeName("void (*)(struct _cef_menu_model_delegate_t *, struct _cef_menu_model_t *)")]
    public delegate* unmanaged[Cdecl]<cef_menu_model_delegate_t*, cef_menu_model_t*, void> menu_will_show;

    [NativeTypeName("void (*)(struct _cef_menu_model_delegate_t *, struct _cef_menu_model_t *)")]
    public delegate* unmanaged[Cdecl]<cef_menu_model_delegate_t*, cef_menu_model_t*, void> menu_closed;

    [NativeTypeName("int (*)(struct _cef_menu_model_delegate_t *, struct _cef_menu_model_t *, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_menu_model_delegate_t*, cef_menu_model_t*, cef_string_utf16_t*, int> format_label;
}
