namespace Oriel.Cef.Interop;

public unsafe partial struct cef_window_delegate_t
{
    public cef_panel_delegate_t @base;

    [NativeTypeName("void (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, void> on_window_created;

    [NativeTypeName("void (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, void> on_window_closing;

    [NativeTypeName("void (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, void> on_window_destroyed;

    [NativeTypeName("void (*)(struct _cef_window_delegate_t *, struct _cef_window_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int, void> on_window_activation_changed;

    [NativeTypeName("void (*)(struct _cef_window_delegate_t *, struct _cef_window_t *, const cef_rect_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, cef_rect_t*, void> on_window_bounds_changed;

    [NativeTypeName("void (*)(struct _cef_window_delegate_t *, struct _cef_window_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int, void> on_window_fullscreen_transition;

    [NativeTypeName("struct _cef_window_t *(*)(struct _cef_window_delegate_t *, struct _cef_window_t *, int *, int *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int*, int*, cef_window_t*> get_parent_window;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> is_window_modal_dialog;

    [NativeTypeName("cef_rect_t (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, cef_rect_t> get_initial_bounds;

    [NativeTypeName("cef_show_state_t (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, cef_show_state_t> get_initial_show_state;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> is_frameless;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> with_standard_window_buttons;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *, float *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, float*, int> get_titlebar_height;

    [NativeTypeName("cef_state_t (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, cef_state_t> accepts_first_mouse;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> can_resize;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> can_maximize;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> can_minimize;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int> can_close;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int, int> on_accelerator;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *, const cef_key_event_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, cef_key_event_t*, int> on_key_event;

    [NativeTypeName("void (*)(struct _cef_window_delegate_t *, struct _cef_window_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, int, void> on_theme_colors_changed;

    [NativeTypeName("cef_runtime_style_t (*)(struct _cef_window_delegate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_runtime_style_t> get_window_runtime_style;

    [NativeTypeName("int (*)(struct _cef_window_delegate_t *, struct _cef_window_t *, struct _cef_linux_window_properties_t *)")]
    public delegate* unmanaged[Cdecl]<cef_window_delegate_t*, cef_window_t*, cef_linux_window_properties_t*, int> get_linux_window_properties;
}
