namespace Oriel.Cef.Interop;

public unsafe partial struct cef_sslstatus_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_sslstatus_t *)")]
    public delegate* unmanaged[Cdecl]<cef_sslstatus_t*, int> is_secure_connection;

    [NativeTypeName("cef_cert_status_t (*)(struct _cef_sslstatus_t *)")]
    public delegate* unmanaged[Cdecl]<cef_sslstatus_t*, cef_cert_status_t> get_cert_status;

    [NativeTypeName("cef_ssl_version_t (*)(struct _cef_sslstatus_t *)")]
    public delegate* unmanaged[Cdecl]<cef_sslstatus_t*, cef_ssl_version_t> get_sslversion;

    [NativeTypeName("cef_ssl_content_status_t (*)(struct _cef_sslstatus_t *)")]
    public delegate* unmanaged[Cdecl]<cef_sslstatus_t*, cef_ssl_content_status_t> get_content_status;

    [NativeTypeName("struct _cef_x509_certificate_t *(*)(struct _cef_sslstatus_t *)")]
    public delegate* unmanaged[Cdecl]<cef_sslstatus_t*, cef_x509_certificate_t*> get_x509_certificate;
}
