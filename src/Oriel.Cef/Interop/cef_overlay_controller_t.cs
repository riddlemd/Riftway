namespace Oriel.Cef.Interop;

public unsafe partial struct cef_overlay_controller_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, int> is_valid;

    [NativeTypeName("int (*)(struct _cef_overlay_controller_t *, struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_overlay_controller_t*, int> is_same;

    [NativeTypeName("struct _cef_view_t *(*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_view_t*> get_contents_view;

    [NativeTypeName("struct _cef_window_t *(*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_window_t*> get_window;

    [NativeTypeName("cef_docking_mode_t (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_docking_mode_t> get_docking_mode;

    [NativeTypeName("void (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, void> destroy;

    [NativeTypeName("void (*)(struct _cef_overlay_controller_t *, const cef_rect_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_rect_t*, void> set_bounds;

    [NativeTypeName("cef_rect_t (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_rect_t> get_bounds;

    [NativeTypeName("cef_rect_t (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_rect_t> get_bounds_in_screen;

    [NativeTypeName("void (*)(struct _cef_overlay_controller_t *, const cef_size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_size_t*, void> set_size;

    [NativeTypeName("cef_size_t (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_size_t> get_size;

    [NativeTypeName("void (*)(struct _cef_overlay_controller_t *, const cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_point_t*, void> set_position;

    [NativeTypeName("cef_point_t (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_point_t> get_position;

    [NativeTypeName("void (*)(struct _cef_overlay_controller_t *, const cef_insets_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_insets_t*, void> set_insets;

    [NativeTypeName("cef_insets_t (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, cef_insets_t> get_insets;

    [NativeTypeName("void (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, void> size_to_preferred_size;

    [NativeTypeName("void (*)(struct _cef_overlay_controller_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, int, void> set_visible;

    [NativeTypeName("int (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, int> is_visible;

    [NativeTypeName("int (*)(struct _cef_overlay_controller_t *)")]
    public delegate* unmanaged[Cdecl]<cef_overlay_controller_t*, int> is_drawn;
}
