using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class LinuxThemeTests
{
    private static readonly LinuxPortalAppearance Portal = new(true, 2u, (0.2, 0.4, 0.6));

    [Theory]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(0u, null)]
    [InlineData(3u, null)]
    [InlineData(null, null)]
    public void PortalColorScheme_MapsAsChromium(uint? scheme, bool? dark)
        => Assert.Equal(dark, LinuxPortalSettings.PrefersDark(scheme));

    [Theory]
    [InlineData(0.0, 0.5, 1.0, true)]
    [InlineData(1.0, 1.0, 1.0, true)]
    [InlineData(-0.01, 0.5, 0.5, false)]
    [InlineData(0.5, 1.01, 0.5, false)]
    [InlineData(0.5, 0.5, double.NaN, false)]
    public void Accent_IsValidOnlyInsideZeroToOne(double r, double g, double b, bool valid)
        => Assert.Equal(valid ? (r, g, b) : null, LinuxPortalSettings.ValidAccent(r, g, b));

    [Fact]
    public void SettingChanged_UpdatesColorSchemeAndAccent()
    {
        Assert.Equal(Portal with { ColorScheme = 1u }, LinuxPortalSettings.Apply(Portal, "org.freedesktop.appearance", "color-scheme", 1u));
        Assert.Equal(Portal with { Accent = (1.0, 0.0, 0.5) },
            LinuxPortalSettings.Apply(Portal, "org.freedesktop.appearance", "accent-color", (1.0, 0.0, 0.5)));
        Assert.Equal(Portal with { Accent = null }, LinuxPortalSettings.Apply(Portal, "org.freedesktop.appearance", "accent-color", (2.0, 0.0, 0.5)));
    }

    [Fact]
    public void SettingChanged_IgnoresOtherNamespacesKeysAndTypes()
    {
        Assert.Same(Portal, LinuxPortalSettings.Apply(Portal, "org.gnome.desktop.interface", "color-scheme", 1u));
        Assert.Same(Portal, LinuxPortalSettings.Apply(Portal, "org.freedesktop.appearance", "contrast", 1u));
        Assert.Same(Portal, LinuxPortalSettings.Apply(Portal, "org.freedesktop.appearance", "color-scheme", 1));
        Assert.Same(Portal, LinuxPortalSettings.Apply(Portal, "org.freedesktop.appearance", "color-scheme", null));
        Assert.Same(Portal, LinuxPortalSettings.Apply(Portal, "org.freedesktop.appearance", "accent-color", 1u));
    }

    [Fact]
    public void WithoutGtk_NothingIsDark()
        => Assert.False(NativeTheme.LinuxPrefersDark(false, Portal with { ColorScheme = 1u }, () => true));

    [Theory]
    [InlineData(1u, false, true)]
    [InlineData(2u, true, false)]
    [InlineData(0u, true, true)]
    [InlineData(0u, false, false)]
    [InlineData(null, true, true)]
    public void PortalPreference_WinsOverGtkBackground(uint? scheme, bool backgroundDark, bool dark)
        => Assert.Equal(dark, NativeTheme.LinuxPrefersDark(true, Portal with { ColorScheme = scheme }, () => backgroundDark));

    [Theory]
    [InlineData("HighContrast", true)]
    [InlineData("HighContrastInverse", true)]
    [InlineData("ContrastHighInverse", true)]
    [InlineData("my-high-contrast", true)]
    [InlineData("Adwaita", false)]
    [InlineData("High", false)]
    [InlineData("Contrast", false)]
    [InlineData(null, false)]
    public void HighContrast_IsAThemeNamedHighAndContrast(string? name, bool high)
        => Assert.Equal(high, LinuxGtkTheme.IsHighContrastThemeName(name));

    [Fact]
    public void AveragePixel_IsCairosPremultipliedAverage()
    {
        Assert.Equal(0u, LinuxGtkTheme.AveragePixel([0u, 0u]));
        Assert.Equal(0xFF336699u, LinuxGtkTheme.AveragePixel([0xFF336699u, 0xFF336699u]));
        // Premultiplied half-alpha red unpremultiplies to full red; alpha is the plain mean.
        Assert.Equal(0x40FF0000u, LinuxGtkTheme.AveragePixel([0x80800000u, 0x00000000u]));
        Assert.Equal(0xFF7F007Fu, LinuxGtkTheme.AveragePixel([0xFFFF0000u, 0xFF0000FFu]));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, false)]
    [InlineData(0xFF000000u, true)]
    [InlineData(0xFFF6F5F4u, false)] // Adwaita
    [InlineData(0xFF353535u, true)] // Adwaita dark
    [InlineData(0xFF7F7F7Fu, false)]
    [InlineData(0xFF7E7E7Eu, true)]
    [InlineData(0xFF00FF00u, false)]
    [InlineData(0xFF0000FFu, true)]
    [InlineData(0xFF007EEFu, true)] // Dark only through sRGB's linear segment for its zero red.
    public void IsDark_IsRelativeLuminanceBelowChromiumsMidpoint(uint argb, bool dark)
        => Assert.Equal(dark, LinuxGtkTheme.IsDark(argb));
}
