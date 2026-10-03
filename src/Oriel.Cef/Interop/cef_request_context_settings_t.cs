namespace Oriel.Cef.Interop;

public partial struct cef_request_context_settings_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t cache_path;

    public int persist_session_cookies;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t accept_language_list;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t cookieable_schemes_list;

    public int cookieable_schemes_exclude_defaults;
}
