namespace Riftway.Cef.Interop;

public unsafe partial struct cef_delete_cookies_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_delete_cookies_callback_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_delete_cookies_callback_t*, int, void> on_complete;
}
