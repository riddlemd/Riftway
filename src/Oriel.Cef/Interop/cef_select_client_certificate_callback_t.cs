namespace Oriel.Cef.Interop;

public unsafe partial struct cef_select_client_certificate_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_select_client_certificate_callback_t *, struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_select_client_certificate_callback_t*, cef_x509_certificate_t*, void> select;
}
