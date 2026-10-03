namespace Oriel.Cef.Interop;

public unsafe partial struct cef_base_scoped_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    [NativeTypeName("void (*)(struct _cef_base_scoped_t *)")]
    public delegate* unmanaged[Cdecl]<cef_base_scoped_t*, void> del;
}
