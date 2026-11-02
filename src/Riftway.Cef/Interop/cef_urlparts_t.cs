namespace Riftway.Cef.Interop;

public partial struct cef_urlparts_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t spec;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t scheme;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t username;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t password;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t host;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t port;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t origin;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t path;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t query;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t fragment;
}
