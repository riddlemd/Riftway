namespace Riftway.Cef.Interop;

public unsafe partial struct cef_browser_view_delegate_t
{
    public cef_view_delegate_t @base;

    [NativeTypeName("void (*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, cef_browser_t*, void> on_browser_created;

    [NativeTypeName("void (*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, cef_browser_t*, void> on_browser_destroyed;

    [NativeTypeName("struct _cef_browser_view_delegate_t *(*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *, const struct _cef_browser_settings_t *, struct _cef_client_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, cef_browser_settings_t*, cef_client_t*, int, cef_browser_view_delegate_t*> get_delegate_for_popup_browser_view;

    [NativeTypeName("int (*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *, struct _cef_browser_view_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, cef_browser_view_t*, int, int> on_popup_browser_view_created;

    [NativeTypeName("cef_chrome_toolbar_type_t (*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, cef_chrome_toolbar_type_t> get_chrome_toolbar_type;

    [NativeTypeName("int (*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, int> use_frameless_window_for_picture_in_picture;

    [NativeTypeName("int (*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *, cef_gesture_command_t)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, cef_gesture_command_t, int> on_gesture_command;

    [NativeTypeName("cef_runtime_style_t (*)(struct _cef_browser_view_delegate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_runtime_style_t> get_browser_runtime_style;

    [NativeTypeName("int (*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, int> allow_move_for_picture_in_picture;

    [NativeTypeName("int (*)(struct _cef_browser_view_delegate_t *, struct _cef_browser_view_t *)")]
    public delegate* unmanaged[Cdecl]<cef_browser_view_delegate_t*, cef_browser_view_t*, int> allow_picture_in_picture_without_user_activation;
}
