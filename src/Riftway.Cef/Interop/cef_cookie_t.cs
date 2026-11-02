namespace Riftway.Cef.Interop;

public partial struct cef_cookie_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t name;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t value;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t domain;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t path;

    public int secure;

    public int httponly;

    public cef_basetime_t creation;

    public cef_basetime_t last_access;

    public int has_expires;

    public cef_basetime_t expires;

    public cef_cookie_same_site_t same_site;

    public cef_cookie_priority_t priority;
}
