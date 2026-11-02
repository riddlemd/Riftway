namespace Riftway.Cef.Interop;

public unsafe partial struct cef_window_t
{
    public cef_panel_t @base;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> show;

    [NativeTypeName("void (*)(struct _cef_window_t *, struct _cef_browser_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_browser_view_t*, void> show_as_browser_modal_dialog;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> hide;

    [NativeTypeName("void (*)(struct _cef_window_t *, const cef_size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_size_t*, void> center_window;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> close;

    [NativeTypeName("int (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int> is_closed;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> activate;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> deactivate;

    [NativeTypeName("int (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int> is_active;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> bring_to_top;

    [NativeTypeName("void (*)(struct _cef_window_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int, void> set_always_on_top;

    [NativeTypeName("int (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int> is_always_on_top;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> maximize;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> minimize;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> restore;

    [NativeTypeName("void (*)(struct _cef_window_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int, void> set_fullscreen;

    [NativeTypeName("int (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int> is_maximized;

    [NativeTypeName("int (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int> is_minimized;

    [NativeTypeName("int (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int> is_fullscreen;

    [NativeTypeName("struct _cef_view_t *(*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_view_t*> get_focused_view;

    [NativeTypeName("void (*)(struct _cef_window_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_string_utf16_t*, void> set_title;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_string_utf16_t*> get_title;

    [NativeTypeName("void (*)(struct _cef_window_t *, struct _cef_image_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_image_t*, void> set_window_icon;

    [NativeTypeName("struct _cef_image_t *(*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_image_t*> get_window_icon;

    [NativeTypeName("void (*)(struct _cef_window_t *, struct _cef_image_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_image_t*, void> set_window_app_icon;

    [NativeTypeName("struct _cef_image_t *(*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_image_t*> get_window_app_icon;

    [NativeTypeName("struct _cef_overlay_controller_t *(*)(struct _cef_window_t *, struct _cef_view_t *, cef_docking_mode_t, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_view_t*, cef_docking_mode_t, int, cef_overlay_controller_t*> add_overlay_view;

    [NativeTypeName("void (*)(struct _cef_window_t *, struct _cef_menu_model_t *, const cef_point_t *, cef_menu_anchor_position_t)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_menu_model_t*, cef_point_t*, cef_menu_anchor_position_t, void> show_menu;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> cancel_menu;

    [NativeTypeName("struct _cef_display_t *(*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_display_t*> get_display;

    [NativeTypeName("cef_rect_t (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_rect_t> get_client_area_bounds_in_screen;

    [NativeTypeName("void (*)(struct _cef_window_t *, size_t, const cef_draggable_region_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, nuint, cef_draggable_region_t*, void> set_draggable_regions;

    [NativeTypeName("cef_window_handle_t (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void*> get_window_handle;

    [NativeTypeName("void (*)(struct _cef_window_t *, int, uint32_t)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int, uint, void> send_key_press;

    [NativeTypeName("void (*)(struct _cef_window_t *, int, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int, int, void> send_mouse_move;

    [NativeTypeName("void (*)(struct _cef_window_t *, cef_mouse_button_type_t, int, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_mouse_button_type_t, int, int, void> send_mouse_events;

    [NativeTypeName("void (*)(struct _cef_window_t *, int, int, int, int, int, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int, int, int, int, int, int, void> set_accelerator;

    [NativeTypeName("void (*)(struct _cef_window_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int, void> remove_accelerator;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> remove_all_accelerators;

    [NativeTypeName("void (*)(struct _cef_window_t *, int, cef_color_t)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, int, uint, void> set_theme_color;

    [NativeTypeName("void (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, void> theme_changed;

    [NativeTypeName("cef_runtime_style_t (*)(struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_t*, cef_runtime_style_t> get_runtime_style;
}
