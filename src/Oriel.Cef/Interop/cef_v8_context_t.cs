namespace Oriel.Cef.Interop;

public unsafe partial struct cef_v8_context_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_task_runner_t *(*)(struct _cef_v8_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, cef_task_runner_t*> get_task_runner;

    [NativeTypeName("int (*)(struct _cef_v8_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, int> is_valid;

    [NativeTypeName("struct _cef_browser_t *(*)(struct _cef_v8_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, cef_browser_t*> get_browser;

    [NativeTypeName("struct _cef_frame_t *(*)(struct _cef_v8_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, cef_frame_t*> get_frame;

    [NativeTypeName("struct _cef_v8_value_t *(*)(struct _cef_v8_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, cef_v8_value_t*> get_global;

    [NativeTypeName("int (*)(struct _cef_v8_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, int> enter;

    [NativeTypeName("int (*)(struct _cef_v8_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, int> exit;

    [NativeTypeName("int (*)(struct _cef_v8_context_t *, struct _cef_v8_context_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, cef_v8_context_t*, int> is_same;

    [NativeTypeName("int (*)(struct _cef_v8_context_t *, const cef_string_t *, const cef_string_t *, int, struct _cef_v8_value_t **, struct _cef_v8_exception_t **)")]
    public delegate* unmanaged[Cdecl]<cef_v8_context_t*, cef_string_utf16_t*, cef_string_utf16_t*, int, cef_v8_value_t**, cef_v8_exception_t**, int> eval;
}
