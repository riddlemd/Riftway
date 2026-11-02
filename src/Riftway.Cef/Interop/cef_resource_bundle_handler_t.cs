namespace Riftway.Cef.Interop;

public unsafe partial struct cef_resource_bundle_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_resource_bundle_handler_t *, int, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_bundle_handler_t*, int, cef_string_utf16_t*, int> get_localized_string;

    [NativeTypeName("int (*)(struct _cef_resource_bundle_handler_t *, int, void **, size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_bundle_handler_t*, int, void**, nuint*, int> get_data_resource;

    [NativeTypeName("int (*)(struct _cef_resource_bundle_handler_t *, int, cef_scale_factor_t, void **, size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_resource_bundle_handler_t*, int, cef_scale_factor_t, void**, nuint*, int> get_data_resource_for_scale;
}
