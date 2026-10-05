namespace Oriel.Tests;

public sealed class MenuTests
{
    [Fact]
    public void Type_IsSubmenuWhenASubmenuIsSet()
    {
        Assert.Equal(MenuItemType.Normal, new MenuItem().Type);
        Assert.Equal(MenuItemType.Submenu, new MenuItem { Submenu = [] }.Type);
        Assert.Equal(MenuItemType.Checkbox, new MenuItem { Type = MenuItemType.Checkbox, Submenu = [] }.Type);
    }

    [Fact]
    public void Activate_TogglesACheckbox()
    {
        var item = new MenuItem { Type = MenuItemType.Checkbox };
        item.Activate(null);
        Assert.True(item.Checked);
        item.Activate(null);
        Assert.False(item.Checked);
    }

    [Fact]
    public void Activate_ChecksOnlyTheClickedRadioOfItsAdjacentGroup()
    {
        var a = new MenuItem { Type = MenuItemType.Radio, Checked = true };
        var b = new MenuItem { Type = MenuItemType.Radio };
        var c = new MenuItem { Type = MenuItemType.Radio };
        var other = new MenuItem { Type = MenuItemType.Radio, Checked = true };
        _ = new Menu { a, b, c, new MenuItem { Type = MenuItemType.Separator }, other };

        b.Activate(null);

        Assert.Equal([false, true, false, true], new[] { a.Checked, b.Checked, c.Checked, other.Checked });

        a.Activate(null);

        Assert.Equal([true, false, false, true], new[] { a.Checked, b.Checked, c.Checked, other.Checked });
    }

    [Fact]
    public void Activate_RunsClickWithTheItemAndWindow()
    {
        MenuItem? clicked = null;
        var item = new MenuItem { Click = (i, _, _) => clicked = i };
        item.Activate(null);
        Assert.Same(item, clicked);
    }

    [Fact]
    public void Activate_RunsTheRoleInsteadOfClick()
    {
        var clicked = false;
        new MenuItem { Role = MenuItemRole.Copy, Click = (_, _, _) => clicked = true }.Activate(null);
        Assert.False(clicked);
    }

    [Fact]
    public void Activate_RunsClickForARoleWithNoActionHere()
    {
        var clicked = false;
        new MenuItem { Role = MenuItemRole.About, Click = (_, _, _) => clicked = true }.Activate(null);
        Assert.True(clicked);
    }

    [Fact]
    public void Activate_DoesNothingWhenDisabled()
    {
        var clicked = false;
        var item = new MenuItem { Type = MenuItemType.Checkbox, Enabled = false, Click = (_, _, _) => clicked = true };
        item.Activate(null);
        Assert.False(clicked);
        Assert.False(item.Checked);
    }

    [Fact]
    public void RoleDefaults_ApplyUntilOverridden()
    {
        var item = new MenuItem { Role = MenuItemRole.Copy };
        Assert.Equal("Copy", item.Label);
        Assert.Equal("CmdOrCtrl+C", item.Accelerator);
        item.Label = "Duplicate";
        item.Accelerator = "CmdOrCtrl+D";
        Assert.Equal("Duplicate", item.Label);
        Assert.Equal("CmdOrCtrl+D", item.Accelerator);
    }

    [Fact]
    public void RoleMenus_SupplyTheirOwnSubmenuOnce()
    {
        var edit = new MenuItem { Role = MenuItemRole.EditMenu };
        Assert.Equal(MenuItemType.Submenu, edit.Type);
        Assert.Contains(edit.Submenu!.Items, i => i.Role == MenuItemRole.Paste);
        Assert.Same(edit.Submenu, edit.Submenu);
    }

