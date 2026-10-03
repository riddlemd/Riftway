namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_response_filter_status_t : uint
{
    RESPONSE_FILTER_NEED_MORE_DATA,
    RESPONSE_FILTER_DONE,
    RESPONSE_FILTER_ERROR,
}
