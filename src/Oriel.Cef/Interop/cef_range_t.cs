namespace Oriel.Cef.Interop;

public partial struct cef_range_t
{
    [NativeTypeName("uint32_t")]
    public uint from;

    [NativeTypeName("uint32_t")]
    public uint to;
}
