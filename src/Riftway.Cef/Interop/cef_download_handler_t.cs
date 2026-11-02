namespace Riftway.Cef.Interop;

public unsafe partial struct cef_download_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_download_handler_t *, struct _cef_browser_t *, const cef_string_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_download_handler_t*, cef_browser_t*, cef_string_utf16_t*, cef_string_utf16_t*, int> can_download;

    [NativeTypeName("int (*)(struct _cef_download_handler_t *, struct _cef_browser_t *, struct _cef_download_item_t *, const cef_string_t *, struct _cef_before_download_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_download_handler_t*, cef_browser_t*, cef_download_item_t*, cef_string_utf16_t*, cef_before_download_callback_t*, int> on_before_download;

    [NativeTypeName("void (*)(struct _cef_download_handler_t *, struct _cef_browser_t *, struct _cef_download_item_t *, struct _cef_download_item_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_download_handler_t*, cef_browser_t*, cef_download_item_t*, cef_download_item_callback_t*, void> on_download_updated;
}
