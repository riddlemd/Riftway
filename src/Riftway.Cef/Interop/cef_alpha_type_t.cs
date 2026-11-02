namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_alpha_type_t : uint
{
    CEF_ALPHA_TYPE_OPAQUE,
    CEF_ALPHA_TYPE_PREMULTIPLIED,
    CEF_ALPHA_TYPE_POSTMULTIPLIED,
}
