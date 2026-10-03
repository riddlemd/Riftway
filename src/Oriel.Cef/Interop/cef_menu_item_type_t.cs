namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_menu_item_type_t : uint
{
    MENUITEMTYPE_NONE,
    MENUITEMTYPE_COMMAND,
    MENUITEMTYPE_CHECK,
    MENUITEMTYPE_RADIO,
    MENUITEMTYPE_SEPARATOR,
    MENUITEMTYPE_SUBMENU,
}
