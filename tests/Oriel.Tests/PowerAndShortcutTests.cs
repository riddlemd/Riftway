namespace Oriel.Tests;

public sealed class PowerAndShortcutTests
{
    // The internal enums are taken boxed: a public test method cannot take an internal type.
    [Theory]
    [InlineData(0x0004u, null, null, PowerEvent.Suspend)]
    [InlineData(0x0012u, null, null, PowerEvent.Resume)]
    [InlineData(0x0007u, null, null, PowerEvent.None)]
    [InlineData(0x000Au, false, true, PowerEvent.OnBattery)]
    [InlineData(0x000Au, true, false, PowerEvent.OnAc)]
    [InlineData(0x000Au, null, false, PowerEvent.OnAc)]
    [InlineData(0x000Au, false, false, PowerEvent.None)]
    [InlineData(0x000Au, true, true, PowerEvent.None)]
    [InlineData(0x000Au, true, null, PowerEvent.None)]
    [InlineData(0x0004u, false, true, PowerEvent.Suspend)]
    public void PowerEventOf_FollowsChromium(uint pbt, bool? wasOnBattery, bool? onBattery, object e)
        => Assert.Equal(e, PowerRules.PowerEventOf(pbt, wasOnBattery, onBattery));

    [Theory]
    [InlineData(0x7u, PowerEvent.Lock)]
    [InlineData(0x8u, PowerEvent.Unlock)]
    [InlineData(0x5u, PowerEvent.None)]
    [InlineData(0x6u, PowerEvent.None)]
    public void SessionEventOf_LockAndUnlock(uint wts, object e) => Assert.Equal(e, PowerRules.SessionEventOf(wts));

    [Theory]
    [InlineData(0, 60, false, SystemIdleState.Active)]
    [InlineData(59, 60, false, SystemIdleState.Active)]
    [InlineData(60, 60, false, SystemIdleState.Idle)]
    [InlineData(500, 60, false, SystemIdleState.Idle)]
    [InlineData(0, 60, true, SystemIdleState.Locked)]
    [InlineData(500, 60, true, SystemIdleState.Locked)]
    public void IdleState_LockedThenIdleThenActive(int idle, int threshold, bool locked, SystemIdleState state)
        => Assert.Equal(state, PowerRules.IdleState(idle, threshold, locked));

    [Fact]
    public void Modifiers_AreWin32ModFlags()
    {
        Assert.Equal(0x1u, GlobalShortcuts.Modifiers(AcceleratorModifiers.Alt));
        Assert.Equal(0x2u, GlobalShortcuts.Modifiers(AcceleratorModifiers.Control));
        Assert.Equal(0x4u, GlobalShortcuts.Modifiers(AcceleratorModifiers.Shift));
        Assert.Equal(0x8u, GlobalShortcuts.Modifiers(AcceleratorModifiers.Command));
        Assert.Equal(0xFu, GlobalShortcuts.Modifiers(AcceleratorModifiers.Alt | AcceleratorModifiers.Control | AcceleratorModifiers.Shift | AcceleratorModifiers.Command));
        Assert.Equal(0u, GlobalShortcuts.Modifiers(AcceleratorModifiers.None));
    }

    [Fact]
    public void Key_IsTheParsedCombination_WithNoRepeat()
    {
        var key = GlobalShortcuts.Key("Ctrl+Alt+Shift+F9");
        Assert.Equal(0x4007u, key.Modifiers);
        Assert.Equal(0x78u, key.VirtualKey);
        Assert.Equal(key, GlobalShortcuts.Key("Shift+Control+Alt+F9"));
        Assert.NotEqual(key, GlobalShortcuts.Key("Ctrl+Alt+F9"));
        Assert.Equal(new HotKey(0x4002, 0x41), GlobalShortcuts.Key("CmdOrCtrl+A"));
        Assert.Throws<ArgumentException>(() => GlobalShortcuts.Key("Ctrl+Nope"));
    }

    [Fact]
    public void Blockers_MapToPowerRequestTypes()
    {
        Assert.Equal(0, PowerSaveBlockers.RequestType(PowerSaveBlockerType.PreventDisplaySleep));
        Assert.Equal(1, PowerSaveBlockers.RequestType(PowerSaveBlockerType.PreventAppSuspension));
        Assert.Equal("Oriel: prevent-display-sleep", PowerSaveBlockers.Reason(PowerSaveBlockerType.PreventDisplaySleep));
        Assert.Equal("Oriel: prevent-app-suspension", PowerSaveBlockers.Reason(PowerSaveBlockerType.PreventAppSuspension));
    }
}
