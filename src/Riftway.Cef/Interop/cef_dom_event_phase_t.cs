namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_dom_event_phase_t : uint
{
    DOM_EVENT_PHASE_UNKNOWN,
    DOM_EVENT_PHASE_CAPTURING,
    DOM_EVENT_PHASE_AT_TARGET,
    DOM_EVENT_PHASE_BUBBLING,
    DOM_EVENT_PHASE_NUM_VALUES,
}
