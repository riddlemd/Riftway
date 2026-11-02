namespace Riftway.Cef.Interop;

public unsafe partial struct cef_v8_stack_trace_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_v8_stack_trace_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_stack_trace_t*, int> is_valid;

    [NativeTypeName("int (*)(struct _cef_v8_stack_trace_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_stack_trace_t*, int> get_frame_count;

    [NativeTypeName("struct _cef_v8_stack_frame_t *(*)(struct _cef_v8_stack_trace_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_v8_stack_trace_t*, int, cef_v8_stack_frame_t*> get_frame;
}
