namespace Riftway.Cef.Interop;

public unsafe partial struct cef_v8_exception_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_v8_exception_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_exception_t*, cef_string_utf16_t*> get_message;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_v8_exception_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_exception_t*, cef_string_utf16_t*> get_source_line;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_v8_exception_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_exception_t*, cef_string_utf16_t*> get_script_resource_name;

    [NativeTypeName("int (*)(struct _cef_v8_exception_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_exception_t*, int> get_line_number;

    [NativeTypeName("int (*)(struct _cef_v8_exception_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_exception_t*, int> get_start_position;

    [NativeTypeName("int (*)(struct _cef_v8_exception_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_exception_t*, int> get_end_position;

    [NativeTypeName("int (*)(struct _cef_v8_exception_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_exception_t*, int> get_start_column;

    [NativeTypeName("int (*)(struct _cef_v8_exception_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_exception_t*, int> get_end_column;
}
