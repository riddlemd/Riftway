namespace Riftway.Cef.Interop;

public partial struct cef_linux_window_properties_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t wayland_app_id;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t wm_class_class;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t wm_class_name;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t wm_role_name;
}
