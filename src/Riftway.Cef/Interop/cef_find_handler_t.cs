namespace Riftway.Cef.Interop;

public unsafe partial struct cef_find_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_find_handler_t *, struct _cef_browser_t *, int, int, const cef_rect_t *, int, int)")]
    public delegate* unmanaged[Cdecl]<cef_find_handler_t*, cef_browser_t*, int, int, cef_rect_t*, int, int, void> on_find_result;
}
