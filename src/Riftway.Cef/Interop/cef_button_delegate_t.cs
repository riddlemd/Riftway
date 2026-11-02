namespace Riftway.Cef.Interop;

public unsafe partial struct cef_button_delegate_t
{
    public cef_view_delegate_t @base;

    [NativeTypeName("void (*)(struct _cef_button_delegate_t *, struct _cef_button_t *)")]
    public delegate* unmanaged[Cdecl]<cef_button_delegate_t*, cef_button_t*, void> on_button_pressed;

    [NativeTypeName("void (*)(struct _cef_button_delegate_t *, struct _cef_button_t *)")]
    public delegate* unmanaged[Cdecl]<cef_button_delegate_t*, cef_button_t*, void> on_button_state_changed;
}
