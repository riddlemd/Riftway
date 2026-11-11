using Riftway;
using Xunit;

namespace Riftway.Tests;

public sealed class UnsupportedPreferenceTests
{
    [Fact]
    public void HtmlFullscreenWindowResize_DefaultValidates() => new WebPreferences().Validate("options");

    [Fact]
    public void DisableHtmlFullscreenWindowResize_IsNotSupported()
    {
        var e = Assert.Throws<NotSupportedException>(() => new WebPreferences { DisableHtmlFullscreenWindowResize = true }.Validate("options"));
        Assert.Contains("W64", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PaintWhenInitiallyHidden_DefaultsToElectrons()
    {
        Assert.True(new BrowserWindowOptions().PaintWhenInitiallyHidden);
        new BrowserWindowOptions().Validate();
    }

    [Fact]
    public void PaintWhenInitiallyHiddenFalse_IsNotSupported()
    {
        var e = Assert.Throws<NotSupportedException>(() => new BrowserWindowOptions { PaintWhenInitiallyHidden = false }.Validate());
        Assert.Contains("W63", e.Message, StringComparison.Ordinal);
    }
}
