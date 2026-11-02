namespace Riftway.Cef.Interop;

public unsafe partial struct cef_shared_memory_region_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_shared_memory_region_t *)")]
    public delegate* unmanaged[Cdecl]<cef_shared_memory_region_t*, int> is_valid;

    [NativeTypeName("size_t (*)(struct _cef_shared_memory_region_t *)")]
    public delegate* unmanaged[Cdecl]<cef_shared_memory_region_t*, nuint> size;

    [NativeTypeName("void *(*)(struct _cef_shared_memory_region_t *)")]
    public delegate* unmanaged[Cdecl]<cef_shared_memory_region_t*, void*> memory;
}
