namespace Oriel.Tests;

public sealed class WindowEnabledStateTests
{
    [Fact]
    public void StartsEnabled() => Assert.True(new WindowEnabledState().Enabled);

    [Fact]
    public void SetEnabled_ChangesTheStateAndReportsTheChange()
    {
        var state = new WindowEnabledState();
        Assert.True(state.SetEnabled(false));
        Assert.False(state.Enabled);
        Assert.False(state.SetEnabled(false));
        Assert.True(state.SetEnabled(true));
        Assert.True(state.Enabled);
    }

    [Fact]
    public void AModalChildDisablesUntilItGoes()
    {
        var state = new WindowEnabledState();
        Assert.True(state.IncrementChildModals());
        Assert.False(state.Enabled);
        Assert.True(state.DecrementChildModals());
        Assert.True(state.Enabled);
    }

    [Fact]
    public void TwoModalChildrenKeepItDisabledUntilBothGo()
    {
        var state = new WindowEnabledState();
        state.IncrementChildModals();
        Assert.False(state.IncrementChildModals());
        Assert.False(state.DecrementChildModals());
        Assert.False(state.Enabled);
        Assert.True(state.DecrementChildModals());
        Assert.True(state.Enabled);
    }

    [Fact]
    public void SetEnabledTrueDoesNotOverrideAShownModal()
    {
        var state = new WindowEnabledState();
        state.SetEnabled(false);
        state.IncrementChildModals();
        Assert.False(state.SetEnabled(true));
        Assert.False(state.Enabled);
        Assert.True(state.DecrementChildModals());
        Assert.True(state.Enabled);
    }

    [Fact]
    public void AModalClosingDoesNotOverrideSetEnabledFalse()
    {
        var state = new WindowEnabledState();
        state.IncrementChildModals();
        Assert.False(state.SetEnabled(false));
        Assert.False(state.DecrementChildModals());
        Assert.False(state.Enabled);
    }

    // Electron decrements on hide and again on destroy; the count stops at zero.
    [Fact]
    public void ExtraDecrementsDoNotUnderflow()
    {
        var state = new WindowEnabledState();
        Assert.False(state.DecrementChildModals());
        Assert.True(state.IncrementChildModals());
        Assert.False(state.Enabled);
    }
}
