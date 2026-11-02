using System.Text.Json.Nodes;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class SystemPreferencesMacLinuxTests
{
    // Electron's macOS GetColor names with the NSColor selectors from electron_api_system_preferences_mac.mm.
    public static TheoryData<string, string> MacColorNames => new()
    {
        { "control-background", "controlBackgroundColor" }, { "control", "controlColor" }, { "control-text", "controlTextColor" },
        { "disabled-control-text", "disabledControlTextColor" }, { "find-highlight", "findHighlightColor" }, { "grid", "gridColor" },
        { "header-text", "headerTextColor" }, { "highlight", "highlightColor" }, { "keyboard-focus-indicator", "keyboardFocusIndicatorColor" },
        { "label", "labelColor" }, { "link", "linkColor" }, { "placeholder-text", "placeholderTextColor" },
        { "quaternary-label", "quaternaryLabelColor" }, { "scrubber-textured-background", "scrubberTexturedBackgroundColor" },
        { "secondary-label", "secondaryLabelColor" }, { "selected-content-background", "selectedContentBackgroundColor" },
        { "selected-control", "selectedControlColor" }, { "selected-control-text", "selectedControlTextColor" },
        { "selected-menu-item-text", "selectedMenuItemTextColor" }, { "selected-text-background", "selectedTextBackgroundColor" },
        { "selected-text", "selectedTextColor" }, { "separator", "separatorColor" }, { "shadow", "shadowColor" },
        { "tertiary-label", "tertiaryLabelColor" }, { "text-background", "textBackgroundColor" }, { "text", "textColor" },
        { "under-page-background", "underPageBackgroundColor" },
        { "unemphasized-selected-content-background", "unemphasizedSelectedContentBackgroundColor" },
        { "unemphasized-selected-text-background", "unemphasizedSelectedTextBackgroundColor" },
        { "unemphasized-selected-text", "unemphasizedSelectedTextColor" }, { "window-background", "windowBackgroundColor" },
        { "window-frame-text", "windowFrameTextColor" },
    };

    [Theory]
    [MemberData(nameof(MacColorNames))]
    public void MacColorSelector_MapsEveryMacName(string name, string selector) => Assert.Equal(selector, SystemPreferences.MacColorSelector(name));

    [Theory]
    [InlineData("window")]
    [InlineData("3d-face")]
    [InlineData("Label")]
    [InlineData("blue")]
    [InlineData("")]
    public void MacColorSelector_RejectsOtherNames(string name) => Assert.Null(SystemPreferences.MacColorSelector(name));

    [Theory]
    [InlineData("blue", "systemBlueColor")]
    [InlineData("brown", "systemBrownColor")]
    [InlineData("gray", "systemGrayColor")]
    [InlineData("green", "systemGreenColor")]
    [InlineData("orange", "systemOrangeColor")]
    [InlineData("pink", "systemPinkColor")]
    [InlineData("purple", "systemPurpleColor")]
    [InlineData("red", "systemRedColor")]
    [InlineData("yellow", "systemYellowColor")]
    public void MacSystemColorSelector_MapsEveryName(string name, string selector) => Assert.Equal(selector, SystemPreferences.MacSystemColorSelector(name));

    [Theory]
    [InlineData("grey")]
    [InlineData("Blue")]
    [InlineData("label")]
    [InlineData("")]
    public void MacSystemColorSelector_RejectsOtherNames(string name) => Assert.Null(SystemPreferences.MacSystemColorSelector(name));

    [Fact]
    public void SkColor4fToArgb_RoundsHalfUpInArgbOrder()
    {
        Assert.Equal(0xFFFF0000u, MacPreferenceRules.SkColor4fToArgb(1, 0, 0, 1));
        Assert.Equal(0x80336699u, MacPreferenceRules.SkColor4fToArgb(0.2, 0.4, 0.6, 0.5));
        // 0.999 * 255 = 254.7: rounds to 255 where truncation would give 254.
        Assert.Equal(0xFFFF0000u, MacPreferenceRules.SkColor4fToArgb(0.999, 0.001, 0, 1));
        // 127.5 rounds up.
        Assert.Equal(0xFF808080u, MacPreferenceRules.SkColor4fToArgb(0.5, 0.5, 0.5, 1));
    }

    [Fact]
    public void SkColor4fToArgb_PinsOutOfRangeChannels()
    {
        Assert.Equal(0xFFFF00FFu, MacPreferenceRules.SkColor4fToArgb(1.5, -0.2, 2, 3));
        Assert.Equal(0x00000000u, MacPreferenceRules.SkColor4fToArgb(-1, -1, -1, -1));
    }

    [Theory]
    [InlineData("c", MacNumberKind.Bool)]
    [InlineData("B", MacNumberKind.Bool)]
    [InlineData("d", MacNumberKind.Double)]
    [InlineData("f", MacNumberKind.Double)]
    [InlineData("q", MacNumberKind.Int)]
    [InlineData("i", MacNumberKind.Int)]
    [InlineData("s", MacNumberKind.Int)]
    [InlineData("", MacNumberKind.Int)]
    public void NumberKind_FollowsElectronsObjCTypeTest(string objCType, MacNumberKind expected)
        => Assert.Equal(expected, MacPreferenceRules.NumberKind(objCType));

    [Fact]
    public void LinuxAccentArgb_TruncatesEachChannelAndIsOpaque()
    {
        Assert.Equal(0xFF336699u, SystemPreferences.LinuxAccentArgb((0.2, 0.4, 0.6)));
        // 0.9 * 255 = 229.5 and 0.1 * 255 = 25.5: truncated, as SkColorSetRGB's unsigned arguments are.
        Assert.Equal(0xFFE51919u, SystemPreferences.LinuxAccentArgb((0.9, 0.1, 0.1)));
        Assert.Equal(0xFFFFFFFFu, SystemPreferences.LinuxAccentArgb((1, 1, 1)));
        Assert.Equal(0xFF000000u, SystemPreferences.LinuxAccentArgb((0, 0, 0)));
        Assert.Null(SystemPreferences.LinuxAccentArgb(null));
        Assert.Equal("E51919FF", SystemPreferences.FormatAccent(SystemPreferences.LinuxAccentArgb((0.9, 0.1, 0.1))));
    }

    [Theory]
    [InlineData(0, MediaAccessStatus.NotDetermined)]
    [InlineData(1, MediaAccessStatus.Restricted)]
    [InlineData(2, MediaAccessStatus.Denied)]
    [InlineData(3, MediaAccessStatus.Granted)]
    [InlineData(4, MediaAccessStatus.Unknown)]
    [InlineData(-1, MediaAccessStatus.Unknown)]
    public void ToMacMediaAccessStatus_FollowsAVAuthorizationStatus(long status, MediaAccessStatus expected)
        => Assert.Equal(expected, SystemPreferences.ToMacMediaAccessStatus(status));

    [Theory]
    [InlineData("NSAppearanceNameAqua", SystemAppearance.Light)]
    [InlineData("NSAppearanceNameDarkAqua", SystemAppearance.Dark)]
    [InlineData("NSAppearanceNameAccessibilityHighContrastDarkAqua", SystemAppearance.Unknown)]
    [InlineData("NSAppearanceNameVibrantDark", SystemAppearance.Unknown)]
    [InlineData(null, SystemAppearance.Unknown)]
    public void ToSystemAppearance_OnlyAquaAndDarkAquaAreKnown(string? name, SystemAppearance expected)
        => Assert.Equal(expected, SystemPreferences.ToSystemAppearance(name));

    [Fact]
    public void MacAnimationSettings_RichIsTheInverseOfReduceMotion()
    {
        Assert.Equal(new AnimationSettings(true, false, false), SystemPreferences.MacAnimationSettings(["app"], () => false, null));
        Assert.Equal(new AnimationSettings(false, true, true), SystemPreferences.MacAnimationSettings(["app"], () => true, true));
        Assert.Equal(new AnimationSettings(true, false, false), SystemPreferences.MacAnimationSettings(["app"], () => false, false));
    }

    [Fact]
    public void MacAnimationSettings_SwitchesWinWithoutReadingTheSetting()
    {
        var asked = false;
        var forced = SystemPreferences.MacAnimationSettings(["app", "--force-prefers-reduced-motion"], () => asked = true, null);
        Assert.Equal(new AnimationSettings(false, false, true), forced);
        Assert.False(asked);
        Assert.Equal(new AnimationSettings(true, true, false), SystemPreferences.MacAnimationSettings(["app", "--force-prefers-no-reduced-motion"], () => true, true));
        // A Windows-style switch is an argument on macOS.
        Assert.Equal(new AnimationSettings(true, false, false), SystemPreferences.MacAnimationSettings(["app", "/force-prefers-reduced-motion"], () => false, null));
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(null, true, true, false)]
    public void LinuxAnimationSettings_FollowGtkAndDefaultOn(bool? gtk, bool rich, bool scroll, bool reduced)
        => Assert.Equal(new AnimationSettings(rich, scroll, reduced), SystemPreferences.LinuxAnimationSettings(["app"], gtk));

    [Fact]
    public void LinuxAnimationSettings_SwitchesDecideReducedMotionOnly()
    {
        Assert.Equal(new AnimationSettings(true, true, true), SystemPreferences.LinuxAnimationSettings(["app", "--force-prefers-reduced-motion"], true));
        Assert.Equal(new AnimationSettings(false, false, false), SystemPreferences.LinuxAnimationSettings(["app", "--force-prefers-no-reduced-motion"], false));
    }

    [Theory]
    [InlineData(UserDefaultType.String, "string")]
    [InlineData(UserDefaultType.Boolean, "boolean")]
    [InlineData(UserDefaultType.Integer, "integer")]
    [InlineData(UserDefaultType.Float, "float")]
    [InlineData(UserDefaultType.Double, "double")]
    [InlineData(UserDefaultType.Url, "url")]
    [InlineData(UserDefaultType.Array, "array")]
    [InlineData(UserDefaultType.Dictionary, "dictionary")]
    [InlineData((UserDefaultType)99, "99")]
    public void UserDefaultTypeName_IsElectronsString(UserDefaultType type, string name) => Assert.Equal(name, SystemPreferences.UserDefaultTypeName(type));

    [Fact]
    public void CoerceUserDefault_StringAndBooleanTakeOnlyTheirOwnKind()
    {
        Assert.Equal("x", SystemPreferences.CoerceUserDefault(UserDefaultType.String, "x"));
        Assert.Equal(true, SystemPreferences.CoerceUserDefault(UserDefaultType.Boolean, true));
        Assert.Equal(false, SystemPreferences.CoerceUserDefault(UserDefaultType.Boolean, false));
        AssertUnable(UserDefaultType.String, 5);
        AssertUnable(UserDefaultType.String, null);
        AssertUnable(UserDefaultType.Boolean, "true");
        AssertUnable(UserDefaultType.Boolean, 1);
    }

    [Fact]
    public void CoerceUserDefault_IntegerTakesInt32ValuedNumbersOnly()
    {
        Assert.Equal(5, SystemPreferences.CoerceUserDefault(UserDefaultType.Integer, 5));
        Assert.Equal(5, SystemPreferences.CoerceUserDefault(UserDefaultType.Integer, 5.0));
        Assert.Equal(int.MaxValue, SystemPreferences.CoerceUserDefault(UserDefaultType.Integer, (long)int.MaxValue));
        Assert.Equal(int.MinValue, SystemPreferences.CoerceUserDefault(UserDefaultType.Integer, (double)int.MinValue));
        Assert.Equal(7, SystemPreferences.CoerceUserDefault(UserDefaultType.Integer, JsonValue.Create(7)));
        AssertUnable(UserDefaultType.Integer, 5.5);
        AssertUnable(UserDefaultType.Integer, 2147483648L);
        AssertUnable(UserDefaultType.Integer, -2147483649L);
        AssertUnable(UserDefaultType.Integer, -0.0);
        AssertUnable(UserDefaultType.Integer, "5");
    }

    [Fact]
    public void CoerceUserDefault_FloatAndDoubleTakeAnyNumber()
    {
        Assert.Equal(1.5f, SystemPreferences.CoerceUserDefault(UserDefaultType.Float, 1.5));
        Assert.Equal(3f, SystemPreferences.CoerceUserDefault(UserDefaultType.Float, 3));
        Assert.Equal(0.1, SystemPreferences.CoerceUserDefault(UserDefaultType.Double, 0.1));
        Assert.Equal(3.0, SystemPreferences.CoerceUserDefault(UserDefaultType.Double, 3L));
        Assert.Equal(2.5, SystemPreferences.CoerceUserDefault(UserDefaultType.Double, 2.5m));
        AssertUnable(UserDefaultType.Float, "1.5");
        AssertUnable(UserDefaultType.Double, true);
    }

    [Fact]
    public void CoerceUserDefault_UrlIsTheCanonicalSpecOfAValidUrl()
    {
        Assert.Equal("https://example.com/a", SystemPreferences.CoerceUserDefault(UserDefaultType.Url, "HTTPS://Example.com/a"));
        AssertUnable(UserDefaultType.Url, "not a url");
        AssertUnable(UserDefaultType.Url, 5);
    }

    [Fact]
    public void CoerceUserDefault_ArrayAndDictionaryBecomeElectronsJson()
    {
        Assert.Equal("[1,\"a\",true,null,2.5]", SystemPreferences.CoerceUserDefault(UserDefaultType.Array, new object?[] { 1, "a", true, null, 2.5 }));
        Assert.Equal("{\"k\":[1E+21]}", SystemPreferences.CoerceUserDefault(UserDefaultType.Dictionary, new Dictionary<string, object?> { ["k"] = new List<double> { 1e21 } }));
        Assert.Equal("{\"n\":3}", SystemPreferences.CoerceUserDefault(UserDefaultType.Dictionary, JsonNode.Parse("{\"n\":3.0}")));
        AssertUnable(UserDefaultType.Array, new Dictionary<string, object?>());
        AssertUnable(UserDefaultType.Array, "abc");
        AssertUnable(UserDefaultType.Dictionary, new[] { 1 });
        AssertUnable(UserDefaultType.Dictionary, new Dictionary<int, int> { [1] = 1 });
        AssertUnable(UserDefaultType.Array, new[] { double.NaN });
    }

    [Fact]
    public void CoerceUserDefault_UndefinedTypeIsInvalid()
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => SystemPreferences.CoerceUserDefault((UserDefaultType)99, "x"));
        Assert.StartsWith("Invalid type: 99", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5.0, "5")]
    [InlineData(-3.0, "-3")]
    [InlineData(5.5, "5.5")]
    [InlineData(1099511627776.0, "1099511627776.0")]
    [InlineData(2147483648.0, "2147483648.0")]
    [InlineData(2147483647.0, "2147483647")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1e21, "1E+21")]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    public void FormatJsonNumber_KeepsNonInt32NumbersDoubles(double value, string? expected) => Assert.Equal(expected, SystemPreferences.FormatJsonNumber(value));

    [Fact]
    public void ToElectronJson_WritesNestedValuesAndFailsOnANonFiniteNumber()
    {
        var node = SystemPreferences.ToValueNode(new Dictionary<string, object?>
        {
            ["s"] = "q\"uote",
            ["list"] = new object?[] { 1, 1.5, false, new Dictionary<string, object?> { ["x"] = null } },
        });
        Assert.Equal("{\"s\":\"q\\u0022uote\",\"list\":[1,1.5,false,{\"x\":null}]}", SystemPreferences.ToElectronJson(node));
        Assert.Null(SystemPreferences.ToElectronJson(SystemPreferences.ToValueNode(new Dictionary<string, object?> { ["a"] = new[] { 1.0, double.NegativeInfinity } })));
    }

    [Fact]
    public void ToValueNode_RejectsValuesWithoutAJsonShape()
    {
        Assert.Throws<ArgumentException>(() => SystemPreferences.ToValueNode(new object()));
        Assert.Throws<ArgumentException>(() => SystemPreferences.ToValueNode(new Dictionary<int, string> { [1] = "a" }));
        Assert.Equal(4.0, SystemPreferences.ToValueNode((byte)4)!.GetValue<double>());
        Assert.Equal("[2]", SystemPreferences.ToElectronJson(SystemPreferences.ToValueNode(System.Text.Json.JsonDocument.Parse("[2]").RootElement)));
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(-0.0, false)]
    [InlineData(1.5, false)]
    [InlineData(2147483647.0, true)]
    [InlineData(2147483648.0, false)]
    [InlineData(-2147483648.0, true)]
    [InlineData(-2147483649.0, false)]
    public void IsInt32_MatchesV8(double value, bool expected) => Assert.Equal(expected, SystemPreferences.IsInt32(value));

    [Fact]
    public void MacOnlyMembers_ThrowElsewhere()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.Throws<PlatformNotSupportedException>(() => SystemPreferences.GetSystemColor("blue"));
        Assert.Throws<PlatformNotSupportedException>(() => SystemPreferences.GetUserDefault("k", UserDefaultType.String));
        Assert.Throws<PlatformNotSupportedException>(() => SystemPreferences.SubscribeNotification(null, _ => { }));
        Assert.Throws<PlatformNotSupportedException>(() => SystemPreferences.IsTrustedAccessibilityClient(false));
        Assert.Throws<PlatformNotSupportedException>(() => { _ = SystemPreferences.AskForMediaAccessAsync(MediaAccessType.Camera); });
    }

    [Fact]
    public void Linux_HasNoGetColorOrMediaAccessStatus()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Throws<PlatformNotSupportedException>(() => SystemPreferences.GetColor("window"));
        Assert.Throws<PlatformNotSupportedException>(() => SystemPreferences.GetMediaAccessStatus(MediaAccessType.Camera));
    }

    private static void AssertUnable(UserDefaultType type, object? value)
    {
        var e = Assert.Throws<ArgumentException>(() => SystemPreferences.CoerceUserDefault(type, value));
        Assert.StartsWith("Unable to convert value to: " + SystemPreferences.UserDefaultTypeName(type), e.Message, StringComparison.Ordinal);
    }
}
