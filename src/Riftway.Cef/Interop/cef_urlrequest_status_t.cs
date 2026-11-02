namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_urlrequest_status_t : uint
{
    UR_UNKNOWN,
    UR_SUCCESS,
    UR_IO_PENDING,
    UR_CANCELED,
    UR_FAILED,
    UR_NUM_VALUES,
}
