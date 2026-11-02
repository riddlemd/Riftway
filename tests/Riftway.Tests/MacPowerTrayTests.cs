using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Chromium's base/power_monitor mac sources, power_save_blocker_mac.cc, and Electron v44's MacLockMonitor.
public sealed class MacPowerTests
{
    [Fact]
    public void MacAssertionType_IsChromiumsIOKitType()
    {
        Assert.Equal("NoDisplaySleepAssertion", PowerSaveBlockers.MacAssertionType(PowerSaveBlockerType.PreventDisplaySleep));
        Assert.Equal("NoIdleSleepAssertion", PowerSaveBlockers.MacAssertionType(PowerSaveBlockerType.PreventAppSuspension));
    }

    [Theory]
    [InlineData(0L, ThermalState.Nominal)]
    [InlineData(1L, ThermalState.Fair)]
    [InlineData(2L, ThermalState.Serious)]
    [InlineData(3L, ThermalState.Critical)]
    [InlineData(4L, ThermalState.Unknown)]
    [InlineData(-1L, ThermalState.Unknown)]
    public void ThermalStateOf_MapsNSProcessInfoThermalState(long ns, ThermalState state) => Assert.Equal(state, PowerRules.ThermalStateOf(ns));

    [Theory]
    [InlineData(0xE0000280u, PowerEvent.Suspend)]
    [InlineData(0xE0000320u, PowerEvent.Resume)]
    [InlineData(0xE0000270u, PowerEvent.None)]
    [InlineData(0xE0000290u, PowerEvent.None)]
    [InlineData(0xE0000300u, PowerEvent.None)]
    public void MacSystemPowerEventOf_WillSleepAndWillPowerOn(uint message, object e) => Assert.Equal(e, PowerRules.MacSystemPowerEventOf(message));

    [Fact]
    public void SuspendFilter_DropsRepeatedSuspendsAndResumes()
    {
        var filter = new SuspendFilter();

        Assert.False(filter.Accept(PowerEvent.Resume));
        Assert.True(filter.Accept(PowerEvent.Suspend));
        Assert.False(filter.Accept(PowerEvent.Suspend));
        Assert.True(filter.Accept(PowerEvent.Resume));
        Assert.False(filter.Accept(PowerEvent.Resume));
        Assert.True(filter.Accept(PowerEvent.OnAc));
    }

    private static MacBatteryReading Battery(bool external) => new(true, true, true, external, true, true);

    [Fact]
    public void MacBatteryStatus_NoServiceIsExternalPower() => Assert.Equal(BatteryPowerStatus.External, PowerRules.MacBatteryStatus(default));

    [Fact]
    public void MacBatteryStatus_ReadsExternalConnected()
    {
        Assert.Equal(BatteryPowerStatus.External, PowerRules.MacBatteryStatus(Battery(true)));
        Assert.Equal(BatteryPowerStatus.Battery, PowerRules.MacBatteryStatus(Battery(false)));
    }

    [Fact]
    public void MacBatteryStatus_AnyMissingPropertyIsUnknown()
    {
        Assert.Equal(BatteryPowerStatus.Unknown, PowerRules.MacBatteryStatus(Battery(false) with { PropertiesRead = false }));
        Assert.Equal(BatteryPowerStatus.Unknown, PowerRules.MacBatteryStatus(Battery(false) with { BatteryInstalled = false }));
        Assert.Equal(BatteryPowerStatus.Unknown, PowerRules.MacBatteryStatus(Battery(false) with { BatteryInstalled = null }));
        Assert.Equal(BatteryPowerStatus.Unknown, PowerRules.MacBatteryStatus(Battery(false) with { ExternalConnected = null }));
        Assert.Equal(BatteryPowerStatus.Unknown, PowerRules.MacBatteryStatus(Battery(false) with { HasCapacities = false }));
        Assert.Equal(BatteryPowerStatus.Unknown, PowerRules.MacBatteryStatus(Battery(false) with { HasVoltage = false }));
    }

