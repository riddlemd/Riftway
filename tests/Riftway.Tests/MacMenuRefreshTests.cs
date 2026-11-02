namespace Riftway.Tests;

/// <summary>An NSMenu tree for <see cref="MacMenuController"/>: menus and items are numbers; applied states are recorded.</summary>
internal sealed class FakeMenuTree : IMacMenuTree
{
    private readonly Dictionary<nint, List<nint>> _menus = [];
    private readonly Dictionary<nint, (int Tag, nint Submenu)> _items = [];
    private nint _next = 100;

    internal Dictionary<nint, MacMenuItemState> Applied { get; } = [];

    internal nint Menu(params nint[] items)
    {
        var menu = _next++;
        _menus[menu] = [.. items];
        return menu;
    }

    internal nint Item(int tag, nint submenu = 0)
    {
        var item = _next++;
        _items[item] = (tag, submenu);
        return item;
    }

    public int ItemCount(nint menu) => _menus.TryGetValue(menu, out var items) ? items.Count : 0;

    public nint ItemAt(nint menu, int index) => _menus[menu][index];

    public int Tag(nint item) => _items[item].Tag;

    public nint Submenu(nint item) => _items[item].Submenu;

    public void Apply(nint item, MacMenuItemState state) => Applied[item] = state;
}

// Electron v44's applyStateToMenuItem / refreshMenuTree (shell/browser/ui/cocoa/electron_menu_controller.mm).
public sealed class MacMenuRefreshTests
{
    [Fact]
    public void State_MapsEnabledVisibleCheckedAndTheLabelWithoutMnemonics()
    {
        var item = new MenuItem { Label = "&Save && Close", Enabled = false, Visible = false, Checked = true };

        Assert.Equal(new MacMenuItemState(false, true, true, "Save & Close", null), MacMenuItemState.Of(item));
        Assert.Equal(new MacMenuItemState(true, false, false, "Plain", null), MacMenuItemState.Of(new MenuItem { Label = "Plain" }));
    }

    [Fact]
    public void State_CarriesTheIcon_TheSublabelWithoutMnemonics_AndTheToolTip()
    {
        var icon = NativeImage.CreateFromBitmap([1, 2, 3, 255], new NativeImageBitmapOptions { Width = 1, Height = 1 });
        var state = MacMenuItemState.Of(new MenuItem { Label = "Open", Icon = icon, Sublabel = "&Recent && new", ToolTip = "Opens a file" })!.Value;
        Assert.Same(icon, state.Icon);
        Assert.Equal(("Recent & new", "Opens a file"), (state.Subtitle, state.ToolTip));
    }

    [Fact]
    public void State_HasNoIconForAnEmptyImage_AndEmptyTextsByDefault()
    {
        var state = MacMenuItemState.Of(new MenuItem { Label = "Open", Icon = NativeImage.CreateEmpty() })!.Value;
        Assert.Null(state.Icon);
        Assert.Equal(("", ""), (state.Subtitle, state.ToolTip));
    }

    [Fact]
    public void ChangingTheIconSublabelOrToolTipTellsTheMenus()
    {
        var menu = new Menu();
        var item = new MenuItem { Label = "Open" };
        menu.Append(item);
        var changes = 0;
        menu.Changed += () => changes++;
        item.Sublabel = "x";
        item.ToolTip = "y";
        item.Icon = NativeImage.CreateEmpty();
        Assert.Equal(3, changes);
    }

    [Fact]
    public void State_CarriesTheRoleSelector_AndTheRoleLabel()
    {
        var state = MacMenuItemState.Of(new MenuItem { Role = MenuItemRole.Copy });

        Assert.Equal("copy:", state?.RoleSelector);
        Assert.Equal("Copy", state?.Title);
    }

    [Fact]
    public void State_IsNullForSeparatorsAndTheServicesMenu()
    {
        Assert.Null(MacMenuItemState.Of(new MenuItem { Type = MenuItemType.Separator }));
        Assert.Null(MacMenuItemState.Of(new MenuItem { Role = MenuItemRole.Services }));
    }

    [Fact]
    public void NeedsUpdate_AppliesChangesMadeSinceTheBuild_ThroughSubmenus_AndLeavesAppKitItems()
    {
        var deep = new MenuItem { Label = "Deep" };
        var sub = new MenuItem { Label = "Sub", Submenu = new Menu { deep } };
        var top = new MenuItem { Label = "Top", Type = MenuItemType.Checkbox };
        var root = new Menu { top, sub };
        var tree = new FakeMenuTree();
        var nsDeep = tree.Item(deep.CommandId);
        var nsSub = tree.Item(sub.CommandId, tree.Menu(nsDeep));
        var nsTop = tree.Item(top.CommandId);
        var appKit = tree.Item(0);
        var bar = tree.Menu(nsTop, nsSub, appKit);
        var controller = new MacMenuController(root, bar, tree);

        top.Checked = true;
        deep.Enabled = false;
        sub.Visible = false;
        controller.NeedsUpdate(bar);

        Assert.True(tree.Applied[nsTop].Checked);
        Assert.False(tree.Applied[nsDeep].Enabled);
        Assert.True(tree.Applied[nsSub].Hidden);
        Assert.False(tree.Applied.ContainsKey(appKit));
    }

    [Fact]
    public void DidClose_RefreshesTheClosingSubmenu_EvenWhenItDoesNotEndTheMenu()
    {
        var item = new MenuItem { Label = "Doc" };
        var root = new Menu { new MenuItem { Label = "File", Submenu = new Menu { new MenuItem { Label = "Recent", Submenu = new Menu { item } } } } };
        var tree = new FakeMenuTree();
        var nsItem = tree.Item(item.CommandId);
        var recent = tree.Menu(nsItem);
        var controller = new MacMenuController(root, 1, tree);

        controller.WillOpen(recent, _ => { });
        item.Label = "Renamed";
        controller.DidClose(recent, 2);

        Assert.Equal("Renamed", tree.Applied[nsItem].Title);
    }

    [Fact]
    public void DidClose_WhenNotOpen_RefreshesNothing()
    {
        var item = new MenuItem { Label = "A" };
        var tree = new FakeMenuTree();
        var top = tree.Menu(tree.Item(item.CommandId));
        var controller = new MacMenuController(new Menu { item }, top, tree);

        controller.DidClose(top, 0);

        Assert.Empty(tree.Applied);
    }

    [Fact]
    public void DidClose_ShowsMenuWillShowsRadioFixUp()
    {
        var a = new MenuItem { Label = "a", Type = MenuItemType.Radio };
        var b = new MenuItem { Label = "b", Type = MenuItemType.Radio };
        var root = new Menu { a, b };
        var tree = new FakeMenuTree();
        var nsA = tree.Item(a.CommandId);
        var top = tree.Menu(nsA, tree.Item(b.CommandId));
        var controller = new MacMenuController(root, top, tree);

        controller.NeedsUpdate(top);
        Assert.False(tree.Applied[nsA].Checked);
        controller.WillOpen(top, _ => { });
        controller.DidClose(top, 0);

        Assert.True(tree.Applied[nsA].Checked);
    }
}
