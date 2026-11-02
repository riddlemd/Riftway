namespace Riftway.Cef.Interop;

public unsafe partial struct cef_box_layout_t
{
    public cef_layout_t @base;

    [NativeTypeName("void (*)(struct _cef_box_layout_t *, struct _cef_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_box_layout_t*, cef_view_t*, int, void> set_flex_for_view;

    [NativeTypeName("void (*)(struct _cef_box_layout_t *, struct _cef_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_box_layout_t*, cef_view_t*, void> clear_flex_for_view;
}
