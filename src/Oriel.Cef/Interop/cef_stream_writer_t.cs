namespace Oriel.Cef.Interop;

public unsafe partial struct cef_stream_writer_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("size_t (*)(struct _cef_stream_writer_t *, const void *, size_t, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_stream_writer_t*, void*, nuint, nuint, nuint> write;

    [NativeTypeName("int (*)(struct _cef_stream_writer_t *, int64_t, int)")]
    public delegate* unmanaged[Cdecl]<cef_stream_writer_t*, long, int, int> seek;

    [NativeTypeName("int64_t (*)(struct _cef_stream_writer_t *)")]
    public delegate* unmanaged[Cdecl]<cef_stream_writer_t*, long> tell;

    [NativeTypeName("int (*)(struct _cef_stream_writer_t *)")]
    public delegate* unmanaged[Cdecl]<cef_stream_writer_t*, int> flush;

    [NativeTypeName("int (*)(struct _cef_stream_writer_t *)")]
    public delegate* unmanaged[Cdecl]<cef_stream_writer_t*, int> may_block;
}
