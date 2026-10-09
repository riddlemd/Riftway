using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Builds CEF Views menu models from a <see cref="Menu"/>: the menu bar's dropdowns and popups. UI thread only.</summary>
internal static unsafe class MenuModels
{
    /// <summary>Appends <paramref name="menu"/>'s visible items to <paramref name="model"/>; command ids are the items' <see cref="MenuItem.CommandId"/>.</summary>
    internal static void Fill(cef_menu_model_t* model, Menu menu)
    {
        var items = menu.Items;
        var radioGroup = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.Visible) continue;
            var id = item.CommandId;
            cef_string_utf16_t label = default;
            CefString.Set(&label, item.Label);
            switch (item.Type)
            {
                case MenuItemType.Separator:
                    model->add_separator(model);
                    break;
                case MenuItemType.Submenu when item.Submenu is { } submenu:
                    var child = model->add_sub_menu(model, id, &label);
                    Fill(child, submenu);
                    NativeObject.Release(child);
                    break;
                case MenuItemType.Checkbox:
                    model->add_check_item(model, id, &label);
                    model->set_checked(model, id, item.Checked ? 1 : 0);
                    break;
                case MenuItemType.Radio:
                    // Adjacent radio items form a group, as in Menu.CheckRadio.
                    if (i == 0 || items[i - 1].Type != MenuItemType.Radio) radioGroup++;
                    model->add_radio_item(model, id, &label, radioGroup);
                    model->set_checked(model, id, item.Checked ? 1 : 0);
                    break;
                default:
                    model->add_item(model, id, &label);
                    break;
            }
            CefString.Clear(&label);
            if (item.Type == MenuItemType.Separator) continue;
            model->set_enabled(model, id, item.Enabled ? 1 : 0);
            if (CefAccelerator(item) is { } a)
                model->set_accelerator(model, id, a.KeyCode, Has(a, AcceleratorModifiers.Shift), Has(a, AcceleratorModifiers.Control), Has(a, AcceleratorModifiers.Alt));
        }
    }

    // CEF accelerators have no Command/Super modifier.
    internal static Accelerator? CefAccelerator(MenuItem item)
        => item.Accelerator is { } text && Accelerator.Parse(text, macOS: false) is var a && !a.Modifiers.HasFlag(AcceleratorModifiers.Command) ? a : null;

    internal static int Has(Accelerator accelerator, AcceleratorModifiers modifier) => accelerator.Modifiers.ForMenus().HasFlag(modifier) ? 1 : 0;
}
