namespace Riftway.Cef.Interop;

public unsafe partial struct cef_cookie_access_filter_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_cookie_access_filter_t *, struct _cef_browser_t *, struct _cef_frame_t *, struct _cef_request_t *, const struct _cef_cookie_t *)")]
    public delegate* unmanaged[Cdecl]<cef_cookie_access_filter_t*, cef_browser_t*, cef_frame_t*, cef_request_t*, cef_cookie_t*, int> can_send_cookie;

    [NativeTypeName("int (*)(struct _cef_cookie_access_filter_t *, struct _cef_browser_t *, struct _cef_frame_t *, struct _cef_request_t *, struct _cef_response_t *, const struct _cef_cookie_t *)")]
    public delegate* unmanaged[Cdecl]<cef_cookie_access_filter_t*, cef_browser_t*, cef_frame_t*, cef_request_t*, cef_response_t*, cef_cookie_t*, int> can_save_cookie;
}
