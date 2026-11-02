namespace Riftway.Cef.Interop;

public unsafe partial struct cef_waitable_event_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_waitable_event_t *)")]
    public delegate* unmanaged[Cdecl]<cef_waitable_event_t*, void> reset;

    [NativeTypeName("void (*)(struct _cef_waitable_event_t *)")]
    public delegate* unmanaged[Cdecl]<cef_waitable_event_t*, void> signal;

    [NativeTypeName("int (*)(struct _cef_waitable_event_t *)")]
    public delegate* unmanaged[Cdecl]<cef_waitable_event_t*, int> is_signaled;

    [NativeTypeName("void (*)(struct _cef_waitable_event_t *)")]
    public delegate* unmanaged[Cdecl]<cef_waitable_event_t*, void> wait;

    [NativeTypeName("int (*)(struct _cef_waitable_event_t *, int64_t)")]
    public delegate* unmanaged[Cdecl]<cef_waitable_event_t*, long, int> timed_wait;
}
