namespace Riftway.Cef.Interop;

public unsafe partial struct cef_string_visitor_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_string_visitor_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_string_visitor_t*, cef_string_utf16_t*, void> visit;
}
