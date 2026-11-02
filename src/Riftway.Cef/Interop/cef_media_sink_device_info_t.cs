namespace Riftway.Cef.Interop;

public partial struct cef_media_sink_device_info_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t ip_address;

    public int port;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t model_name;
}
