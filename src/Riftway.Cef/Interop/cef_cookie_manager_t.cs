namespace Riftway.Cef.Interop;

public unsafe partial struct cef_cookie_manager_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_cookie_manager_t *, struct _cef_cookie_visitor_t *)")]
    public delegate* unmanaged[Cdecl]<cef_cookie_manager_t*, cef_cookie_visitor_t*, int> visit_all_cookies;

    [NativeTypeName("int (*)(struct _cef_cookie_manager_t *, const cef_string_t *, int, struct _cef_cookie_visitor_t *)")]
    public delegate* unmanaged[Cdecl]<cef_cookie_manager_t*, cef_string_utf16_t*, int, cef_cookie_visitor_t*, int> visit_url_cookies;

    [NativeTypeName("int (*)(struct _cef_cookie_manager_t *, const cef_string_t *, const struct _cef_cookie_t *, struct _cef_set_cookie_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_cookie_manager_t*, cef_string_utf16_t*, cef_cookie_t*, cef_set_cookie_callback_t*, int> set_cookie;

    [NativeTypeName("int (*)(struct _cef_cookie_manager_t *, const cef_string_t *, const cef_string_t *, struct _cef_delete_cookies_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_cookie_manager_t*, cef_string_utf16_t*, cef_string_utf16_t*, cef_delete_cookies_callback_t*, int> delete_cookies;

    [NativeTypeName("int (*)(struct _cef_cookie_manager_t *, struct _cef_completion_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_cookie_manager_t*, cef_completion_callback_t*, int> flush_store;
}
