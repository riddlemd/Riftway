namespace Oriel.Cef.Interop;

public partial struct cef_touch_handle_state_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    public int touch_handle_id;

    [NativeTypeName("uint32_t")]
    public uint flags;

    public int enabled;

    public cef_horizontal_alignment_t orientation;

    public int mirror_vertical;

    public int mirror_horizontal;

    public cef_point_t origin;

    public float alpha;
}
