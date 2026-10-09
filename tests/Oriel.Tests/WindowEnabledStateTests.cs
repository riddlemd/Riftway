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

    // Windows reads the app's request alone; Chromium disables a modal's parent there itself.
    [Fact]
    public void RequestedFollowsSetEnabledOnly()
    {
        var state = new WindowEnabledState();
        state.IncrementChildModals();
        Assert.True(state.Requested);
        state.SetEnabled(false);
        Assert.False(state.Requested);
        state.DecrementChildModals();
        Assert.False(state.Requested);
        state.SetEnabled(true);
        Assert.True(state.Requested);
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

    [Fact]
    public void UnderTheStockCover_KeysAreDroppedOnlyWhileDisabled()
    {
        var state = new WindowEnabledState();
        Assert.False(state.DropsKeys);
        state.SetEnabled(false);
        Assert.True(state.DropsKeys);
        state.SetEnabled(true);
        Assert.False(state.DropsKeys);
    }

    // Patch 0002's DisableEventListening keeps keys from the window, as in Electron.
    [Fact]
    public void BlockedInViews_KeysAreNotDropped()
    {
        var state = new WindowEnabledState { BlockedInViews = true };
        state.SetEnabled(false);
        Assert.False(state.DropsKeys);
    }
}
