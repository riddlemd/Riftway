namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_state_t : uint
{
    STATE_DEFAULT = 0,
    STATE_ENABLED,
    STATE_DISABLED,
}
