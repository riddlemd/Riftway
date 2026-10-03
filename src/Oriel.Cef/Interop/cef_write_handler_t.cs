namespace Oriel.Cef.Interop;

public unsafe partial struct cef_write_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("size_t (*)(struct _cef_write_handler_t *, const void *, size_t, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_write_handler_t*, void*, nuint, nuint, nuint> write;

    [NativeTypeName("int (*)(struct _cef_write_handler_t *, int64_t, int)")]
    public delegate* unmanaged[Cdecl]<cef_write_handler_t*, long, int, int> seek;

    [NativeTypeName("int64_t (*)(struct _cef_write_handler_t *)")]
    public delegate* unmanaged[Cdecl]<cef_write_handler_t*, long> tell;

    [NativeTypeName("int (*)(struct _cef_write_handler_t *)")]
    public delegate* unmanaged[Cdecl]<cef_write_handler_t*, int> flush;

    [NativeTypeName("int (*)(struct _cef_write_handler_t *)")]
    public delegate* unmanaged[Cdecl]<cef_write_handler_t*, int> may_block;
}
