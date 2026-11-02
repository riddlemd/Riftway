namespace Riftway.Cef.Interop;

public unsafe partial struct cef_menu_button_t
{
    public cef_label_button_t @base;

    [NativeTypeName("void (*)(struct _cef_menu_button_t *, struct _cef_menu_model_t *, const cef_point_t *, cef_menu_anchor_position_t)")]
    public delegate* unmanaged[Cdecl]<cef_menu_button_t*, cef_menu_model_t*, cef_point_t*, cef_menu_anchor_position_t, void> show_menu;

    [NativeTypeName("void (*)(struct _cef_menu_button_t *)")]
    public delegate* unmanaged[Cdecl]<cef_menu_button_t*, void> trigger_menu;
}
