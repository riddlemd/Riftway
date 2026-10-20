namespace Oriel.Tests;

[Collection(nameof(MenuRoleStateCollection))]
public sealed class MenuRoleStateTests : IDisposable
{
    private readonly Func<(bool, bool, bool)?> _savedWindow = MenuItem.FocusedWindowAbilities;
    private readonly Func<bool> _savedSpell = MenuItem.SpellCheckerEnabled;

    public void Dispose()
    {
        MenuItem.FocusedWindowAbilities = _savedWindow;
        MenuItem.SpellCheckerEnabled = _savedSpell;
    }

    [Theory]
    [InlineData(MenuItemRole.Minimize, false, true, true)]
    [InlineData(MenuItemRole.ToggleFullScreen, true, false, true)]
    [InlineData(MenuItemRole.Close, true, true, false)]
    public void WindowRolesFollowWhatTheFocusedWindowAllows(MenuItemRole role, bool minimizable, bool fullScreenable, bool closable)
    {
        var item = new MenuItem { Role = role };
        MenuItem.FocusedWindowAbilities = () => (minimizable, fullScreenable, closable);
        Assert.False(item.CommandEnabled);
        MenuItem.FocusedWindowAbilities = () => (true, true, true);
        item.Enabled = false;
        Assert.True(item.CommandEnabled);
    }

    [Fact]
    public void WithoutAFocusedWindowOrForOtherRolesEnabledDecides()
    {
        MenuItem.FocusedWindowAbilities = () => null;
        Assert.False(new MenuItem { Role = MenuItemRole.Close, Enabled = false }.CommandEnabled);
        Assert.True(new MenuItem { Role = MenuItemRole.Close }.CommandEnabled);
        MenuItem.FocusedWindowAbilities = () => (false, false, false);
        Assert.True(new MenuItem { Role = MenuItemRole.Copy }.CommandEnabled);
        Assert.False(new MenuItem { Label = "x", Enabled = false }.CommandEnabled);
    }

    [Fact]
    public void AWindowRoleItemTheWindowRefusesDoesNotRun()
    {
        // A checkbox flips when it runs, which shows whether the activation got through.
        var item = new MenuItem { Role = MenuItemRole.Minimize, Type = MenuItemType.Checkbox };
        MenuItem.FocusedWindowAbilities = () => (false, true, true);
        item.Activate(null);
        Assert.False(item.Checked);
        MenuItem.FocusedWindowAbilities = () => (true, true, true);
        item.Enabled = false;
        item.Activate(null);
        Assert.True(item.Checked);
    }

    [Fact]
    public void TheSpellCheckerRoleIsACheckboxShowingTheSessionsSetting()
    {
        var item = new MenuItem { Role = MenuItemRole.ToggleSpellChecker };
        Assert.Equal(MenuItemType.Checkbox, item.Type);
        MenuItem.SpellCheckerEnabled = () => true;
        Assert.True(item.CheckStatus);
        Assert.False(item.Checked);
        MenuItem.SpellCheckerEnabled = () => false;
        item.Checked = true;
        Assert.False(item.CheckStatus);
        Assert.Equal(MenuItemType.Radio, new MenuItem { Role = MenuItemRole.ToggleSpellChecker, Type = MenuItemType.Radio }.Type);
    }

    [Fact]
    public void OtherItemsShowTheirOwnCheck()
    {
        MenuItem.SpellCheckerEnabled = () => throw new InvalidOperationException("not asked");
        Assert.True(new MenuItem { Type = MenuItemType.Checkbox, Checked = true }.CheckStatus);
        Assert.Equal(MenuItemType.Normal, new MenuItem { Role = MenuItemRole.Copy }.Type);
    }

    [Fact]
    public void AClickDoesNotFlipTheSpellCheckersOwnCheck()
    {
        MenuItem.FocusedWindowAbilities = () => null;
        var spell = new MenuItem { Role = MenuItemRole.ToggleSpellChecker };
        spell.Run(null);
        Assert.False(spell.Checked);
        var box = new MenuItem { Type = MenuItemType.Checkbox };
        box.Run(null);
        Assert.True(box.Checked);
    }

    [Fact]
    public void TheMacMenuShowsTheEffectiveState()
    {
        MenuItem.FocusedWindowAbilities = () => (true, true, false);
        MenuItem.SpellCheckerEnabled = () => true;
        var close = MacMenuItemState.Of(new MenuItem { Role = MenuItemRole.Close })!.Value;
        var spell = MacMenuItemState.Of(new MenuItem { Role = MenuItemRole.ToggleSpellChecker })!.Value;
        Assert.False(close.Enabled);
        Assert.True(spell.Checked);
    }
}

[CollectionDefinition(nameof(MenuRoleStateCollection), DisableParallelization = true)]
public sealed class MenuRoleStateCollection;
