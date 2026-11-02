namespace Riftway.Cef.Interop;

public unsafe partial struct cef_task_manager_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("size_t (*)(struct _cef_task_manager_t *)")]
    public delegate* unmanaged[Cdecl]<cef_task_manager_t*, nuint> get_tasks_count;

    [NativeTypeName("int (*)(struct _cef_task_manager_t *, size_t *, int64_t *)")]
    public delegate* unmanaged[Cdecl]<cef_task_manager_t*, nuint*, long*, int> get_task_ids_list;

    [NativeTypeName("int (*)(struct _cef_task_manager_t *, int64_t, struct _cef_task_info_t *)")]
    public delegate* unmanaged[Cdecl]<cef_task_manager_t*, long, cef_task_info_t*, int> get_task_info;

    [NativeTypeName("int (*)(struct _cef_task_manager_t *, int64_t)")]
    public delegate* unmanaged[Cdecl]<cef_task_manager_t*, long, int> kill_task;

    [NativeTypeName("int64_t (*)(struct _cef_task_manager_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_task_manager_t*, int, long> get_task_id_for_browser_id;
}
