namespace Oriel.Cef.Interop;

public unsafe partial struct cef_navigation_entry_visitor_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_navigation_entry_visitor_t *, struct _cef_navigation_entry_t *, int, int, int)")]
    public delegate* unmanaged[Cdecl]<cef_navigation_entry_visitor_t*, cef_navigation_entry_t*, int, int, int, int> visit;
}
