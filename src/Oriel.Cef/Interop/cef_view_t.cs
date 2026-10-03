namespace Oriel.Cef.Interop;

public unsafe partial struct cef_view_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_browser_view_t *(*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_browser_view_t*> as_browser_view;

    [NativeTypeName("struct _cef_button_t *(*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_button_t*> as_button;

    [NativeTypeName("struct _cef_panel_t *(*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_panel_t*> as_panel;

    [NativeTypeName("struct _cef_scroll_view_t *(*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_scroll_view_t*> as_scroll_view;

    [NativeTypeName("struct _cef_textfield_t *(*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_textfield_t*> as_textfield;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_string_utf16_t*> get_type_string;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, cef_string_utf16_t*> to_string;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> is_valid;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> is_attached;

    [NativeTypeName("int (*)(struct _cef_view_t *, struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_view_t*, int> is_same;

    [NativeTypeName("struct _cef_view_delegate_t *(*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_view_delegate_t*> get_delegate;

    [NativeTypeName("struct _cef_window_t *(*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_window_t*> get_window;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> get_id;

    [NativeTypeName("void (*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, void> set_id;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> get_group_id;

    [NativeTypeName("void (*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, void> set_group_id;

    [NativeTypeName("struct _cef_view_t *(*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_view_t*> get_parent_view;

    [NativeTypeName("struct _cef_view_t *(*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, cef_view_t*> get_view_for_id;

    [NativeTypeName("void (*)(struct _cef_view_t *, const cef_rect_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_rect_t*, void> set_bounds;

    [NativeTypeName("cef_rect_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_rect_t> get_bounds;

    [NativeTypeName("cef_rect_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_rect_t> get_bounds_in_screen;

    [NativeTypeName("void (*)(struct _cef_view_t *, const cef_size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_size_t*, void> set_size;

    [NativeTypeName("cef_size_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_size_t> get_size;

    [NativeTypeName("void (*)(struct _cef_view_t *, const cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_point_t*, void> set_position;

    [NativeTypeName("cef_point_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_point_t> get_position;

    [NativeTypeName("void (*)(struct _cef_view_t *, const cef_insets_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_insets_t*, void> set_insets;

    [NativeTypeName("cef_insets_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_insets_t> get_insets;

    [NativeTypeName("cef_size_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_size_t> get_preferred_size;

    [NativeTypeName("void (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, void> size_to_preferred_size;

    [NativeTypeName("cef_size_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_size_t> get_minimum_size;

    [NativeTypeName("cef_size_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_size_t> get_maximum_size;

    [NativeTypeName("int (*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, int> get_height_for_width;

    [NativeTypeName("void (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, void> invalidate_layout;

    [NativeTypeName("void (*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, void> set_visible;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> is_visible;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> is_drawn;

    [NativeTypeName("void (*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, void> set_enabled;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> is_enabled;

    [NativeTypeName("void (*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, void> set_focusable;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> is_focusable;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> is_accessibility_focusable;

    [NativeTypeName("int (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int> has_focus;

    [NativeTypeName("void (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, void> request_focus;

    [NativeTypeName("void (*)(struct _cef_view_t *, cef_color_t)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, uint, void> set_background_color;

    [NativeTypeName("cef_color_t (*)(struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, uint> get_background_color;

    [NativeTypeName("cef_color_t (*)(struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, int, uint> get_theme_color;

    [NativeTypeName("int (*)(struct _cef_view_t *, cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_point_t*, int> convert_point_to_screen;

    [NativeTypeName("int (*)(struct _cef_view_t *, cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_point_t*, int> convert_point_from_screen;

    [NativeTypeName("int (*)(struct _cef_view_t *, cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_point_t*, int> convert_point_to_window;

    [NativeTypeName("int (*)(struct _cef_view_t *, cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_point_t*, int> convert_point_from_window;

    [NativeTypeName("int (*)(struct _cef_view_t *, struct _cef_view_t *, cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_view_t*, cef_point_t*, int> convert_point_to_view;

    [NativeTypeName("int (*)(struct _cef_view_t *, struct _cef_view_t *, cef_point_t *)")]
    public delegate* unmanaged[Cdecl]<cef_view_t*, cef_view_t*, cef_point_t*, int> convert_point_from_view;
}
