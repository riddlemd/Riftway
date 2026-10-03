namespace Oriel.Cef.Interop;

public unsafe partial struct cef_textfield_t
{
    public cef_view_t @base;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, int, void> set_password_input;

    [NativeTypeName("int (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, int> is_password_input;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, int, void> set_read_only;

    [NativeTypeName("int (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, int> is_read_only;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*> get_text;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*, void> set_text;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*, void> append_text;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*, void> insert_or_replace_text;

    [NativeTypeName("int (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, int> has_selection;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*> get_selected_text;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, int, void> select_all;

    [NativeTypeName("void (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, void> clear_selection;

    [NativeTypeName("cef_range_t (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_range_t> get_selected_range;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, const cef_range_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_range_t*, void> select_range;

    [NativeTypeName("size_t (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, nuint> get_cursor_position;

    [NativeTypeName("uintptr_t")]
    public nuint set_text_color_removed;

    [NativeTypeName("uintptr_t")]
    public nuint get_text_color_removed;

    [NativeTypeName("uintptr_t")]
    public nuint set_selection_text_color_removed;

    [NativeTypeName("uintptr_t")]
    public nuint get_selection_text_color_removed;

    [NativeTypeName("uintptr_t")]
    public nuint set_selection_background_color_removed;

    [NativeTypeName("uintptr_t")]
    public nuint get_selection_background_color_removed;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*, void> set_font_list;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, cef_color_t, const cef_range_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, uint, cef_range_t*, void> apply_text_color;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, cef_text_style_t, int, const cef_range_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_text_style_t, int, cef_range_t*, void> apply_text_style;

    [NativeTypeName("int (*)(struct _cef_textfield_t *, cef_text_field_commands_t)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_text_field_commands_t, int> is_command_enabled;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, cef_text_field_commands_t)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_text_field_commands_t, void> execute_command;

    [NativeTypeName("void (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, void> clear_edit_history;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*, void> set_placeholder_text;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_textfield_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*> get_placeholder_text;

    [NativeTypeName("uintptr_t")]
    public nuint set_placeholder_text_color_removed;

    [NativeTypeName("void (*)(struct _cef_textfield_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_textfield_t*, cef_string_utf16_t*, void> set_accessible_name;
}
