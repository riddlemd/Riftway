namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_cookie_same_site_t : uint
{
    CEF_COOKIE_SAME_SITE_UNSPECIFIED,
    CEF_COOKIE_SAME_SITE_NO_RESTRICTION,
    CEF_COOKIE_SAME_SITE_LAX_MODE,
    CEF_COOKIE_SAME_SITE_STRICT_MODE,
    CEF_COOKIE_SAME_SITE_NUM_VALUES,
}
