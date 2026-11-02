namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_component_update_priority_t : uint
{
    CEF_COMPONENT_UPDATE_PRIORITY_BACKGROUND = 0,
    CEF_COMPONENT_UPDATE_PRIORITY_FOREGROUND = 1,
}
