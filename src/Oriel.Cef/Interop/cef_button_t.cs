namespace Oriel.Cef.Interop;

public unsafe partial struct cef_button_t
{
    public cef_view_t @base;

    [NativeTypeName("struct _cef_label_button_t *(*)(struct _cef_button_t *)")]
    public delegate* unmanaged[Cdecl]<cef_button_t*, cef_label_button_t*> as_label_button;

    [NativeTypeName("void (*)(struct _cef_button_t *, cef_button_state_t)")]
    public delegate* unmanaged[Cdecl]<cef_button_t*, cef_button_state_t, void> set_state;

    [NativeTypeName("cef_button_state_t (*)(struct _cef_button_t *)")]
    public delegate* unmanaged[Cdecl]<cef_button_t*, cef_button_state_t> get_state;

    [NativeTypeName("void (*)(struct _cef_button_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_button_t*, int, void> set_ink_drop_enabled;

    [NativeTypeName("void (*)(struct _cef_button_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_button_t*, cef_string_utf16_t*, void> set_tooltip_text;

    [NativeTypeName("void (*)(struct _cef_button_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_button_t*, cef_string_utf16_t*, void> set_accessible_name;
}
