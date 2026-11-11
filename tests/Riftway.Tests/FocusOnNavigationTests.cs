namespace Riftway.Tests;

// Electron's focusOnNavigation: on by default; a window's own page cannot turn it off on Chrome-style CEF (drift W60).
public sealed class FocusOnNavigationTests
{
    [Fact]
    public void ItIsOnByDefault() => Assert.True(new WebPreferences().FocusOnNavigation);

    [Fact]
    public void AWindowsPage_RefusesFalse_WithTheReason()
    {
        var e = Assert.Throws<NotSupportedException>(() => new WebPreferences { FocusOnNavigation = false }.Validate("options"));
        Assert.Contains("Chrome-style browsers focus it on every navigation", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowsPage_AcceptsTrue() => new WebPreferences { FocusOnNavigation = true }.Validate("options");

    [Fact]
    public void AViewsPage_AcceptsFalse_ItNeverTakesFocusOnNavigationAnyway()
        => new WebPreferences { FocusOnNavigation = false }.Validate("options", windowPage: false);
}
