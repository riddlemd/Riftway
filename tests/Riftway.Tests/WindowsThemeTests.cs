using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class WindowsThemeTests
{
    private static uint Grey(uint level) => level | (level << 8) | (level << 16);

    private static readonly WindowsThemeInputs s_light = new(
        AppsUseLightTheme: true, EnableTransparency: true, ForcedColorsActive: false,
        WindowColor: Grey(255), WindowTextColor: Grey(0), InvertFilter: false, AccentColor: 0x00D77800, CaretBlinkTime: 530);

    // A contrast theme with the given window and text greys.
    private static WindowsThemeInputs Forced(uint window, uint text, bool appsUseLight = true)
        => s_light with { ForcedColorsActive = true, WindowColor = Grey(window), WindowTextColor = Grey(text), AppsUseLightTheme = appsUseLight };

    [Fact]
    public void PrefersDark_WithoutContrastTheme_IsAppsUseLightTheme()
    {
        Assert.False(WindowsTheme.PrefersDark(s_light));
        Assert.True(WindowsTheme.PrefersDark(s_light with { AppsUseLightTheme = false }));
    }

    [Fact]
    public void PrefersDark_InContrastTheme_FollowsTheWindowColoursLightness()
    {
        // Grey 77 is L* 32.7 and 164 is 67.4; 78 (33.2) and 163 (67.0) leave it to AppsUseLightTheme.
        Assert.True(WindowsTheme.PrefersDark(Forced(77, 255, appsUseLight: true)));
        Assert.False(WindowsTheme.PrefersDark(Forced(164, 0, appsUseLight: false)));
        Assert.False(WindowsTheme.PrefersDark(Forced(78, 255, appsUseLight: true)));
        Assert.True(WindowsTheme.PrefersDark(Forced(78, 255, appsUseLight: false)));
        Assert.True(WindowsTheme.PrefersDark(Forced(163, 0, appsUseLight: false)));
    }

    [Fact]
    public void LabLightness_MatchesCssLab()
    {
        Assert.Equal(100f, WindowsTheme.LabLightness(Grey(255)), 0.01f);
        Assert.Equal(0f, WindowsTheme.LabLightness(Grey(0)), 0.01f);
        Assert.Equal(50.03f, WindowsTheme.LabLightness(Grey(119)), 0.02f);
        // Pure red is L* 54.29 (D50); a channel swap would read blue's 29.57.
        Assert.Equal(54.29f, WindowsTheme.LabLightness(0x0000FF), 0.05f);
    }

    [Fact]
    public void ContrastRatio_IsWcag()
    {
        Assert.Equal(21f, WindowsTheme.ContrastRatio(Grey(0), Grey(255)), 0.01f);
        Assert.Equal(21f, WindowsTheme.ContrastRatio(Grey(255), Grey(0)), 0.01f);
        Assert.Equal(1f, WindowsTheme.ContrastRatio(Grey(119), Grey(119)), 0.001f);
    }

    [Fact]
    public void Contrast_NoPreferenceWithoutContrastTheme()
        => Assert.Equal(PreferredContrast.NoPreference, WindowsTheme.Contrast(s_light));

    [Fact]
    public void Contrast_InContrastTheme_ByTextToWindowRatio()
    {
        // Grey 89 on white is 7.005:1, 90 is 6.90:1, 163 is 2.52:1 and 164 is 2.49:1.
        Assert.Equal(PreferredContrast.More, WindowsTheme.Contrast(Forced(255, 89)));
        Assert.Equal(PreferredContrast.Custom, WindowsTheme.Contrast(Forced(255, 90)));
        Assert.Equal(PreferredContrast.Custom, WindowsTheme.Contrast(Forced(255, 163)));
        Assert.Equal(PreferredContrast.Less, WindowsTheme.Contrast(Forced(255, 164)));
    }

    [Fact]
    public void Variables_MapEachSetting()
    {
        var inputs = Forced(0, 255, appsUseLight: true) with { EnableTransparency = false, InvertFilter = true, AccentColor = 0x123456, CaretBlinkTime = 700 };
        Assert.Equal(new WindowsThemeVariables(ForcedColors: true, Dark: true, PreferredContrast.More, ReducedTransparency: true,
            Inverted: true, Accent: 0x123456, CaretBlinkTime: 700), WindowsTheme.Variables(inputs));
        Assert.Equal(new WindowsThemeVariables(false, false, PreferredContrast.NoPreference, false, false, 0x00D77800, 530),
            WindowsTheme.Variables(s_light));
    }

    [Theory]
    [InlineData(WindowsThemeChange.Personalize)]
    [InlineData(WindowsThemeChange.ColorFiltering)]
    [InlineData(WindowsThemeChange.Dwm)]
    [InlineData(WindowsThemeChange.HighContrast)]
    [InlineData(WindowsThemeChange.SysColor)]
    public void Next_NothingChanged_EmitsNothing(WindowsThemeChange change)
        => Assert.Null(WindowsTheme.Next(change, s_light, s_light, WindowsTheme.Variables(s_light)));

    private static readonly (WindowsThemeChange Change, WindowsThemeInputs After)[] s_changes =
    [
        (WindowsThemeChange.Personalize, s_light with { AppsUseLightTheme = false }),
        (WindowsThemeChange.Personalize, s_light with { EnableTransparency = false }),
        (WindowsThemeChange.ColorFiltering, s_light with { InvertFilter = true }),
        (WindowsThemeChange.Dwm, s_light with { AccentColor = 0x00112233 }),
        (WindowsThemeChange.Dwm, s_light with { AccentColor = null }),
        (WindowsThemeChange.HighContrast, Forced(0, 255)),
    ];

    public static TheoryData<int> Changes => [.. Enumerable.Range(0, s_changes.Length)];

    [Theory]
    [MemberData(nameof(Changes))]
    public void Next_EachSourcesOwnChange_EmitsTheNewVariables(int index)
    {
        var (change, after) = s_changes[index];
        Assert.Equal(WindowsTheme.Variables(after), WindowsTheme.Next(change, s_light, after, WindowsTheme.Variables(s_light)));
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void Next_AnotherSourcesChange_IsNotPassedOn(int index)
    {
        var (change, after) = s_changes[index];
        // OsSettingsProviderWin only compares what the notifying source covers.
        var other = change == WindowsThemeChange.Dwm ? WindowsThemeChange.Personalize : WindowsThemeChange.Dwm;
        Assert.Null(WindowsTheme.Next(other, s_light, after, WindowsTheme.Variables(s_light)));
    }

    [Fact]
    public void Next_AppsUseLightThemeInContrastTheme_IsDecidedByTheWindowColour()
        => Assert.Null(WindowsTheme.Next(WindowsThemeChange.Personalize, Forced(0, 255, appsUseLight: true),
            Forced(0, 255, appsUseLight: false), WindowsTheme.Variables(Forced(0, 255))));

    [Fact]
    public void Next_ProviderChange_ThatNativeThemeAlreadyHas_EmitsNothing()
    {
        var dark = s_light with { AppsUseLightTheme = false };
        Assert.Null(WindowsTheme.Next(WindowsThemeChange.Personalize, s_light, dark, WindowsTheme.Variables(dark)));
    }

    [Fact]
    public void Next_SysColorInContrastTheme_EmitsEvenUnchanged()
    {
        var forced = Forced(0, 255);
        Assert.Equal(WindowsTheme.Variables(forced), WindowsTheme.Next(WindowsThemeChange.SysColor, forced, forced, WindowsTheme.Variables(forced)));
    }

    [Fact]
    public void Next_SysColorWithoutContrastTheme_EmitsNothingEvenChanged()
        => Assert.Null(WindowsTheme.Next(WindowsThemeChange.SysColor, s_light, s_light with { WindowColor = Grey(0) }, WindowsTheme.Variables(s_light)));

    [Fact]
    public void ShouldUseDarkColors_SourceOverridesTheSystem()
    {
        var light = WindowsTheme.Variables(s_light);
        var dark = WindowsTheme.Variables(s_light with { AppsUseLightTheme = false });
        Assert.True(WindowsTheme.ShouldUseDarkColors(ThemeSource.Dark, light));
        Assert.False(WindowsTheme.ShouldUseDarkColors(ThemeSource.Light, dark));
        Assert.True(WindowsTheme.ShouldUseDarkColors(ThemeSource.System, dark));
        Assert.False(WindowsTheme.ShouldUseDarkColors(ThemeSource.System, light));
    }

    [Fact]
    public void ShouldUseHighContrastColors_OnlyForMoreContrast()
    {
        Assert.True(WindowsTheme.ShouldUseHighContrastColors(WindowsTheme.Variables(Forced(255, 0))));
        Assert.False(WindowsTheme.ShouldUseHighContrastColors(WindowsTheme.Variables(Forced(255, 120))));
        Assert.False(WindowsTheme.ShouldUseHighContrastColors(WindowsTheme.Variables(s_light)));
    }

    [Fact]
    public void ShouldUseInvertedColorScheme_IsForcedColoursWithADarkScheme()
    {
        Assert.True(WindowsTheme.ShouldUseInvertedColorScheme(ThemeSource.System, WindowsTheme.Variables(Forced(0, 255))));
        Assert.False(WindowsTheme.ShouldUseInvertedColorScheme(ThemeSource.System, WindowsTheme.Variables(Forced(255, 0))));
        Assert.True(WindowsTheme.ShouldUseInvertedColorScheme(ThemeSource.Dark, WindowsTheme.Variables(Forced(255, 0))));
        Assert.False(WindowsTheme.ShouldUseInvertedColorScheme(ThemeSource.Dark, WindowsTheme.Variables(s_light)));
    }
}
