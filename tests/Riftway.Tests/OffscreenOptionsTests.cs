using Riftway;
using Xunit;

namespace Riftway.Tests;

public sealed class OffscreenOptionsTests
{
    [Fact]
    public void NoOffscreen_Validates() => new WebPreferences().Validate("options");

    [Fact]
    public void Offscreen_Validates() => new WebPreferences { Offscreen = new OffscreenOptions { DeviceScaleFactor = 2 } }.Validate("options");

    [Fact]
    public void SharedTexture_IsWindowsOnly()
    {
        var preferences = new WebPreferences { Offscreen = new OffscreenOptions { UseSharedTexture = true } };
        if (OperatingSystem.IsWindows())
        {
            preferences.Validate("options");
            return;
        }
        var e = Assert.Throws<NotSupportedException>(() => preferences.Validate("options"));
        Assert.Contains("C63", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DeviceScaleFactor_MustBePositive(double scale)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new WebPreferences { Offscreen = new OffscreenOptions { DeviceScaleFactor = scale } }.Validate("options"));
}
