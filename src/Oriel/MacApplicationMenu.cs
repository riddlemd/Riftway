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
    private static Menu? s_dock;
    private static bool s_rebuildPosted;
    private static MacMenuController? s_barController;
    private static MacMenuController? s_dockController;

    /// <summary>Starts following the application menu; call on the UI thread once CEF is ready.</summary>
    internal static void Install()
    {
        MacMenu.Chosen = OnChosen;
        InstallMenuEvents();
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
                s_barController = null;
                MacMenu.SetMainMenu(0);
                return;
            }
            var bar = MacMenu.CreateMenu(string.Empty);
            foreach (var item in menu.Items) AddItem(bar, item);
            MacMenu.SetDelegate(bar);
            s_barController = new MacMenuController(menu, bar);
            MacMenu.SetMainMenu(bar);
            MacMenu.Release(bar);
        }
        finally
        {
            MacMenu.PopPool(pool);
        }
    }

    /// <summary>Makes <paramref name="menu"/> the Dock menu, built afresh each time the Dock asks; UI thread.</summary>
    internal static void SetDockMenu(Menu menu)
    {
        MacMenu.Chosen = OnChosen;
        InstallMenuEvents();
        s_dock = menu;
        MacActivation.InstallDockMenu(BuildDockMenu);
    }

    private static nint BuildDockMenu()
    {
        if (s_dock is null) return 0;
        var nsMenu = MacMenu.CreateMenu(string.Empty);
        foreach (var item in s_dock.Items) AddItem(nsMenu, item, inMenuBar: false);
        MacMenu.SetDelegate(nsMenu);
        s_dockController = new MacMenuController(s_dock, nsMenu);
        return MacMenu.Autorelease(nsMenu);
    }

    private static void AddItem(nint nsMenu, MenuItem item) => AddItem(nsMenu, item, inMenuBar: true);

    private static void AddItem(nint nsMenu, MenuItem item, bool inMenuBar)
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
        foreach (var child in submenu.Items) AddItem(nsSubmenu, child, inMenuBar);
        MacMenu.SetSubmenu(native, nsSubmenu);
        MacMenu.SetDelegate(nsSubmenu);
        // Only the menu bar's submenus become the app's Services, Window and Help menus.
        if (inMenuBar) switch (item.Role)
        {
            case MenuItemRole.Services: MacMenu.SetServicesMenu(nsSubmenu); break;
            case MenuItemRole.Window or MenuItemRole.WindowMenu: MacMenu.SetWindowsMenu(nsSubmenu); break;
            case MenuItemRole.Help: MacMenu.SetHelpMenu(nsSubmenu); break;
        }
        MacMenu.Release(nsSubmenu);
    }

    // Electron's ElectronMenuController is the delegate of its NSMenu and every submenu, and reports them all on the
    // one Menu it was made for: the application menu, or the Dock menu.
    private static void InstallMenuEvents()
    {
        MacMenu.Opened = menu => ControllerOf(menu)?.WillOpen(menu, RemoveInjectedFullScreenItems);
        MacMenu.Closed = menu => ControllerOf(menu)?.DidClose(menu, MacMenu.Supermenu(menu));
    }

    private static MacMenuController? ControllerOf(nint menu)
    {
        var top = menu;
        for (var up = MacMenu.Supermenu(top); up != 0; up = MacMenu.Supermenu(top)) top = up;
        if (s_barController?.NativeMenu == top) return s_barController;
        return s_dockController?.NativeMenu == top ? s_dockController : null;
    }

    // macOS adds a hidden duplicate "Toggle Full Screen" to a submenu that has a delegate; Electron removes it.
    private static void RemoveInjectedFullScreenItems(nint menu)
    {
        for (var i = MacMenu.ItemCount(menu) - 1; i >= 0; i--)
        {
            var item = MacMenu.ItemAt(menu, i);
            if (MacMenu.IsHidden(item) && MacMenu.ActionName(item) == "toggleFullScreenMode:") MacMenu.RemoveItem(menu, item);
        }
    }

    private static ulong MaskOf(AcceleratorModifiers modifiers)
        => (modifiers.HasFlag(AcceleratorModifiers.Command) ? MacMenu.CommandKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Shift) ? MacMenu.ShiftKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Alt) ? MacMenu.OptionKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Control) ? MacMenu.ControlKeyMask : 0);

    private static void OnChosen(nint tag)
    {
        if ((s_shown?.FindByCommandId((int)tag) ?? s_dock?.FindByCommandId((int)tag)) is not { } item) return;
        var (flags, keyDown) = MacMenu.CurrentEvent();
        item.Activate(BrowserWindow.GetFocusedWindow(), KeyboardEvent.FromMacModifiers(flags, keyDown));
    }
}

/// <summary>The menu events of Electron's <c>ElectronMenuController</c> for a menu without a popup close callback (the
/// application menu, the Dock menu): every NSMenu that opens under <see cref="NativeMenu"/> raises <c>menu-will-show</c>
/// on <see cref="Menu"/>, and <c>menu-will-close</c> comes when <see cref="NativeMenu"/> or one of its direct submenus
/// closes. Main thread only.</summary>
internal sealed class MacMenuController(Menu menu, nint nativeMenu)
{
    private bool _isMenuOpen;

    internal Menu Menu { get; } = menu;

    internal nint NativeMenu { get; } = nativeMenu;

    /// <summary><c>menuWillOpen:</c>, for <paramref name="menu"/> or any submenu.</summary>
    internal void WillOpen(nint menu, Action<nint> removeInjectedItems)
    {
        _isMenuOpen = true;
        removeInjectedItems(menu);
        Menu.OnMenuWillShow();
    }

    /// <summary><c>menuDidClose:</c>; <paramref name="supermenu"/> is the closing menu's.</summary>
    internal void DidClose(nint menu, nint supermenu)
    {
        if (!_isMenuOpen) return;
        // A deeper submenu closes before the menu above it; only the top level ends the menu.
        if (menu != NativeMenu && supermenu != NativeMenu) return;
        _isMenuOpen = false;
        Menu.OnMenuWillClose();
    }
}
