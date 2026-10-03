namespace Oriel.Cef.Interop;

public unsafe partial struct cef_component_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_component_t *)")]
    public delegate* unmanaged[Cdecl]<cef_component_t*, cef_string_utf16_t*> get_id;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_component_t *)")]
    public delegate* unmanaged[Cdecl]<cef_component_t*, cef_string_utf16_t*> get_name;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_component_t *)")]
    public delegate* unmanaged[Cdecl]<cef_component_t*, cef_string_utf16_t*> get_version;

    [NativeTypeName("cef_component_state_t (*)(struct _cef_component_t *)")]
    public delegate* unmanaged[Cdecl]<cef_component_t*, cef_component_state_t> get_state;
}
