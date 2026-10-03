namespace Oriel.Cef.Interop;

public unsafe partial struct cef_zip_reader_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, int> move_to_first_file;

    [NativeTypeName("int (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, int> move_to_next_file;

    [NativeTypeName("int (*)(struct _cef_zip_reader_t *, const cef_string_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, cef_string_utf16_t*, int, int> move_to_file;

    [NativeTypeName("int (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, int> close;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, cef_string_utf16_t*> get_file_name;

    [NativeTypeName("int64_t (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, long> get_file_size;

    [NativeTypeName("cef_basetime_t (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, cef_basetime_t> get_file_last_modified;

    [NativeTypeName("int (*)(struct _cef_zip_reader_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, cef_string_utf16_t*, int> open_file;

    [NativeTypeName("int (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, int> close_file;

    [NativeTypeName("int (*)(struct _cef_zip_reader_t *, void *, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, void*, nuint, int> read_file;

    [NativeTypeName("int64_t (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, long> tell;

    [NativeTypeName("int (*)(struct _cef_zip_reader_t *)")]
    public delegate* unmanaged[Cdecl]<cef_zip_reader_t*, int> eof;
}
