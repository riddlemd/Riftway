namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_jsdialog_type_t : uint
{
    JSDIALOGTYPE_ALERT,
    JSDIALOGTYPE_CONFIRM,
    JSDIALOGTYPE_PROMPT,
    JSDIALOGTYPE_NUM_VALUES,
}
