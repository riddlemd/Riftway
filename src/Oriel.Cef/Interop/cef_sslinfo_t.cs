namespace Oriel.Cef.Interop;

public unsafe partial struct cef_sslinfo_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("cef_cert_status_t (*)(struct _cef_sslinfo_t *)")]
    public delegate* unmanaged[Cdecl]<cef_sslinfo_t*, cef_cert_status_t> get_cert_status;

    [NativeTypeName("struct _cef_x509_certificate_t *(*)(struct _cef_sslinfo_t *)")]
    public delegate* unmanaged[Cdecl]<cef_sslinfo_t*, cef_x509_certificate_t*> get_x509_certificate;
}
