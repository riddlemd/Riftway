namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_value_type_t : uint
{
    VTYPE_INVALID,
    VTYPE_NULL,
    VTYPE_BOOL,
    VTYPE_INT,
    VTYPE_DOUBLE,
    VTYPE_STRING,
    VTYPE_BINARY,
    VTYPE_DICTIONARY,
    VTYPE_LIST,
    VTYPE_NUM_VALUES,
}
