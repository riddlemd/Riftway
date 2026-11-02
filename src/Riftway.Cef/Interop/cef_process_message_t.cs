namespace Riftway.Cef.Interop;

public unsafe partial struct cef_process_message_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_process_message_t *)")]
    public delegate* unmanaged[Cdecl]<cef_process_message_t*, int> is_valid;

    [NativeTypeName("int (*)(struct _cef_process_message_t *)")]
    public delegate* unmanaged[Cdecl]<cef_process_message_t*, int> is_read_only;

    [NativeTypeName("struct _cef_process_message_t *(*)(struct _cef_process_message_t *)")]
    public delegate* unmanaged[Cdecl]<cef_process_message_t*, cef_process_message_t*> copy;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_process_message_t *)")]
    public delegate* unmanaged[Cdecl]<cef_process_message_t*, cef_string_utf16_t*> get_name;

    [NativeTypeName("struct _cef_list_value_t *(*)(struct _cef_process_message_t *)")]
    public delegate* unmanaged[Cdecl]<cef_process_message_t*, cef_list_value_t*> get_argument_list;

    [NativeTypeName("struct _cef_shared_memory_region_t *(*)(struct _cef_process_message_t *)")]
    public delegate* unmanaged[Cdecl]<cef_process_message_t*, cef_shared_memory_region_t*> get_shared_memory_region;
}
