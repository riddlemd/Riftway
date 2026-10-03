namespace Oriel.Cef.Interop;

public unsafe partial struct cef_browser_view_t
{
    public cef_view_t @base;

    [NativeTypeName("struct _cef_browser_t *(*)(struct _cef_browser_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_t*, cef_browser_t*> get_browser;

    [NativeTypeName("struct _cef_view_t *(*)(struct _cef_browser_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_t*, cef_view_t*> get_chrome_toolbar;

    [NativeTypeName("void (*)(struct _cef_browser_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_t*, int, void> set_prefer_accelerators;

    [NativeTypeName("cef_runtime_style_t (*)(struct _cef_browser_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_t*, cef_runtime_style_t> get_runtime_style;
}
