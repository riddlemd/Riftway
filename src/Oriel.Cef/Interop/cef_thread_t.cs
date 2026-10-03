namespace Oriel.Cef.Interop;

public unsafe partial struct cef_thread_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_task_runner_t *(*)(struct _cef_thread_t *)")]
    public delegate* unmanaged[Cdecl]<cef_thread_t*, cef_task_runner_t*> get_task_runner;

    [NativeTypeName("cef_platform_thread_id_t (*)(struct _cef_thread_t *)")]
    public delegate* unmanaged[Cdecl]<cef_thread_t*, int> get_platform_thread_id;

    [NativeTypeName("void (*)(struct _cef_thread_t *)")]
    public delegate* unmanaged[Cdecl]<cef_thread_t*, void> stop;

    [NativeTypeName("int (*)(struct _cef_thread_t *)")]
    public delegate* unmanaged[Cdecl]<cef_thread_t*, int> is_running;
}
