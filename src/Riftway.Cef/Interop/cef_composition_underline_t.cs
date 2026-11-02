namespace Riftway.Cef.Interop;

public partial struct cef_composition_underline_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    public cef_range_t range;

    [NativeTypeName("cef_color_t")]
    public uint color;

    [NativeTypeName("cef_color_t")]
    public uint background_color;

    public int thick;

    public cef_composition_underline_style_t style;
}
