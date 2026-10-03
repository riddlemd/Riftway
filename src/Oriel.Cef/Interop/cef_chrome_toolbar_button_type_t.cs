namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_chrome_toolbar_button_type_t : uint
{
    CEF_CTBT_CAST_DEPRECATED,
    CEF_CTBT_DOWNLOAD_DEPRECATED,
    CEF_CTBT_SEND_TAB_TO_SELF_DEPRECATED,
    CEF_CTBT_SIDE_PANEL_DEPRECATED,
    CEF_CTBT_MEDIA,
    CEF_CTBT_TAB_SEARCH_DEPRECATED,
    CEF_CTBT_BATTERY_SAVER,
    CEF_CTBT_AVATAR,
    CEF_CTBT_NUM_VALUES,
}
