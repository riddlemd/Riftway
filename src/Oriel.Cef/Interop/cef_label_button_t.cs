namespace Oriel.Cef.Interop;

public unsafe partial struct cef_label_button_t
{
    public cef_button_t @base;

    [NativeTypeName("struct _cef_menu_button_t *(*)(struct _cef_label_button_t *)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_menu_button_t*> as_menu_button;

    [NativeTypeName("void (*)(struct _cef_label_button_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_string_utf16_t*, void> set_text;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_label_button_t *)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_string_utf16_t*> get_text;

    [NativeTypeName("void (*)(struct _cef_label_button_t *, cef_button_state_t, struct _cef_image_t *)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_button_state_t, cef_image_t*, void> set_image;

    [NativeTypeName("struct _cef_image_t *(*)(struct _cef_label_button_t *, cef_button_state_t)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_button_state_t, cef_image_t*> get_image;

    [NativeTypeName("void (*)(struct _cef_label_button_t *, cef_button_state_t, cef_color_t)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_button_state_t, uint, void> set_text_color;

    [NativeTypeName("void (*)(struct _cef_label_button_t *, cef_color_t)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, uint, void> set_enabled_text_colors;

    [NativeTypeName("void (*)(struct _cef_label_button_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_string_utf16_t*, void> set_font_list;

    [NativeTypeName("void (*)(struct _cef_label_button_t *, cef_horizontal_alignment_t)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_horizontal_alignment_t, void> set_horizontal_alignment;

    [NativeTypeName("void (*)(struct _cef_label_button_t *, const cef_size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_size_t*, void> set_minimum_size;

    [NativeTypeName("void (*)(struct _cef_label_button_t *, const cef_size_t *)")]
    public delegate* unmanaged[Cdecl]<cef_label_button_t*, cef_size_t*, void> set_maximum_size;
}
