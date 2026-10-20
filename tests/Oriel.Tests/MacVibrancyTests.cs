namespace Oriel.Tests;

// Electron v44's NativeWindowMac::SetVibrancy material table, its visual effect states, and NativeWindow::IsTranslucent.
public sealed class MacVibrancyTests
{
    [Theory]
    [InlineData("titlebar", 3L)]
    [InlineData("selection", 4L)]
    [InlineData("menu", 5L)]
    [InlineData("popover", 6L)]
    [InlineData("sidebar", 7L)]
    [InlineData("header", 10L)]
    [InlineData("sheet", 11L)]
    [InlineData("window", 12L)]
    [InlineData("hud", 13L)]
    [InlineData("fullscreen-ui", 15L)]
    [InlineData("tooltip", 17L)]
    [InlineData("content", 18L)]
    [InlineData("under-window", 21L)]
    [InlineData("under-page", 22L)]
    [InlineData("appearance-based", null)]
    [InlineData("Sidebar", null)]
    public void TypesAreElectronsMaterials(string type, long? material) => Assert.Equal(material, MacVibrancy.Material(type));

    [Theory]
    [InlineData(VisualEffectState.FollowWindow, 0L)]
    [InlineData(VisualEffectState.Active, 1L)]
    [InlineData(VisualEffectState.Inactive, 2L)]
    public void StatesAreAppKits(VisualEffectState state, long value) => Assert.Equal(value, MacVibrancy.State(state));

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    [InlineData(true, null, true)]
    [InlineData(false, "sidebar", true)]
    [InlineData(false, "nonsense", true)]
    public void AnyVibrancyMakesTheWindowTranslucent(bool transparent, string? vibrancy, bool translucent)
        => Assert.Equal(translucent, MacVibrancy.IsTranslucent(transparent, vibrancy));
}

public sealed class MacVibrancyRadiusTests
{
    [Theory]
    [InlineData(true, false, false, false, 9.0)]
    [InlineData(true, true, true, false, 9.0)]
    [InlineData(true, true, false, false, 9.0)]
    [InlineData(true, false, false, true, 0.0)]
    [InlineData(true, false, true, false, null)]
    [InlineData(false, false, false, false, null)]
    [InlineData(false, true, true, false, null)]
    public void OnlyTitledModalOrFramelessWindowsAreRounded(bool titled, bool modal, bool hasFrame, bool fullScreen, double? radius)
        => Assert.Equal(radius, MacVibrancy.CornerRadius(titled, modal, hasFrame, fullScreen));
}