    // Electron v44's role table (electron_api_menu_roles.cc), as this OS sees it.
    [Fact]
    public void RoleLabels_AreElectrons()
    {
        string? Label(MenuItemRole role) => new MenuItem { Role = role }.Label;
        Assert.Equal(OperatingSystem.IsMacOS() ? "Close Window" : "Close", Label(MenuItemRole.Close));
        if (OperatingSystem.IsMacOS()) Assert.StartsWith("Quit ", Label(MenuItemRole.Quit));
        else Assert.Equal(OperatingSystem.IsWindows() ? "Exit" : "Quit", Label(MenuItemRole.Quit));
        Assert.Equal(["File", "Edit", "View", "Window", "Help"], new[] { MenuItemRole.FileMenu, MenuItemRole.EditMenu, MenuItemRole.ViewMenu, MenuItemRole.WindowMenu, MenuItemRole.Help }.Select(Label));
        Assert.Equal("Check Spelling While Typing", Label(MenuItemRole.ToggleSpellChecker));
        Assert.Equal(["Start Speaking", "Stop Speaking", "Show Substitutions", "Smart Quotes", "Smart Dashes", "Text Replacement", "Clear Menu"],
            new[] { MenuItemRole.StartSpeaking, MenuItemRole.StopSpeaking, MenuItemRole.ShowSubstitutions, MenuItemRole.ToggleSmartQuotes,
                MenuItemRole.ToggleSmartDashes, MenuItemRole.ToggleTextReplacement, MenuItemRole.ClearRecentDocuments }.Select(Label));
    }

    [Fact]
    public void Redo_IsControlYOnWindowsOnly()
        => Assert.Equal(OperatingSystem.IsWindows() ? "Control+Y" : "Shift+CmdOrCtrl+Z", new MenuItem { Role = MenuItemRole.Redo }.Accelerator);

    [Fact]
    public void EditMenu_OnMacHasSubstitutionsAndSpeech()
    {
        var items = new MenuItem { Role = MenuItemRole.EditMenu }.Submenu!.Items;
        if (!OperatingSystem.IsMacOS())
        {
            Assert.DoesNotContain(items, i => i.Label is "Substitutions" or "Speech");
            return;
        }
        var substitutions = items.Single(i => i.Label == "Substitutions").Submenu!.Items.Select(i => i.Role);
        Assert.Equal([MenuItemRole.ShowSubstitutions, MenuItemRole.None, MenuItemRole.ToggleSmartQuotes, MenuItemRole.ToggleSmartDashes, MenuItemRole.ToggleTextReplacement], substitutions);
        Assert.Equal([MenuItemRole.StartSpeaking, MenuItemRole.StopSpeaking], items.Single(i => i.Label == "Speech").Submenu!.Items.Select(i => i.Role));
        Assert.Equal(MenuItemType.Separator, items[^3].Type);
    }

    [Theory]
    [InlineData(MenuItemRole.StartSpeaking, "startSpeaking:")]
    [InlineData(MenuItemRole.StopSpeaking, "stopSpeaking:")]
    [InlineData(MenuItemRole.ShowSubstitutions, "orderFrontSubstitutionsPanel:")]
    [InlineData(MenuItemRole.ToggleSmartQuotes, "toggleAutomaticQuoteSubstitution:")]
    [InlineData(MenuItemRole.ToggleSmartDashes, "toggleAutomaticDashSubstitution:")]
    [InlineData(MenuItemRole.ToggleTextReplacement, "toggleAutomaticTextReplacement:")]
    [InlineData(MenuItemRole.ToggleTabBar, "toggleTabBar:")]
    [InlineData(MenuItemRole.SelectNextTab, "selectNextTab:")]
    [InlineData(MenuItemRole.SelectPreviousTab, "selectPreviousTab:")]
    [InlineData(MenuItemRole.ShowAllTabs, "toggleTabOverview:")]
    [InlineData(MenuItemRole.MergeAllWindows, "mergeAllWindows:")]
    [InlineData(MenuItemRole.MoveTabToNewWindow, "moveTabToNewWindow:")]
    [InlineData(MenuItemRole.ClearRecentDocuments, "clearRecentDocuments:")]
    public void MacRoles_UseElectronsSelectors(MenuItemRole role, string selector) => Assert.Equal(selector, MenuRoles.MacSelector(role));

    [Fact]
    public void Accelerator_RejectsAnInvalidValue()
        => Assert.ThrowsAny<ArgumentException>(() => new MenuItem { Accelerator = "Ctrl+Nope" });

    [Fact]
    public void GetMenuItemById_SearchesSubmenus()
    {
        var deep = new MenuItem { Id = "deep" };
        var menu = new Menu { new MenuItem { Id = "top" }, new MenuItem { Submenu = [new MenuItem { Submenu = [deep] }] } };
        Assert.Same(deep, menu.GetMenuItemById("deep"));
        Assert.Null(menu.GetMenuItemById("missing"));
    }

