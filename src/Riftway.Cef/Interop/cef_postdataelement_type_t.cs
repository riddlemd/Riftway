namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_postdataelement_type_t : uint
{
    PDE_TYPE_EMPTY = 0,
    PDE_TYPE_BYTES,
    PDE_TYPE_FILE,
    PDE_TYPE_NUM_VALUES,
}
