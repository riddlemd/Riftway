namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_key_event_type_t : uint
{
    KEYEVENT_RAWKEYDOWN = 0,
    KEYEVENT_KEYDOWN,
    KEYEVENT_KEYUP,
    KEYEVENT_CHAR,
}
