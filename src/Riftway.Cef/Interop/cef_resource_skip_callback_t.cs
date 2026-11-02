namespace Riftway.Cef.Interop;

public unsafe partial struct cef_resource_skip_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_resource_skip_callback_t *, int64_t)")]
    public delegate* unmanaged[Cdecl]<cef_resource_skip_callback_t*, long, void> cont;
}
