namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_return_value_t : uint
{
    RV_CANCEL = 0,
    RV_CONTINUE,
    RV_CONTINUE_ASYNC,
}
