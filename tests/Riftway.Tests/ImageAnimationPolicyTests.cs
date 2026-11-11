namespace Riftway.Tests;

// Electron's imageAnimationPolicy (webPreferences and webContents.setImageAnimationPolicy): animate, animateOnce or
// noAnimation; anything else is ignored. Chromium's profile preference names them allowed, once and none (drift C53).
public sealed class ImageAnimationPolicyTests
{
    [Fact]
    public void ThePreferenceIsUnsetByDefault_SoAPageLeavesItsSessionAlone()
        => Assert.Null(new WebPreferences().ImageAnimationPolicy);

    [Theory]
    [InlineData("animate", "allowed")]
    [InlineData("animateOnce", "once")]
    [InlineData("noAnimation", "none")]
    public void ElectronsPolicies_MapToChromiumsPreferenceValues(string policy, string expected)
        => Assert.Equal(expected, WebContents.ChromiumAnimationPolicy(policy));

    [Theory]
    [InlineData("")]
    [InlineData("Animate")]
    [InlineData("once")]
    [InlineData("none")]
    [InlineData("allowed")]
    public void AnythingElse_IsIgnored_AsElectrons(string policy)
        => Assert.Null(WebContents.ChromiumAnimationPolicy(policy));
}
