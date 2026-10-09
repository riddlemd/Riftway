using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class SystemPreferencesTests
{
    // Electron's GetColor map, with the COLOR_ values from WinUser.h.
    public static TheoryData<string, int> ColorNames => new()
    {
        { "3d-dark-shadow", 21 }, { "3d-face", 15 }, { "3d-highlight", 20 }, { "3d-light", 22 }, { "3d-shadow", 16 },
        { "active-border", 10 }, { "active-caption", 2 }, { "active-caption-gradient", 27 }, { "app-workspace", 12 },
        { "button-text", 18 }, { "caption-text", 9 }, { "desktop", 1 }, { "disabled-text", 17 }, { "highlight", 13 },
        { "highlight-text", 14 }, { "hotlight", 26 }, { "inactive-border", 11 }, { "inactive-caption", 3 },
        { "inactive-caption-gradient", 28 }, { "inactive-caption-text", 19 }, { "info-background", 24 }, { "info-text", 23 },
        { "menu", 4 }, { "menu-highlight", 29 }, { "menu-text", 7 }, { "menubar", 30 }, { "scrollbar", 0 }, { "window", 5 },
        { "window-frame", 6 }, { "window-text", 8 },
    };

    [Theory]
    [MemberData(nameof(ColorNames))]
    public void SysColorIndex_MapsEveryWindowsName(string name, int index) => Assert.Equal(index, SystemPreferences.SysColorIndex(name));

    [Theory]
    [InlineData("Window")]
    [InlineData("control-text")]
    [InlineData("")]
    public void SysColorIndex_RejectsOtherNames(string name) => Assert.Null(SystemPreferences.SysColorIndex(name));

    [Fact]
    public void GetColor_UnknownNameThrowsElectronsMessage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var e = Assert.Throws<ArgumentException>(() => SystemPreferences.GetColor("nope"));
        Assert.StartsWith("Unknown color: nope", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToRgbaHex_OrdersRgbaInUpperCase()
    {
        Assert.Equal("#ABEFDECD", SystemPreferences.ToRgbaHex(0xCDABEFDE, includeHash: true));
        Assert.Equal("12AB34CD", SystemPreferences.ToRgbaHex(0xCD12AB34, includeHash: false));
    }

    [Fact]
    public void ColorRefToArgb_SwapsRedAndBlueAndIgnoresTheHighByte()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(0xFF0078D4u, WindowsSystemPreferences.ColorRefToArgb(0x00D47800));
        Assert.Equal(0xFF0078D4u, WindowsSystemPreferences.ColorRefToArgb(0xFFD47800));
        Assert.Equal(0xFF123456u, WindowsSystemPreferences.ColorRefToArgb(0x00563412));
    }

    [Fact]
    public void FormatAccent_IsEmptyWithoutAColour()
    {
        Assert.Equal("", SystemPreferences.FormatAccent(null));
        Assert.Equal("0078D4FF", SystemPreferences.FormatAccent(0xFF0078D4));
    }

    [Theory]
    [InlineData(0, MediaAccessStatus.NotDetermined)]
    [InlineData(1, MediaAccessStatus.Granted)]
    [InlineData(2, MediaAccessStatus.Denied)]
    [InlineData(3, MediaAccessStatus.Restricted)]
    [InlineData(4, MediaAccessStatus.Unknown)]
    [InlineData(-1, MediaAccessStatus.Unknown)]
    public void ToMediaAccessStatus_FollowsElectron(int status, MediaAccessStatus expected) =>
        Assert.Equal(expected, SystemPreferences.ToMediaAccessStatus(status));

    [Fact]
    public void GetMediaAccessStatus_ScreenIsAlwaysGranted()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(MediaAccessStatus.Granted, SystemPreferences.GetMediaAccessStatus(MediaAccessType.Screen));
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemPreferences.GetMediaAccessStatus((MediaAccessType)7));
    }

    [Theory]
    [InlineData(0x0015u, 0, true)] // WM_SYSCOLORCHANGE
    [InlineData(0x001Au, 0x43, true)] // WM_SETTINGCHANGE, SPI_SETHIGHCONTRAST
    [InlineData(0x001Au, 0x42, false)] // another SPI
    [InlineData(0x001Au, 0, false)] // ImmersiveColorSet arrives with wParam 0
    [InlineData(0x0320u, 0, false)] // WM_DWMCOLORIZATIONCOLORCHANGED
    [InlineData(0x0016u, 0, false)]
    public void IsColorChange_MatchesElectronsFilter(uint message, int wParam, bool expected) =>
        Assert.Equal(expected, SystemPreferences.IsColorChange(message, wParam));

    [Fact]
    public void TryUpdateAccent_OnlyOnChange()
    {
        var current = "0078D4FF";
        Assert.False(SystemPreferences.TryUpdateAccent(ref current, "0078D4FF"));
        Assert.True(SystemPreferences.TryUpdateAccent(ref current, "123456FF"));
        Assert.Equal("123456FF", current);
        Assert.True(SystemPreferences.TryUpdateAccent(ref current, ""));
        Assert.Equal("", current);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    public void ShouldRenderRichAnimation_SettingThenSession(bool? setting, bool remote, bool expected) =>
        Assert.Equal(expected, SystemPreferences.ShouldRenderRichAnimation(setting, () => remote));

    [Fact]
    public void ShouldRenderRichAnimation_AsksAboutTheSessionOnlyWithoutTheSetting()
    {
        var asked = false;
        _ = SystemPreferences.ShouldRenderRichAnimation(true, () => asked = true);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, false)]
    public void PrefersReducedMotion_InvertsTheSetting(bool? setting, bool expected) =>
        Assert.Equal(expected, SystemPreferences.PrefersReducedMotion(["app.exe"], setting));

    [Fact]
    public void PrefersReducedMotion_SwitchesWin()
    {
        Assert.True(SystemPreferences.PrefersReducedMotion(["app.exe", "--force-prefers-reduced-motion"], true));
        Assert.False(SystemPreferences.PrefersReducedMotion(["app.exe", "--force-prefers-no-reduced-motion"], false));
        // Chromium checks the reduced-motion switch first.
        Assert.True(SystemPreferences.PrefersReducedMotion(["app.exe", "--force-prefers-no-reduced-motion", "--force-prefers-reduced-motion"], true));
    }

    [Theory]
    [InlineData("--force-prefers-reduced-motion", true)]
    [InlineData("-force-prefers-reduced-motion", true)]
    [InlineData("/force-prefers-reduced-motion", true)]
    [InlineData("--FORCE-prefers-reduced-motion", true)]
    [InlineData("--force-prefers-reduced-motion=1", true)]
    [InlineData("force-prefers-reduced-motion", false)]
    [InlineData("--force-prefers-reduced-motion-x", false)]
    public void HasSwitch_ParsesLikeBaseCommandLine(string arg, bool expected) =>
        Assert.Equal(expected, SystemPreferences.HasSwitch(["app.exe", arg], "force-prefers-reduced-motion", windows: true));

    [Fact]
    public void HasSwitch_SkipsTheProgramAndStopsAtDoubleDash()
    {
        Assert.False(SystemPreferences.HasSwitch(["--force-prefers-reduced-motion"], "force-prefers-reduced-motion", windows: true));
        Assert.False(SystemPreferences.HasSwitch(["app.exe", "--", "--force-prefers-reduced-motion"], "force-prefers-reduced-motion", windows: true));
        Assert.False(SystemPreferences.HasSwitch(["app", "--", "--force-prefers-reduced-motion"], "force-prefers-reduced-motion", windows: false));
    }

    [Theory]
    [InlineData("--force-prefers-reduced-motion", true)]
    [InlineData("-force-prefers-reduced-motion", true)]
    [InlineData("--force-prefers-reduced-motion=1", true)]
    [InlineData("/force-prefers-reduced-motion", false)]
    [InlineData("--FORCE-prefers-reduced-motion", false)]
    public void HasSwitch_OnPosixTakesDashesOnlyAndKeepsCase(string arg, bool expected) =>
        Assert.Equal(expected, SystemPreferences.HasSwitch(["app", arg], "force-prefers-reduced-motion", windows: false));

    [Fact]
    public void ForcedReducedMotion_ReducedSwitchWinsAndNoneIsNull()
    {
        Assert.Null(SystemPreferences.ForcedReducedMotion(["app"], windows: false));
        Assert.True(SystemPreferences.ForcedReducedMotion(["app", "--force-prefers-reduced-motion"], windows: false));
        Assert.False(SystemPreferences.ForcedReducedMotion(["app", "--force-prefers-no-reduced-motion"], windows: false));
        Assert.True(SystemPreferences.ForcedReducedMotion(["app", "--force-prefers-no-reduced-motion", "--force-prefers-reduced-motion"], windows: false));
    }
}
