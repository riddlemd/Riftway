namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_button_state_t : uint
{
    CEF_BUTTON_STATE_NORMAL,
    CEF_BUTTON_STATE_HOVERED,
    CEF_BUTTON_STATE_PRESSED,
    CEF_BUTTON_STATE_DISABLED,
    CEF_BUTTON_STATE_NUM_VALUES,
}
