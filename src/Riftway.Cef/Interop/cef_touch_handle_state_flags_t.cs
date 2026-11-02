namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_touch_handle_state_flags_t : uint
{
    CEF_THS_FLAG_NONE = 0,
    CEF_THS_FLAG_ENABLED = 1 << 0,
    CEF_THS_FLAG_ORIENTATION = 1 << 1,
    CEF_THS_FLAG_ORIGIN = 1 << 2,
    CEF_THS_FLAG_ALPHA = 1 << 3,
}
