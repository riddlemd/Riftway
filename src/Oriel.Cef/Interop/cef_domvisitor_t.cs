namespace Oriel.Cef.Interop;

public unsafe partial struct cef_domvisitor_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_domvisitor_t *, struct _cef_domdocument_t *)")]
    public delegate* unmanaged[Cdecl]<cef_domvisitor_t*, cef_domdocument_t*, void> visit;
}
