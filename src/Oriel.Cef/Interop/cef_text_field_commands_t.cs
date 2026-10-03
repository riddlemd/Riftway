namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_text_field_commands_t : uint
{
    CEF_TFC_UNKNOWN,
    CEF_TFC_CUT,
    CEF_TFC_COPY,
    CEF_TFC_PASTE,
    CEF_TFC_SELECT_ALL,
    CEF_TFC_SELECT_WORD,
    CEF_TFC_UNDO,
    CEF_TFC_DELETE,
    CEF_TFC_NUM_VALUES,
}
