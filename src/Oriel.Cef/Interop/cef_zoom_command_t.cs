namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_zoom_command_t : uint
{
    CEF_ZOOM_COMMAND_OUT,
    CEF_ZOOM_COMMAND_RESET,
    CEF_ZOOM_COMMAND_IN,
}
