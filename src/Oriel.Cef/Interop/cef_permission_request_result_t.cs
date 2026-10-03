namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_permission_request_result_t : uint
{
    CEF_PERMISSION_RESULT_ACCEPT,
    CEF_PERMISSION_RESULT_DENY,
    CEF_PERMISSION_RESULT_DISMISS,
    CEF_PERMISSION_RESULT_IGNORE,
    CEF_PERMISSION_RESULT_NUM_VALUES,
}
