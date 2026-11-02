namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_preferences_type_t : uint
{
    CEF_PREFERENCES_TYPE_GLOBAL,
    CEF_PREFERENCES_TYPE_REQUEST_CONTEXT,
    CEF_PREFERENCES_TYPE_NUM_VALUES,
}
