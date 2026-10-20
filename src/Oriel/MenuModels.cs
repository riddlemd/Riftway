using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Builds CEF Views menu models from a <see cref="Menu"/>: the menu bar's dropdowns and popups. UI thread only.</summary>
internal static unsafe class MenuModels
{
    /// <summary>Appends <paramref name="menu"/>'s visible items to <paramref name="model"/>; command ids are the items' <see cref="MenuItem.CommandId"/>.</summary>
    internal static void Fill(cef_menu_model_t* model, Menu menu) => FillMenu(model, menu, isSubmenu: false);

    private static void FillMenu(cef_menu_model_t* model, Menu menu, bool isSubmenu)
    {
        var items = menu.Items;
        if (isSubmenu && !items.Any(i => i.Visible && i.Type != MenuItemType.Separator))
        {
            // A submenu otherwise has no command id to tell which Menu it is when it opens; Views still shows it empty.
            var marker = menu.MarkerCommandId;
            cef_string_utf16_t none = default;
            model->add_item(model, marker, &none);
            model->set_visible(model, marker, 0);
        }
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
                case MenuItemType.Submenu or MenuItemType.Palette when item.Submenu is { } submenu:
                    var child = model->add_sub_menu(model, id, &label);
                    FillMenu(child, submenu, isSubmenu: true);
                    NativeObject.Release(child);
                    break;
                case MenuItemType.Checkbox:
                    model->add_check_item(model, id, &label);
                    model->set_checked(model, id, item.CheckStatus ? 1 : 0);
                    break;
                case MenuItemType.Radio:
                    model->add_radio_item(model, id, &label, item.GroupId);
                    model->set_checked(model, id, item.CheckStatus ? 1 : 0);
                    break;
                default:
                    model->add_item(model, id, &label);
                    break;
            }
            CefString.Clear(&label);
            if (item.Type == MenuItemType.Separator) continue;
            model->set_enabled(model, id, item.CommandEnabled ? 1 : 0);
            if (CefAccelerator(item) is { } a)
                model->set_accelerator(model, id, a.KeyCode, Has(a, AcceleratorModifiers.Shift), Has(a, AcceleratorModifiers.Control), Has(a, AcceleratorModifiers.Alt));
        }
    }

    // CEF accelerators have no Command/Super modifier.
    internal static Accelerator? CefAccelerator(MenuItem item)
        => item.Accelerator is { } text && Accelerator.Parse(text, macOS: false) is var a && !a.Modifiers.HasFlag(AcceleratorModifiers.Command) ? a : null;

    internal static int Has(Accelerator accelerator, AcceleratorModifiers modifier) => accelerator.Modifiers.ForMenus().HasFlag(modifier) ? 1 : 0;
}

/// <summary>Electron's <c>menu-will-show</c> and <c>menu-will-close</c> for one run of a CEF Views menu built by
/// <see cref="MenuModels.Fill"/> from <see cref="Root"/>: Views (<c>MenuModelAdapter</c>) reports each submenu on its own
/// model, which maps back to its Menu. UI thread only.</summary>
internal sealed unsafe class MenuRun(Menu root)
{
    // Menus on Views' selection path, in the order they opened.
    private readonly List<Menu> _open = [];
    private bool _released;

    internal Menu Root { get; } = root;

    /// <summary>CEF's <c>menu_will_show</c>: runs <c>_menuWillShow</c> and raises the event on the model's Menu, then shows
    /// the radio items it checked, as Views reads them through the model.</summary>
    internal void WillShow(cef_menu_model_t* model)
    {
        if (MenuOf(model) is not { } menu) return;
        foreach (var item in WillShow(menu)) model->set_checked(model, item.CommandId, 1);
    }

    /// <summary>CEF's <c>menu_closed</c>; true when it is the top menu's, which ends a run not released.</summary>
    internal bool Closed(cef_menu_model_t* model)
    {
        var isSubmenu = model->is_sub_menu(model) != 0;
        return Closed(isSubmenu ? MenuOf(model) : Root, !isSubmenu);
    }

    /// <summary>A menu of the run opened. Returns the radio items <c>_menuWillShow</c> checked.</summary>
    /// <remarks>Escape closes a submenu without <c>WillHideMenu</c> (its item stays on the selection path), so reopening
    /// it shows it again while it is still on the path.</remarks>
    internal IReadOnlyList<MenuItem> WillShow(Menu menu)
    {
        if (!_open.Contains(menu)) _open.Add(menu);
        return menu.OnMenuWillShow();
    }

    /// <summary>A menu of the run closed. Views hides the top menu and then each open submenu down the path
    /// (<c>MenuController::SetSelection</c>), so the top menu closing raises <c>menu-will-close</c> on every open menu in
    /// the order they opened, and the submenus' own late reports are then ignored.</summary>
    internal bool Closed(Menu? menu, bool isRoot)
    {
        if (_released) return false;
        if (isRoot)
        {
            List<Menu> open = [.. _open];
            _open.Clear();
            foreach (var m in open) m.OnMenuWillClose();
            return true;
        }
        if (menu is not null && _open.Remove(menu)) menu.OnMenuWillClose();
        return false;
    }

    /// <summary>The run's owner is letting go of a menu that may still be open: Chromium's <c>MenuRunnerImpl::Release</c>
    /// swaps in an empty delegate before it cancels, so the models never hear the menus close and the close callback
    /// (the adapter's) never runs.</summary>
    internal void Release() => _released = true;

    private Menu? MenuOf(cef_menu_model_t* model)
    {
        if (model->is_sub_menu(model) == 0) return Root;
        var count = (int)model->get_count(model);
        var ids = new int[count];
        for (var i = 0; i < count; i++) ids[i] = model->get_command_id_at(model, (nuint)i);
        return Resolve(Root, ids);
    }

    /// <summary>The submenu of <paramref name="root"/> whose model holds <paramref name="commandIds"/>: the parent of its
    /// first item, or the menu whose marker it is. Separators are -1.</summary>
    internal static Menu? Resolve(Menu root, IReadOnlyList<int> commandIds)
    {
        foreach (var id in commandIds)
        {
            if (id < 0) continue;
            if (root.FindByCommandId(id) is { } item) return item.Parent;
            foreach (var descendant in root.Descendants())
                if (descendant.Submenu is { } submenu && submenu.IsMarker(id)) return submenu;
            return null;
        }
        return null;
    }
}
