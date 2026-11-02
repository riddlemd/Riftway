namespace Riftway.Cef.Interop;

public unsafe partial struct cef_textfield_delegate_t
{
    public cef_view_delegate_t @base;

    [NativeTypeName("int (*)(struct _cef_textfield_delegate_t *, struct _cef_textfield_t *, const cef_key_event_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_delegate_t*, cef_textfield_t*, cef_key_event_t*, int> on_key_event;

    [NativeTypeName("void (*)(struct _cef_textfield_delegate_t *, struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_delegate_t*, cef_textfield_t*, void> on_after_user_action;
}
