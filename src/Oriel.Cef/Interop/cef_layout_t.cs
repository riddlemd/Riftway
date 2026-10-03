namespace Oriel.Cef.Interop;

public unsafe partial struct cef_layout_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_box_layout_t *(*)(struct _cef_layout_t *)")]
    public delegate* unmanaged[Cdecl]<cef_layout_t*, cef_box_layout_t*> as_box_layout;

    [NativeTypeName("struct _cef_fill_layout_t *(*)(struct _cef_layout_t *)")]
    public delegate* unmanaged[Cdecl]<cef_layout_t*, cef_fill_layout_t*> as_fill_layout;

    [NativeTypeName("int (*)(struct _cef_layout_t *)")]
    public delegate* unmanaged[Cdecl]<cef_layout_t*, int> is_valid;
}
