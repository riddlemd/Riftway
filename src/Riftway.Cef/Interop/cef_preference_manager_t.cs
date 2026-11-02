namespace Riftway.Cef.Interop;

public unsafe partial struct cef_preference_manager_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_preference_manager_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_preference_manager_t*, cef_string_utf16_t*, int> has_preference;

    [NativeTypeName("struct _cef_value_t *(*)(struct _cef_preference_manager_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_preference_manager_t*, cef_string_utf16_t*, cef_value_t*> get_preference;

    [NativeTypeName("struct _cef_dictionary_value_t *(*)(struct _cef_preference_manager_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_preference_manager_t*, int, cef_dictionary_value_t*> get_all_preferences;

    [NativeTypeName("int (*)(struct _cef_preference_manager_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_preference_manager_t*, cef_string_utf16_t*, int> can_set_preference;

    [NativeTypeName("int (*)(struct _cef_preference_manager_t *, const cef_string_t *, struct _cef_value_t *, cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_preference_manager_t*, cef_string_utf16_t*, cef_value_t*, cef_string_utf16_t*, int> set_preference;

    [NativeTypeName("struct _cef_registration_t *(*)(struct _cef_preference_manager_t *, const cef_string_t *, struct _cef_preference_observer_t *)")]
    public delegate* unmanaged[Cdecl]<cef_preference_manager_t*, cef_string_utf16_t*, cef_preference_observer_t*, cef_registration_t*> add_preference_observer;
}
