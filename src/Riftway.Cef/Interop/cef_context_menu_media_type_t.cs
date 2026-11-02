namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_context_menu_media_type_t : uint
{
    CM_MEDIATYPE_NONE,
    CM_MEDIATYPE_IMAGE,
    CM_MEDIATYPE_VIDEO,
    CM_MEDIATYPE_AUDIO,
    CM_MEDIATYPE_CANVAS,
    CM_MEDIATYPE_FILE,
    CM_MEDIATYPE_PLUGIN,
    CM_MEDIATYPE_NUM_VALUES,
}
