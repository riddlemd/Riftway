namespace Riftway.Cef.Interop;

public unsafe partial struct cef_cursor_info_t
{
    public cef_point_t hotspot;

    public float image_scale_factor;

    public void* buffer;

    public cef_size_t size;
}
