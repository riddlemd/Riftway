namespace Riftway.Cef.Interop;

public partial struct cef_accelerated_paint_info_common_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    [NativeTypeName("uint64_t")]
    public ulong timestamp;

    public cef_size_t coded_size;

    public cef_rect_t visible_rect;

    public cef_rect_t content_rect;

    public cef_size_t source_size;

    public cef_rect_t capture_update_rect;

    public cef_rect_t region_capture_rect;

    [NativeTypeName("uint64_t")]
    public ulong capture_counter;

    [NativeTypeName("uint8_t")]
    public byte has_capture_update_rect;

    [NativeTypeName("uint8_t")]
    public byte has_region_capture_rect;

    [NativeTypeName("uint8_t")]
    public byte has_source_size;

    [NativeTypeName("uint8_t")]
    public byte has_capture_counter;
}
