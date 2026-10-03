namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_pointer_type_t : uint
{
    CEF_POINTER_TYPE_TOUCH = 0,
    CEF_POINTER_TYPE_MOUSE,
    CEF_POINTER_TYPE_PEN,
    CEF_POINTER_TYPE_ERASER,
    CEF_POINTER_TYPE_UNKNOWN,
}
