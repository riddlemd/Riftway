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
    private static readonly List<MacMenuController> s_popupControllers = [];

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
            s_barController = new MacMenuController(menu, bar, MacMenuTree.Instance);
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
        s_dockController = new MacMenuController(s_dock, nsMenu, MacMenuTree.Instance);
        return MacMenu.Autorelease(nsMenu);
    }

    /// <summary>A tray menu as Electron's <c>ElectronMenuController initWithModel:useDefaultAccelerator:NO</c> builds it:
    /// an NSMenu (owned by the controller until <see cref="ReleasePopupMenu"/>) whose delegate reports to
    /// <paramref name="menu"/>. UI thread.</summary>
    internal static MacMenuController CreatePopupMenu(Menu menu)
    {
        MacMenu.Chosen = OnChosen;
        InstallMenuEvents();
        var nsMenu = MacMenu.CreateMenu(string.Empty);
        foreach (var item in menu.Items) AddItem(nsMenu, item, inMenuBar: false);
        MacMenu.SetDelegate(nsMenu);
        var controller = new MacMenuController(menu, nsMenu, MacMenuTree.Instance);
        s_popupControllers.Add(controller);
        return controller;
    }

    /// <summary>Forgets a <see cref="CreatePopupMenu"/> controller and releases its NSMenu. UI thread.</summary>
    internal static void ReleasePopupMenu(MacMenuController controller)
    {
        if (s_popupControllers.Remove(controller)) MacMenu.Release(controller.NativeMenu);
    }

    private static void AddItem(nint nsMenu, MenuItem item) => AddItem(nsMenu, item, inMenuBar: true);

    private static void AddItem(nint nsMenu, MenuItem item, bool inMenuBar)
    {
        if (item.Type == MenuItemType.Separator)
        {
            MacMenu.AddSeparator(nsMenu);
            return;
        }

        var accelerator = MacMenuItemState.ShownAccelerator(item, inMenuBar) is { } text ? Accelerator.Parse(text, macOS: true) : (Accelerator?)null;
        var native = MacMenu.AddItem(nsMenu, MenuLabels.WithoutMnemonics(item.Label), item.CommandId,
            accelerator?.MacKeyEquivalent ?? string.Empty, accelerator is { } a ? MaskOf(a.MacMenuModifiers) : 0,
            MenuRoles.MacSelector(item.Role));
        if (MacMenuItemState.Of(item) is { } state) MacMenuTree.Instance.Apply(native, state);

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
        MacMenu.NeedsUpdate = menu => ControllerOf(menu)?.NeedsUpdate(menu);
        MacMenu.Opened = menu => ControllerOf(menu)?.WillOpen(menu, RemoveInjectedFullScreenItems);
        MacMenu.Closed = menu => ControllerOf(menu)?.DidClose(menu, MacMenu.Supermenu(menu));
    }

    private static MacMenuController? ControllerOf(nint menu)
    {
        var top = menu;
        for (var up = MacMenu.Supermenu(top); up != 0; up = MacMenu.Supermenu(top)) top = up;
        if (s_barController?.NativeMenu == top) return s_barController;
        if (s_dockController?.NativeMenu == top) return s_dockController;
        return s_popupControllers.Find(c => c.NativeMenu == top);
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

    private static ulong MaskOf(AcceleratorModifiers modifiers) => MaskOfMenu(modifiers.ForMenus());

    private static ulong MaskOfMenu(AcceleratorModifiers modifiers)
        => (modifiers.HasFlag(AcceleratorModifiers.Command) ? MacMenu.CommandKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Shift) ? MacMenu.ShiftKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Alt) ? MacMenu.OptionKeyMask : 0)
            | (modifiers.HasFlag(AcceleratorModifiers.Control) ? MacMenu.ControlKeyMask : 0);

    private static void OnChosen(nint tag)
    {
        var item = s_shown?.FindByCommandId((int)tag) ?? s_dock?.FindByCommandId((int)tag);
        // Command ids are unique across menus, so the first popup menu that has one owns it.
        foreach (var c in s_popupControllers)
        {
            if (item is not null) break;
            item = c.Menu.FindByCommandId((int)tag);
        }
        if (item is null) return;
        var (flags, keyDown) = MacMenu.CurrentEvent();
        item.Activate(BrowserWindow.GetFocusedWindow(), KeyboardEvent.FromMacModifiers(flags, keyDown));
    }
}