    [Theory]
    [InlineData(BatteryPowerStatus.External, BatteryPowerStatus.Battery, PowerEvent.OnBattery)]
    [InlineData(BatteryPowerStatus.Battery, BatteryPowerStatus.External, PowerEvent.OnAc)]
    [InlineData(BatteryPowerStatus.Unknown, BatteryPowerStatus.External, PowerEvent.OnAc)]
    [InlineData(BatteryPowerStatus.Unknown, BatteryPowerStatus.Battery, PowerEvent.OnBattery)]
    [InlineData(BatteryPowerStatus.Battery, BatteryPowerStatus.Unknown, PowerEvent.None)]
    [InlineData(BatteryPowerStatus.Battery, BatteryPowerStatus.Battery, PowerEvent.None)]
    [InlineData(BatteryPowerStatus.External, BatteryPowerStatus.External, PowerEvent.None)]
    public void BatteryEventOf_OnlyAKnownChangeNotifies(object was, object now, object e)
        => Assert.Equal(e, PowerRules.BatteryEventOf((BatteryPowerStatus)was, (BatteryPowerStatus)now));

    [Theory]
    [InlineData(MacSessionNotification.ScreenLocked, PowerEvent.Lock)]
    [InlineData(MacSessionNotification.ScreenUnlocked, PowerEvent.Unlock)]
    [InlineData(MacSessionNotification.SessionDidBecomeActive, PowerEvent.UserDidBecomeActive)]
    [InlineData(MacSessionNotification.SessionDidResignActive, PowerEvent.UserDidResignActive)]
    public void MacSessionEventOf_IsMacLockMonitors(MacSessionNotification n, object e) => Assert.Equal(e, PowerRules.MacSessionEventOf(n));
}

// Electron v44's tray_icon_cocoa.mm, NSString+ANSI.mm and gfx/mac/coordinate_conversion.mm.
public sealed class MacTrayTests
{
    [Theory]
    [InlineData(MacStatusItemSignal.MouseDown, TrayEvent.MouseDown)]
    [InlineData(MacStatusItemSignal.MouseUp, TrayEvent.MouseUp)]
    [InlineData(MacStatusItemSignal.MouseEntered, TrayEvent.MouseEnter)]
    [InlineData(MacStatusItemSignal.MouseExited, TrayEvent.MouseLeave)]
    [InlineData(MacStatusItemSignal.MouseMoved, TrayEvent.MouseMove)]
    [InlineData(MacStatusItemSignal.DragEntered, TrayEvent.DragEnter)]
    [InlineData(MacStatusItemSignal.DragExited, TrayEvent.DragLeave)]
    [InlineData(MacStatusItemSignal.Click, TrayEvent.None)]
    [InlineData(MacStatusItemSignal.RightMouseUp, TrayEvent.None)]
    [InlineData(MacStatusItemSignal.DragEnded, TrayEvent.None)]
    public void EventOf_MapsTheLocationOnlyEvents(MacStatusItemSignal signal, object e) => Assert.Equal(e, MacTrayRules.EventOf(signal));

    [Theory]
    [InlineData(1L, false, true, false)]
    [InlineData(2L, false, false, true)]
    [InlineData(3L, false, false, false)]
    [InlineData(0L, false, false, false)]
    [InlineData(1L, true, true, false)]
    [InlineData(2L, true, true, false)]
    [InlineData(3L, true, true, false)]
    public void Clicks_FollowHandleClickNotifications(long count, bool ignore, bool click, bool doubleClick)
        => Assert.Equal((click, doubleClick), MacTrayRules.Clicks(count, ignore));

    [Fact]
    public void Keys_MapShiftControlOptionCommand()
    {
        Assert.Equal((true, false, false, false), MacTrayRules.Keys(1UL << 17));
        Assert.Equal((false, true, false, false), MacTrayRules.Keys(1UL << 18));
        Assert.Equal((false, false, true, false), MacTrayRules.Keys(1UL << 19));
        Assert.Equal((false, false, false, true), MacTrayRules.Keys(1UL << 20));
        // Caps Lock and the function key are not event keys.
        Assert.Equal((false, false, false, false), MacTrayRules.Keys((1UL << 16) | (1UL << 23)));
    }

