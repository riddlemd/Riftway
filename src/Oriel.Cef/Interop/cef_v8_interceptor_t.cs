namespace Oriel.Cef.Interop;

public unsafe partial struct cef_v8_interceptor_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_v8_interceptor_t *, const cef_string_t *, struct _cef_v8_value_t *, struct _cef_v8_value_t **, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_interceptor_t*, cef_string_utf16_t*, cef_v8_value_t*, cef_v8_value_t**, cef_string_utf16_t*, int> get_byname;

    [NativeTypeName("int (*)(struct _cef_v8_interceptor_t *, int, struct _cef_v8_value_t *, struct _cef_v8_value_t **, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_interceptor_t*, int, cef_v8_value_t*, cef_v8_value_t**, cef_string_utf16_t*, int> get_byindex;

    [NativeTypeName("int (*)(struct _cef_v8_interceptor_t *, const cef_string_t *, struct _cef_v8_value_t *, struct _cef_v8_value_t *, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_interceptor_t*, cef_string_utf16_t*, cef_v8_value_t*, cef_v8_value_t*, cef_string_utf16_t*, int> set_byname;

    [NativeTypeName("int (*)(struct _cef_v8_interceptor_t *, int, struct _cef_v8_value_t *, struct _cef_v8_value_t *, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_interceptor_t*, int, cef_v8_value_t*, cef_v8_value_t*, cef_string_utf16_t*, int> set_byindex;
}
