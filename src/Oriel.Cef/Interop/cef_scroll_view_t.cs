namespace Oriel.Cef.Interop;

public unsafe partial struct cef_scroll_view_t
{
    public cef_view_t @base;

    [NativeTypeName("void (*)(struct _cef_scroll_view_t *, struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_scroll_view_t*, cef_view_t*, void> set_content_view;

    [NativeTypeName("struct _cef_view_t *(*)(struct _cef_scroll_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_scroll_view_t*, cef_view_t*> get_content_view;

    [NativeTypeName("cef_rect_t (*)(struct _cef_scroll_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_scroll_view_t*, cef_rect_t> get_visible_content_rect;

    [NativeTypeName("int (*)(struct _cef_scroll_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_scroll_view_t*, int> has_horizontal_scrollbar;

    [NativeTypeName("int (*)(struct _cef_scroll_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_scroll_view_t*, int> get_horizontal_scrollbar_height;

    [NativeTypeName("int (*)(struct _cef_scroll_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_scroll_view_t*, int> has_vertical_scrollbar;

    [NativeTypeName("int (*)(struct _cef_scroll_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_scroll_view_t*, int> get_vertical_scrollbar_width;
}
