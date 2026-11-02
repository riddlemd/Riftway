namespace Riftway.Cef.Interop;

public unsafe partial struct cef_v8_backing_store_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void *(*)(struct _cef_v8_backing_store_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_backing_store_t*, void*> data;

    [NativeTypeName("size_t (*)(struct _cef_v8_backing_store_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_backing_store_t*, nuint> byte_length;

    [NativeTypeName("int (*)(struct _cef_v8_backing_store_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_backing_store_t*, int> is_valid;
}
