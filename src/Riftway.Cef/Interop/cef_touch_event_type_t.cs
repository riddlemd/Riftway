namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_touch_event_type_t : uint
{
    CEF_TET_RELEASED = 0,
    CEF_TET_PRESSED,
    CEF_TET_MOVED,
    CEF_TET_CANCELLED,
}