    [Fact]
    public void FindByCommandId_SearchesSubmenus()
    {
        var deep = new MenuItem();
        var menu = new Menu { new MenuItem(), new MenuItem { Submenu = [deep] } };
        Assert.Same(deep, menu.FindByCommandId(deep.CommandId));
        Assert.Null(menu.FindByCommandId(-1));
    }

    [Fact]
    public void Insert_PlacesTheItemAndRejectsOneAlreadyInAMenu()
    {
        var a = new MenuItem();
        var b = new MenuItem();
        var menu = new Menu { a };
        menu.Insert(0, b);
        Assert.Equal([b, a], menu.Items);
        Assert.Throws<InvalidOperationException>(() => new Menu().Append(a));
        Assert.Throws<ArgumentOutOfRangeException>(() => menu.Insert(3, new MenuItem()));
    }

    [Fact]
    public void Submenu_CannotBelongToTwoItems()
    {
        Menu shared = [];
        _ = new MenuItem { Submenu = shared };
        Assert.Throws<InvalidOperationException>(() => new MenuItem { Submenu = shared });
    }

    [Fact]
    public void Changed_RisesFromSubmenuItemsToTheRoot()
    {
        var leaf = new MenuItem();
        var root = new Menu { new MenuItem { Submenu = [leaf] } };
        var raised = 0;
        root.Changed += () => raised++;

        leaf.Enabled = false;
        leaf.Enabled = false;
        leaf.Label = "x";

        Assert.Equal(2, raised);
    }

    [Theory]
    [InlineData("&File", "File")]
    [InlineData("Save && Quit", "Save & Quit")]
    [InlineData("E&xit", "Exit")]
    [InlineData("Plain", "Plain")]
    [InlineData("Trailing&", "Trailing")]
    public void WithoutMnemonics_StripsMarkers(string label, string text) => Assert.Equal(text, MenuLabels.WithoutMnemonics(label));

    [Theory]
    [InlineData("&File", 'F')]
    [InlineData("E&xit", 'X')]
    [InlineData("Save && &Quit", 'Q')]
    [InlineData("A && B", null)]
    [InlineData("&1st", null)]
    [InlineData("None", null)]
    public void MnemonicOf_FindsTheMarkedLetter(string label, char? letter) => Assert.Equal(letter, WindowMenuBar.MnemonicOf(label));
}

public sealed class KeyboardEventTests
{
    [Fact]
    public void FromCefFlags_ReadsEachModifier()
    {
        Assert.Equal(new KeyboardEvent { ShiftKey = true, CtrlKey = true, AltKey = true, MetaKey = true },
            KeyboardEvent.FromCefFlags(Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_SHIFT_DOWN | Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_CONTROL_DOWN
                | Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_ALT_DOWN | Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_COMMAND_DOWN));
        Assert.Equal(new KeyboardEvent { AltKey = true }, KeyboardEvent.FromCefFlags(Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_ALT_DOWN));
        Assert.Equal(new KeyboardEvent(), KeyboardEvent.FromCefFlags(Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_LEFT_MOUSE_BUTTON));
    }

    [Theory]
    [InlineData(1UL << 17, true, false, false, false)]
    [InlineData(1UL << 18, false, true, false, false)]
    [InlineData(1UL << 19, false, false, true, false)]
    [InlineData(1UL << 20, false, false, false, true)]
    public void FromMacModifiers_ReadsEachModifier(ulong flags, bool shift, bool ctrl, bool alt, bool meta)
        => Assert.Equal(new KeyboardEvent { ShiftKey = shift, CtrlKey = ctrl, AltKey = alt, MetaKey = meta, TriggeredByAccelerator = true },
            KeyboardEvent.FromMacModifiers(flags, keyDown: true));

    [Fact]
    public void FromMacModifiers_AClickIsNotAnAccelerator() => Assert.False(KeyboardEvent.FromMacModifiers(0, keyDown: false).TriggeredByAccelerator);

    [Fact]
    public void ForAccelerator_HoldsItsModifiers()
    {
        var keys = KeyboardEvent.ForAccelerator("CmdOrCtrl+Alt+K");
        Assert.True(keys.TriggeredByAccelerator && keys.AltKey && !keys.ShiftKey);
        Assert.Equal(OperatingSystem.IsMacOS(), keys.MetaKey);
        Assert.Equal(!OperatingSystem.IsMacOS(), keys.CtrlKey);
    }
}
