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
    public void SharedTexture_IsNotSupported()
    {
        var e = Assert.Throws<NotSupportedException>(() => new WebPreferences { Offscreen = new OffscreenOptions { UseSharedTexture = true } }.Validate("options"));
        Assert.Contains("C63", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DeviceScaleFactor_MustBePositive(double scale)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new WebPreferences { Offscreen = new OffscreenOptions { DeviceScaleFactor = scale } }.Validate("options"));
}
