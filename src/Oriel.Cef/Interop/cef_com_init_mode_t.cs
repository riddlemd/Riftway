namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_com_init_mode_t : uint
{
    COM_INIT_MODE_NONE,
    COM_INIT_MODE_STA,
    COM_INIT_MODE_MTA,
}
