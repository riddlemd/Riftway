namespace Riftway.Cef.Interop;

public unsafe partial struct cef_x509_certificate_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("struct _cef_x509_cert_principal_t *(*)(struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, cef_x509_cert_principal_t*> get_subject;

    [NativeTypeName("struct _cef_x509_cert_principal_t *(*)(struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, cef_x509_cert_principal_t*> get_issuer;

    [NativeTypeName("struct _cef_binary_value_t *(*)(struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, cef_binary_value_t*> get_serial_number;

    [NativeTypeName("cef_basetime_t (*)(struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, cef_basetime_t> get_valid_start;

    [NativeTypeName("cef_basetime_t (*)(struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, cef_basetime_t> get_valid_expiry;

    [NativeTypeName("struct _cef_binary_value_t *(*)(struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, cef_binary_value_t*> get_derencoded;

    [NativeTypeName("struct _cef_binary_value_t *(*)(struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, cef_binary_value_t*> get_pemencoded;

    [NativeTypeName("size_t (*)(struct _cef_x509_certificate_t *)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, nuint> get_issuer_chain_size;

    [NativeTypeName("void (*)(struct _cef_x509_certificate_t *, size_t *, struct _cef_binary_value_t **)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, nuint*, cef_binary_value_t**, void> get_derencoded_issuer_chain;

    [NativeTypeName("void (*)(struct _cef_x509_certificate_t *, size_t *, struct _cef_binary_value_t **)")]
    public delegate* unmanaged[Cdecl]<cef_x509_certificate_t*, nuint*, cef_binary_value_t**, void> get_pemencoded_issuer_chain;
}
