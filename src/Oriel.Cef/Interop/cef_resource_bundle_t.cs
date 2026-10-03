namespace Oriel.Cef.Interop;

public unsafe partial struct cef_resource_bundle_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_resource_bundle_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_resource_bundle_t*, int, cef_string_utf16_t*> get_localized_string;

    [NativeTypeName("struct _cef_binary_value_t *(*)(struct _cef_resource_bundle_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_resource_bundle_t*, int, cef_binary_value_t*> get_data_resource;

    [NativeTypeName("struct _cef_binary_value_t *(*)(struct _cef_resource_bundle_t *, int, cef_scale_factor_t)")]
    public delegate* unmanaged[Cdecl]<cef_resource_bundle_t*, int, cef_scale_factor_t, cef_binary_value_t*> get_data_resource_for_scale;
}
