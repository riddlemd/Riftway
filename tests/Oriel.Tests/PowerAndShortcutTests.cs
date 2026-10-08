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
    public void Key_IsTheParsedCombination_WithNoRepeatFlag()
    {
        var key = GlobalShortcuts.Key("Ctrl+Alt+Shift+F9");
        Assert.Equal(0x7u, key.Modifiers);
        Assert.Equal(0x78u, key.VirtualKey);
        Assert.Equal(key, GlobalShortcuts.Key("Shift+Control+Alt+F9"));
        Assert.NotEqual(key, GlobalShortcuts.Key("Ctrl+Alt+F9"));
        // CmdOrCtrl follows the host: Command (MOD_WIN) when the tests run on macOS.
        Assert.Equal(new HotKey(OperatingSystem.IsMacOS() ? 0x8u : 0x2u, 0x41), GlobalShortcuts.Key("CmdOrCtrl+A"));
        Assert.Throws<ArgumentException>(() => GlobalShortcuts.Key("Ctrl+Nope"));
    }

    [Fact]
    public void Blockers_MapToChromiumsPowerRequests()
    {
        Assert.Equal([0], PowerSaveBlockers.RequestTypes(PowerSaveBlockerType.PreventDisplaySleep, true));
        Assert.Equal([0, 1], PowerSaveBlockers.RequestTypes(PowerSaveBlockerType.PreventDisplaySleep, false));
        Assert.Equal([3], PowerSaveBlockers.RequestTypes(PowerSaveBlockerType.PreventAppSuspension, true));
        Assert.Equal([3], PowerSaveBlockers.RequestTypes(PowerSaveBlockerType.PreventAppSuspension, false));
    }

    [Fact]
    public void Reason_IsTheProductNameThenTheAppName()
    {
        Assert.Equal("Product", PowerSaveBlockers.Reason("Product", "app"));
        Assert.Equal("app", PowerSaveBlockers.Reason("", "app"));
        Assert.Equal("app", PowerSaveBlockers.Reason(null, "app"));
        Assert.Equal("Oriel", PowerSaveBlockers.Reason(null, null));
    }

    [Fact]
    public void Effective_DisplayBeatsAppAndNoneIsNull()
    {
        const PowerSaveBlockerType app = PowerSaveBlockerType.PreventAppSuspension, display = PowerSaveBlockerType.PreventDisplaySleep;
        Assert.Null(PowerSaveBlockers.Effective([]));
        Assert.Equal(app, PowerSaveBlockers.Effective([app, app]));
        Assert.Equal(display, PowerSaveBlockers.Effective([display]));
        Assert.Equal(display, PowerSaveBlockers.Effective([app, display, app]));
    }

    [Fact]
    public void Throttling_IsTheMeanDistanceFromTheDeepestIdleState()
    {
        Assert.Equal(0f, PowerRules.Throttling([]));
        Assert.Equal(0f, PowerRules.Throttling([(2u, 2u), (3u, 3u)]));
        Assert.Equal(0.5f, PowerRules.Throttling([(2u, 1u)]));
        Assert.Equal(0.5f, PowerRules.Throttling([(2u, 0u), (2u, 2u)]));
        Assert.Equal(1f / 9, PowerRules.Throttling([(3u, 2u), (3u, 3u), (3u, 3u)]), 5);
        Assert.Equal(0f, PowerRules.Throttling([(0u, 0u)]));
    }

    private static int[] Run(SpeedLimitFilter filter, int count, int idleness, float throttling)
        => [.. Enumerable.Range(0, count).Select(_ => filter.Next(idleness, throttling))];

    [Fact]
    public void SpeedLimit_StaysAtMaxUntilTheWindowIsFull_ThenIsTheMean()
    {
        var filter = new SpeedLimitFilter();
        Assert.Equal([100, 100, 100, 100, 100, 100, 100, 100, 100, 50], Run(filter, 10, 10, 0.5f));
        // The window slides: nine 50s and a 70 average 52.
        Assert.Equal(52, filter.Next(10, 0.3f));
    }

    [Fact]
    public void SpeedLimit_RoundsUpAndTruncatesTheMean()
    {
        // ceil(100 * (1 - 0.333)) = 67; nine 67s and a 74 (throttling 0.265) sum to 677, which truncates to 67, not 68.
        var filter = new SpeedLimitFilter();
        Run(filter, 9, 10, 0.333f);
        Assert.Equal(67, filter.Next(10, 0.265f));
    }

    [Theory]
    [InlineData(91, 0.5f)]
    [InlineData(10, 0.09f)]
    public void SpeedLimit_IdleOrBarelyThrottledResetsTheWindow(int idleness, float throttling)
    {
        var filter = new SpeedLimitFilter();
        Run(filter, 10, 10, 0.5f);
        Assert.Equal(100, filter.Next(idleness, throttling));
        // The window was emptied, so ten samples are needed again.
        Assert.Equal([100, 100, 100, 100, 100, 100, 100, 100, 100, 50], Run(filter, 10, 10, 0.5f));
    }

    [Theory]
    [InlineData(90, 0.5f, 50)]
    [InlineData(10, 0.1f, 90)]
    public void SpeedLimit_ThresholdsAreInclusive(int idleness, float throttling, int expected)
    {
        var filter = new SpeedLimitFilter();
        Assert.Equal(expected, Run(filter, 10, idleness, throttling)[^1]);
    }

    [Fact]
    public void SpeedLimit_AnUnreadableIdlenessGivesMaxAndKeepsTheWindow()
    {
        var filter = new SpeedLimitFilter();
        Run(filter, 9, 10, 0.5f);
        Assert.Equal(100, filter.Next(null, 0.5f));
        Assert.Equal(50, filter.Next(10, 0.5f));
    }
}
