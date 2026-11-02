namespace Riftway.Cef.Interop;

public unsafe partial struct cef_post_data_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_post_data_t *)")]
    public delegate* unmanaged[Cdecl]<cef_post_data_t*, int> is_read_only;

    [NativeTypeName("int (*)(struct _cef_post_data_t *)")]
    public delegate* unmanaged[Cdecl]<cef_post_data_t*, int> has_excluded_elements;

    [NativeTypeName("size_t (*)(struct _cef_post_data_t *)")]
    public delegate* unmanaged[Cdecl]<cef_post_data_t*, nuint> get_element_count;

    [NativeTypeName("void (*)(struct _cef_post_data_t *, size_t *, struct _cef_post_data_element_t **)")]
    public delegate* unmanaged[Cdecl]<cef_post_data_t*, nuint*, cef_post_data_element_t**, void> get_elements;

    [NativeTypeName("int (*)(struct _cef_post_data_t *, struct _cef_post_data_element_t *)")]
    public delegate* unmanaged[Cdecl]<cef_post_data_t*, cef_post_data_element_t*, int> remove_element;

    [NativeTypeName("int (*)(struct _cef_post_data_t *, struct _cef_post_data_element_t *)")]
    public delegate* unmanaged[Cdecl]<cef_post_data_t*, cef_post_data_element_t*, int> add_element;

    [NativeTypeName("void (*)(struct _cef_post_data_t *)")]
    public delegate* unmanaged[Cdecl]<cef_post_data_t*, void> remove_elements;
}
