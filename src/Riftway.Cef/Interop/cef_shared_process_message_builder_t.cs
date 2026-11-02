namespace Riftway.Cef.Interop;

public unsafe partial struct cef_shared_process_message_builder_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_shared_process_message_builder_t *)")]
    public delegate* unmanaged[Cdecl]<cef_shared_process_message_builder_t*, int> is_valid;

    [NativeTypeName("size_t (*)(struct _cef_shared_process_message_builder_t *)")]
    public delegate* unmanaged[Cdecl]<cef_shared_process_message_builder_t*, nuint> size;

    [NativeTypeName("void *(*)(struct _cef_shared_process_message_builder_t *)")]
    public delegate* unmanaged[Cdecl]<cef_shared_process_message_builder_t*, void*> memory;

    [NativeTypeName("struct _cef_process_message_t *(*)(struct _cef_shared_process_message_builder_t *)")]
    public delegate* unmanaged[Cdecl]<cef_shared_process_message_builder_t*, cef_process_message_t*> build;
}
