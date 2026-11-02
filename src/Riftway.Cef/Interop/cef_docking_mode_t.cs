namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_docking_mode_t : uint
{
    CEF_DOCKING_MODE_TOP_LEFT,
    CEF_DOCKING_MODE_TOP_RIGHT,
    CEF_DOCKING_MODE_BOTTOM_LEFT,
    CEF_DOCKING_MODE_BOTTOM_RIGHT,
    CEF_DOCKING_MODE_CUSTOM,
    CEF_DOCKING_MODE_NUM_VALUES,
}
