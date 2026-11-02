namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_thread_priority_t : uint
{
    TP_BACKGROUND,
    TP_NORMAL,
    TP_DISPLAY,
    TP_REALTIME_AUDIO,
    TP_NUM_VALUES,
}
