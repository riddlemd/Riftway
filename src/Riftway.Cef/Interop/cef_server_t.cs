namespace Riftway.Cef.Interop;

public unsafe partial struct cef_server_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_task_runner_t *(*)(struct _cef_server_t *)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, cef_task_runner_t*> get_task_runner;

    [NativeTypeName("void (*)(struct _cef_server_t *)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, void> shutdown;

    [NativeTypeName("int (*)(struct _cef_server_t *)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int> is_running;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_server_t *)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, cef_string_utf16_t*> get_address;

    [NativeTypeName("int (*)(struct _cef_server_t *)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int> has_connection;

    [NativeTypeName("int (*)(struct _cef_server_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int, int> is_valid_connection;

    [NativeTypeName("void (*)(struct _cef_server_t *, int, const cef_string_t *, const void *, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int, cef_string_utf16_t*, void*, nuint, void> send_http200_response;

    [NativeTypeName("void (*)(struct _cef_server_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int, void> send_http404_response;

    [NativeTypeName("void (*)(struct _cef_server_t *, int, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int, cef_string_utf16_t*, void> send_http500_response;

    [NativeTypeName("void (*)(struct _cef_server_t *, int, int, const cef_string_t *, int64_t, cef_string_multimap_t)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int, int, cef_string_utf16_t*, long, cef_string_multimap_t*, void> send_http_response;

    [NativeTypeName("void (*)(struct _cef_server_t *, int, const void *, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int, void*, nuint, void> send_raw_data;

    [NativeTypeName("void (*)(struct _cef_server_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int, void> close_connection;

    [NativeTypeName("void (*)(struct _cef_server_t *, int, const void *, size_t)")]
    public delegate* unmanaged[Cdecl]<cef_server_t*, int, void*, nuint, void> send_web_socket_message;
}
