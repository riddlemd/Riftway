namespace Riftway.Cef.Interop;

public unsafe partial struct cef_v8_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_v8_handler_t *, const cef_string_t *, struct _cef_v8_value_t *, size_t, struct _cef_v8_value_t *const *, struct _cef_v8_value_t **, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_handler_t*, cef_string_utf16_t*, cef_v8_value_t*, nuint, cef_v8_value_t**, cef_v8_value_t**, cef_string_utf16_t*, int> execute;
}