/// <summary>What Electron's <c>applyStateToMenuItem</c> writes to an NSMenuItem from its model.</summary>
/// <remarks><see cref="Checked"/> applies to any item type, as Electron's <c>IsItemCheckedAt</c> reads <c>checked</c>
/// whatever the type. <see cref="RoleSelector"/> is the role's AppKit action, set again after the image is cleared.</remarks>
internal readonly record struct MacMenuItemState(bool Enabled, bool Hidden, bool Checked, string Title, string? RoleSelector)
{
    /// <summary>Electron's <c>useDefaultAccelerator</c>: YES for the menu bar, NO for the Dock and tray menus, where a
    /// role shows only an accelerator set on the item.</summary>
    internal static string? ShownAccelerator(MenuItem item, bool inMenuBar) => inMenuBar ? item.Accelerator : item.ExplicitAccelerator;

    /// <summary>The state for <paramref name="item"/>, or null for the items Electron's NSMenuItem carries no model for
    /// (separators, the Services submenu), which it leaves alone.</summary>
    internal static MacMenuItemState? Of(MenuItem item)
        => item.Type == MenuItemType.Separator || item.Role == MenuItemRole.Services ? null
            : new(item.Enabled, !item.Visible, item.Checked, MenuLabels.WithoutMnemonics(item.Label), MenuRoles.MacSelector(item.Role));
}

/// <summary>The NSMenu calls <see cref="MacMenuController"/> makes; a seam for tests.</summary>
internal interface IMacMenuTree
{
    int ItemCount(nint menu);

    nint ItemAt(nint menu, int index);

    /// <summary>The item's tag: the <see cref="MenuItem.CommandId"/> Oriel built it from, or 0 for AppKit's own items.</summary>
    int Tag(nint item);

    nint Submenu(nint item);

    void Apply(nint item, MacMenuItemState state);
}

[SupportedOSPlatform("macos")]
internal sealed class MacMenuTree : IMacMenuTree
{
    internal static readonly MacMenuTree Instance = new();

    public int ItemCount(nint menu) => MacMenu.ItemCount(menu);

    public nint ItemAt(nint menu, int index) => MacMenu.ItemAt(menu, index);

    public int Tag(nint item) => (int)MacMenu.Tag(item);

    public nint Submenu(nint item) => MacMenu.Submenu(item);

    public void Apply(nint item, MacMenuItemState state)
    {
        MacMenu.SetEnabled(item, state.Enabled);
        MacMenu.SetHidden(item, state.Hidden);
        MacMenu.SetChecked(item, state.Checked);
        MacMenu.SetTitle(item, state.Title);
        MacMenu.ClearImage(item, state.RoleSelector);
    }
}

/// <summary>The menu events and state refresh of Electron's <c>ElectronMenuController</c> for a menu without a popup
/// close callback (the application menu, the Dock menu): every NSMenu that opens under <see cref="NativeMenu"/> raises
/// <c>menu-will-show</c> on <see cref="Menu"/>, and <c>menu-will-close</c> comes when <see cref="NativeMenu"/> or one of
/// its direct submenus closes. Main thread only.</summary>
internal sealed class MacMenuController(Menu menu, nint nativeMenu, IMacMenuTree tree)
{
    private bool _isMenuOpen;

    internal Menu Menu { get; } = menu;

    internal nint NativeMenu { get; } = nativeMenu;

    /// <summary>Electron's <c>isMenuOpen</c>: between <c>menuWillOpen:</c> and the top level's close.</summary>
    internal bool IsMenuOpen => _isMenuOpen;

    /// <summary>Electron's <c>cancel</c>: an open menu stops tracking and raises <c>menu-will-close</c> at once.</summary>
    internal void Cancel(Action<nint> cancelTracking)
    {
        if (!_isMenuOpen) return;
        cancelTracking(NativeMenu);
        _isMenuOpen = false;
        Menu.OnMenuWillClose();
    }

    /// <summary><c>menuNeedsUpdate:</c>, for <paramref name="menu"/> or any submenu: AppKit sends it before the menu
    /// shows and before it matches a key equivalent in it.</summary>
    internal void NeedsUpdate(nint menu) => RefreshTree(menu);

    /// <summary>Electron's <c>refreshMenuTree</c>: re-applies each item's state from its <see cref="MenuItem"/>, depth first.</summary>
    internal void RefreshTree(nint menu)
    {
        var count = tree.ItemCount(menu);
        for (var i = 0; i < count; i++)
        {
            var item = tree.ItemAt(menu, i);
            if (Menu.FindByCommandId(tree.Tag(item)) is { } model && MacMenuItemState.Of(model) is { } state) tree.Apply(item, state);
            if (tree.Submenu(item) is var submenu && submenu != 0) RefreshTree(submenu);
        }
    }

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
        var endsMenu = menu == NativeMenu || supermenu == NativeMenu;
        RefreshTree(menu);
        // A deeper submenu closes before the menu above it; only the top level ends the menu.
        if (!endsMenu) return;
        _isMenuOpen = false;
        Menu.OnMenuWillClose();
    }
}
