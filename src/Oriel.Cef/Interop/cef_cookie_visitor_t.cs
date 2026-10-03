namespace Oriel.Cef.Interop;

public unsafe partial struct cef_cookie_visitor_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_cookie_visitor_t *, const struct _cef_cookie_t *, int, int, int *)")]
    public delegate* unmanaged[Cdecl]<cef_cookie_visitor_t*, cef_cookie_t*, int, int, int*, int> visit;
}
