namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_navigation_type_t : uint
{
    NAVIGATION_LINK_CLICKED,
    NAVIGATION_FORM_SUBMITTED,
    NAVIGATION_BACK_FORWARD,
    NAVIGATION_RELOAD,
    NAVIGATION_FORM_RESUBMITTED,
    NAVIGATION_OTHER,
    NAVIGATION_NUM_VALUES,
}
