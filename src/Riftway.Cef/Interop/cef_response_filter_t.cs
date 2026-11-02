namespace Riftway.Cef.Interop;

public unsafe partial struct cef_response_filter_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_response_filter_t *)")]
    public delegate* unmanaged[Cdecl]<cef_response_filter_t*, int> init_filter;

    [NativeTypeName("cef_response_filter_status_t (*)(struct _cef_response_filter_t *, void *, size_t, size_t *, void *, size_t, size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_response_filter_t*, void*, nuint, nuint*, void*, nuint, nuint*, cef_response_filter_status_t> filter;
}
