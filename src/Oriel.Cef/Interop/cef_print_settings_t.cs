namespace Oriel.Cef.Interop;

public unsafe partial struct cef_print_settings_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int> is_valid;

    [NativeTypeName("int (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int> is_read_only;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int, void> set_orientation;

    [NativeTypeName("int (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int> is_landscape;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, const cef_size_t *, const cef_rect_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, cef_size_t*, cef_rect_t*, int, void> set_printer_printable_area;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, cef_string_utf16_t*, void> set_device_name;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, cef_string_utf16_t*> get_device_name;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int, void> set_dpi;

    [NativeTypeName("int (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int> get_dpi;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, size_t, const cef_range_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, nuint, cef_range_t*, void> set_page_ranges;

    [NativeTypeName("size_t (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, nuint> get_page_ranges_count;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, size_t *, cef_range_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, nuint*, cef_range_t*, void> get_page_ranges;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int, void> set_selection_only;

    [NativeTypeName("int (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int> is_selection_only;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int, void> set_collate;

    [NativeTypeName("int (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int> will_collate;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, cef_color_model_t)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, cef_color_model_t, void> set_color_model;

    [NativeTypeName("cef_color_model_t (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, cef_color_model_t> get_color_model;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int, void> set_copies;

    [NativeTypeName("int (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, int> get_copies;

    [NativeTypeName("void (*)(struct _cef_print_settings_t *, cef_duplex_mode_t)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, cef_duplex_mode_t, void> set_duplex_mode;

    [NativeTypeName("cef_duplex_mode_t (*)(struct _cef_print_settings_t *)")]
    public delegate* unmanaged[Cdecl]<cef_print_settings_t*, cef_duplex_mode_t> get_duplex_mode;
}
