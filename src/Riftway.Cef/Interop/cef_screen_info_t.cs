namespace Riftway.Cef.Interop;

public partial struct cef_screen_info_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    public float device_scale_factor;

    public int depth;

    public int depth_per_component;

    public int is_monochrome;

    public cef_rect_t rect;

    public cef_rect_t available_rect;
}
