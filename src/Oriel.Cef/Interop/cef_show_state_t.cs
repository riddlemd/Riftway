namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_show_state_t : uint
{
    CEF_SHOW_STATE_NORMAL,
    CEF_SHOW_STATE_MINIMIZED,
    CEF_SHOW_STATE_MAXIMIZED,
    CEF_SHOW_STATE_FULLSCREEN,
    CEF_SHOW_STATE_HIDDEN,
    CEF_SHOW_STATE_NUM_VALUES,
}
