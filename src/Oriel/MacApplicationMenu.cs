using System.Runtime.Versioning;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Shows <see cref="Menu.GetApplicationMenu"/> as the macOS menu bar, rebuilt whenever it changes.</summary>
[SupportedOSPlatform("macos")]
internal static class MacApplicationMenu
{
    // UI thread only.
    private static Menu? s_shown;
    private static bool s_rebuildPosted;

    /// <summary>Starts following the application menu; call on the UI thread once CEF is ready.</summary>
    internal static void Install()
    {
        MacMenu.Chosen = OnChosen;
        Menu.ApplicationMenuChanged += _ => PostRebuild();
        PostRebuild();
    }

    private static void PostRebuild() => CefThread.RunOnUi(() =>
    {
        if (s_rebuildPosted) return;
        s_rebuildPosted = true;
        // Batches a burst of changes (a radio click unchecks its siblings) into one rebuild.
        CefThread.PostUi(Rebuild);
    });

    private static void Rebuild()
    {
        s_rebuildPosted = false;
        var menu = Menu.GetApplicationMenu();
        if (!ReferenceEquals(menu, s_shown))
        {
            if (s_shown is not null) s_shown.Changed -= PostRebuild;
            if (menu is not null) menu.Changed += PostRebuild;
            s_shown = menu;
        }

        var pool = MacMenu.PushPool();
        try
        {
            if (menu is null)
            {
                MacMenu.SetMainMenu(0);
                return;
            }
            var bar = MacMenu.CreateMenu(string.Empty);
            foreach (var item in menu.Items) AddItem(bar, item);
            MacMenu.SetMainMenu(bar);
            MacMenu.Release(bar);
        }
        finally
        {
            MacMenu.PopPool(pool);
        }
    }

    private static void AddItem(nint nsMenu, MenuItem item)
    {
        if (item.Type == MenuItemType.Separator)
        {
            MacMenu.AddSeparator(nsMenu);
            return;
        }

        var accelerator = item.Accelerator is { } text ? Accelerator.Parse(text, macOS: true) : (Accelerator?)null;
        var native = MacMenu.AddItem(nsMenu, MenuLabels.WithoutMnemonics(item.Label), item.CommandId,
            accelerator?.MacKeyEquivalent ?? string.Empty, accelerator is { } a ? MaskOf(a.Modifiers) : 0,
            MenuRoles.MacSelector(item.Role));
        MacMenu.SetEnabled(native, item.Enabled);
        MacMenu.SetHidden(native, !item.Visible);
        if (item.Type is MenuItemType.Checkbox or MenuItemType.Radio) MacMenu.SetChecked(native, item.Checked);

        if (item.Submenu is not { } submenu) return;
        var nsSubmenu = MacMenu.CreateMenu(MenuLabels.WithoutMnemonics(item.Label));
        foreach (var child in submenu.Items) AddItem(nsSubmenu, child);
        MacMenu.SetSubmenu(native, nsSubmenu);
        switch (item.Role)
        {
            case MenuItemRole.Services: MacMenu.SetServicesMenu(nsSubmenu); break;
            case MenuItemRole.Window or MenuItemRole.WindowMenu: MacMenu.SetWindowsMenu(nsSubmenu); break;
            case MenuItemRole.Help: MacMenu.SetHelpMenu(nsSubmenu); break;
        }
        MacMenu.Release(nsSubmenu);
    }

    private static ulong MaskOf(AcceleratorModifiers modifiers)
        => (modifiers.HasFlag(AcceleratorModifiers.Command) ? MacMenu.CommandKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Shift) ? MacMenu.ShiftKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Alt) ? MacMenu.OptionKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Control) ? MacMenu.ControlKeyMask : 0);

    private static void OnChosen(nint tag)
    {
        if (s_shown?.FindByCommandId((int)tag) is { } item) item.Activate(BrowserWindow.GetFocusedWindow());
    }
}
