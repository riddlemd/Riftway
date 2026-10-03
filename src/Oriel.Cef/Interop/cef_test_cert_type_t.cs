namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_test_cert_type_t : uint
{
    CEF_TEST_CERT_OK_IP,
    CEF_TEST_CERT_OK_DOMAIN,
    CEF_TEST_CERT_EXPIRED,
    CEF_TEST_CERT_NUM_VALUES,
}
