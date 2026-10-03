namespace Oriel.Cef.Interop;

public unsafe partial struct cef_resource_read_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_resource_read_callback_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_resource_read_callback_t*, int, void> cont;
}