    [Fact]
    public void ScreenPointFromNSPoint_FlipsAgainstThePrimaryDisplayAndTruncates()
    {
        Assert.Equal(new Point(10, 882), MacTrayRules.ScreenPointFromNSPoint(10.9, 17.5, 900));
        Assert.Equal(new Point(0, 900), MacTrayRules.ScreenPointFromNSPoint(0, 0, 900));
    }

    [Fact]
    public void ScreenRectFromNSRect_FlipsTheTopEdge()
    {
        Assert.Equal(new Rectangle(1200, 0, 30, 24), MacTrayRules.ScreenRectFromNSRect(1200, 876, 30, 24, 900));
        // A synthetic VoiceOver event has no window: Electron reports a zero rect at the display's height.
        Assert.Equal(new Rectangle(0, 900, 0, 0), MacTrayRules.ScreenRectFromNSRect(0, 0, 0, 0, 900));
        Assert.Equal(new Rectangle(5, 870, 30, 24), MacTrayRules.ScreenRectFromNSRect(5.7, 6.2, 30.9, 24.1, 900.5));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(29.9, 23.9, true)]
    [InlineData(30, 10, false)]
    [InlineData(10, 24, false)]
    [InlineData(-0.1, 10, false)]
    [InlineData(10, -0.1, false)]
    public void PointInRect_IncludesTheMinimumEdgesOnly(double x, double y, bool inside) => Assert.Equal(inside, MacTrayRules.PointInRect(x, y, 0, 0, 30, 24));

    [Fact]
    public void FontTypeCode_IsWhatMacStatusItemTakes()
    {
        Assert.Equal(0, MacTrayRules.FontTypeCode(null));
        Assert.Equal(1, MacTrayRules.FontTypeCode(TrayTitleFontType.Monospaced));
        Assert.Equal(2, MacTrayRules.FontTypeCode(TrayTitleFontType.MonospacedDigit));
    }

    [Fact]
    public void TitleRuns_WithoutEscapeIsOnePlainRun() => Assert.Equal(new[] { new MacTitleRun("CPU 42%", null, null) }, MacTrayRules.TitleRuns("CPU 42%"));

    [Fact]
    public void TitleRuns_ColoursEachPart_AndResetClearsThem()
    {
        var runs = MacTrayRules.TitleRuns("a\u001b[31mred\u001b[42mon green\u001b[0mplain");

        Assert.Equal(new[]
        {
            new MacTitleRun("a", null, null),
            new MacTitleRun("red", 0xFF0000, null),
            new MacTitleRun("on green", 0xFF0000, 0x00CD00),
            new MacTitleRun("plain", null, null),
        }, runs);
    }

    [Fact]
    public void TitleRuns_BoldPicksTheOtherPalette_OnlyWithinOneSequence()
    {
        Assert.Equal(0xCD0000, MacTrayRules.TitleRuns("\u001b[1;31mx")[1].Foreground);
        // Bold starts off at each sequence, so a separate ESC[1m changes nothing.
        Assert.Equal(0xFF0000, MacTrayRules.TitleRuns("\u001b[1m\u001b[31mx")[2].Foreground);
        Assert.Equal(0xFF0000, MacTrayRules.TitleRuns("\u001b[1;22;31mx")[1].Foreground);
    }

    [Fact]
    public void TitleRuns_KeepsLaterMs_SkipsEmptyParts_AndAppendsAPartWithoutM()
    {
        Assert.Equal(new[] { new MacTitleRun("", null, null), new MacTitleRun("mmm", 0x00FF00, null) }, MacTrayRules.TitleRuns("\u001b[32mmmm"));
        Assert.Equal(new[] { new MacTitleRun("x", null, null), new MacTitleRun("y", 0x00FF00, null) }, MacTrayRules.TitleRuns("x\u001b[\u001b[32my"));
        Assert.Equal(new[] { new MacTitleRun("x", null, null), new MacTitleRun("12;3", null, null) }, MacTrayRules.TitleRuns("x\u001b[12;3"));
    }

