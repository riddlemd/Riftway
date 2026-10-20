namespace Oriel.Tests;

// webPreferences.scrollBounce, decided per session (drift W55), with Oriel's patches 0010 and 0012.
public sealed class ScrollBounceTests
{
    [Theory]
    [InlineData(null, false, null)]
    [InlineData(null, true, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, null)]
    [InlineData(true, false, "webPreferences.scrollBounce true needs a session created with SessionOptions.ScrollBounce = true: Oriel decides it per session (drift W55).")]
    [InlineData(false, true, "webPreferences.scrollBounce false needs a session created with SessionOptions.ScrollBounce = false: Oriel decides it per session (drift W55).")]
    public void AWindowMustAgreeWithItsSession(bool? requested, bool session, string? error)
        => Assert.Equal(error, Session.ScrollBounceMismatch(requested, session));

    [Fact]
    public void AskingAnExistingSessionForAnotherScrollBounceIsAnError()
    {
        Assert.Null(Session.OptionsMismatch(true, new SessionOptions { ScrollBounce = true }, existingScrollBounce: true));
        Assert.Equal("The session already exists with SessionOptions.ScrollBounce = false.",
            Session.OptionsMismatch(true, new SessionOptions { ScrollBounce = true }, existingScrollBounce: false));
        Assert.Equal("The session already exists with SessionOptions.Sandbox = true.",
            Session.OptionsMismatch(true, new SessionOptions { Sandbox = false, ScrollBounce = true }, existingScrollBounce: false));
        Assert.Null(Session.OptionsMismatch(true, null, existingScrollBounce: true));
    }

    [Fact]
    public void TheDefaultIsElectronsNoBounce() => Assert.False(new SessionOptions().ScrollBounce);
}
