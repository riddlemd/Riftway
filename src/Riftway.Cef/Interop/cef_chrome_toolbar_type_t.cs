namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_chrome_toolbar_type_t : uint
{
    CEF_CTT_UNKNOWN,
    CEF_CTT_NONE,
    CEF_CTT_NORMAL,
    CEF_CTT_LOCATION,
    CEF_CTT_NUM_VALUES,
}