    [Theory]
    [InlineData("30", 0x000000, null)]
    [InlineData("1;30", 0x7F7F7F, null)]
    [InlineData("34", 0x5C5CFF, null)]
    [InlineData("1;34", 0x0000EE, null)]
    [InlineData("37", 0xFFFFFF, null)]
    [InlineData("1;37", 0xE5E5E5, null)]
    [InlineData("40", null, 0x7F7F7F)]
    [InlineData("45", null, 0xCD00CD)]
    [InlineData("47", null, 0xE5E5E5)]
    [InlineData("1;47", null, 0xE5E5E5)]
    [InlineData("38", null, null)]
    [InlineData("48", null, null)]
    public void ApplyCodes_UsesElectronsPalette(string codes, int? fg, int? bg) => Assert.Equal((fg, bg), MacTrayRules.ApplyCodes(codes, null, null));

    [Fact]
    public void ApplyCodes_39And49DropOneColour_AndAnEmptyCodeResets()
    {
        Assert.Equal((null, 2), MacTrayRules.ApplyCodes("39", 1, 2));
        Assert.Equal((1, null), MacTrayRules.ApplyCodes("49", 1, 2));
        Assert.Equal((null, null), MacTrayRules.ApplyCodes("", 1, 2));
        Assert.Equal((1, 2), MacTrayRules.ApplyCodes("3", 1, 2));
    }

    [Theory]
    [InlineData("31", 31)]
    [InlineData("  31x", 31)]
    [InlineData("+7", 7)]
    [InlineData("-4", -4)]
    [InlineData("", 0)]
    [InlineData("x1", 0)]
    [InlineData("99999999999", int.MaxValue)]
    [InlineData("-99999999999", int.MinValue)]
    [InlineData("9999999999999999999999999", int.MaxValue)]
    // 1e19 wraps a long to a negative number unless the accumulation is capped.
    [InlineData("10000000000000000000", int.MaxValue)]
    public void IntValue_ParsesAsNSString(string s, int value) => Assert.Equal(value, MacTrayRules.IntValue(s));

    [Fact]
    public void ShownAccelerator_DropsTheRoleDefaultOutsideTheMenuBar()
    {
        var copy = new MenuItem { Role = MenuItemRole.Copy };
        var own = new MenuItem { Role = MenuItemRole.Copy, Accelerator = "CmdOrCtrl+Shift+C" };

        Assert.Equal("CmdOrCtrl+C", MacMenuItemState.ShownAccelerator(copy, inMenuBar: true));
        Assert.Null(MacMenuItemState.ShownAccelerator(copy, inMenuBar: false));
        Assert.Equal("CmdOrCtrl+Shift+C", MacMenuItemState.ShownAccelerator(own, inMenuBar: false));
    }

    [Fact]
    public void Cancel_ClosesAnOpenMenuOnce_AndRaisesMenuWillClose()
    {
        var tree = new FakeMenuTree();
        var menu = new Menu { new MenuItem { Label = "A" } };
        var closes = 0;
        menu.MenuWillClose += (_, _) => closes++;
        var controller = new MacMenuController(menu, 7, tree);
        var cancelled = new List<nint>();

        controller.Cancel(cancelled.Add);
        Assert.Empty(cancelled);
        controller.WillOpen(7, _ => { });
        Assert.True(controller.IsMenuOpen);
        controller.Cancel(cancelled.Add);
        controller.Cancel(cancelled.Add);

        Assert.Equal(new nint[] { 7 }, cancelled);
        Assert.Equal(1, closes);
        Assert.False(controller.IsMenuOpen);
    }
}
