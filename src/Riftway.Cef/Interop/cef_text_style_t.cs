namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_text_style_t : uint
{
    CEF_TEXT_STYLE_BOLD,
    CEF_TEXT_STYLE_ITALIC,
    CEF_TEXT_STYLE_STRIKE,
    CEF_TEXT_STYLE_DIAGONAL_STRIKE,
    CEF_TEXT_STYLE_UNDERLINE,
    CEF_TEXT_STYLE_NUM_VALUES,
}
