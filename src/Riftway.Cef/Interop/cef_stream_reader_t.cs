namespace Riftway.Cef.Interop;

public unsafe partial struct cef_stream_reader_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("size_t (*)(struct _cef_stream_reader_t *, void *, size_t, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_stream_reader_t*, void*, nuint, nuint, nuint> read;

    [NativeTypeName("int (*)(struct _cef_stream_reader_t *, int64_t, int)")]
    public delegate* unmanaged[Cdecl]<cef_stream_reader_t*, long, int, int> seek;

    [NativeTypeName("int64_t (*)(struct _cef_stream_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_stream_reader_t*, long> tell;

    [NativeTypeName("int (*)(struct _cef_stream_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_stream_reader_t*, int> eof;

    [NativeTypeName("int (*)(struct _cef_stream_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_stream_reader_t*, int> may_block;
}
