namespace Riftway.Cef.Interop;

public unsafe partial struct cef_menu_button_delegate_t
{
    public cef_button_delegate_t @base;

    [NativeTypeName("void (*)(struct _cef_menu_button_delegate_t *, struct _cef_menu_button_t *, const cef_point_t *, struct _cef_menu_button_pressed_lock_t *)")]
    public delegate* unmanaged[Cdecl]<cef_menu_button_delegate_t*, cef_menu_button_t*, cef_point_t*, cef_menu_button_pressed_lock_t*, void> on_menu_button_pressed;
}
