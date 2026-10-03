namespace Oriel.Cef.Interop;

public partial struct cef_popup_features_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    public int x;

    public int xSet;

    public int y;

    public int ySet;

    public int width;

    public int widthSet;

    public int height;

    public int heightSet;

    public int isPopup;
}
