namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_message_loop_type_t : uint
{
    ML_TYPE_DEFAULT,
    ML_TYPE_UI,
    ML_TYPE_IO,
    ML_NUM_VALUES,
}
