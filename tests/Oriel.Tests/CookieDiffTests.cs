namespace Oriel.Tests;

// Causes follow Chromium 152's CookieMonster: kChangeCauseMapping, MaybeDeleteEquivalentCookieAndUpdateStatus and
// InternalInsertCookie (net/cookies/cookie_monster.cc).
public sealed class CookieDiffTests
{
    private const double Now = 1_000_000;

    private static CookieDiff.Stored Stored(string name, string value = "v", double? expires = null, long creation = 1, bool secure = false,
        bool httpOnly = false, CookieSameSite sameSite = CookieSameSite.Lax, string domain = "oriel.test", string path = "/")
        => new(new Cookie(name, value, domain, path) { Secure = secure, HttpOnly = httpOnly, SameSite = sameSite, ExpirationDate = expires, Session = expires is null }, creation);

    private static string Events(IEnumerable<CookieChangedEventArgs> events)
        => string.Join(" ", events.Select(e => $"{e.Cookie.Name}={e.Cookie.Value}:{e.Cause}:{(e.Removed ? "removed" : "added")}"));

    private static readonly CookieDiff.Key KeyA = new("a", "oriel.test", "/");

    [Fact]
    public void Unchanged_RaisesNothing() => Assert.Empty(CookieDiff.Compare([Stored("a")], [Stored("a")], Now));

    [Fact]
    public void NewCookie_IsInserted() => Assert.Equal("a=v:Inserted:added", Events(CookieDiff.Compare([], [Stored("a")], Now)));

    [Fact]
    public void NewValue_IsTheOldOverwrittenThenTheNewInserted()
        => Assert.Equal("a=v:Overwrite:removed a=w:Inserted:added", Events(CookieDiff.Compare([Stored("a")], [Stored("a", "w", creation: 2)], Now)));

    [Theory]
    [InlineData("secure")]
    [InlineData("httpOnly")]
    [InlineData("sameSite")]
    [InlineData("expires")]
    public void SameValueOtherAttribute_IsANoValueChangeOverwrite(string attribute)
    {
        var after = attribute switch
        {
            "secure" => Stored("a", secure: true),
            "httpOnly" => Stored("a", httpOnly: true),
            "sameSite" => Stored("a", sameSite: CookieSameSite.Strict),
            _ => Stored("a", expires: Now + 60),
        };
        Assert.Equal("a=v:Overwrite:removed a=v:InsertedNoValueChangeOverwrite:added", Events(CookieDiff.Compare([Stored("a")], [after], Now)));
    }

    [Fact]
    public void OnlyCreationChanged_IsANoChangeOverwrite()
        => Assert.Equal("a=v:Overwrite:removed a=v:InsertedNoChangeOverwrite:added", Events(CookieDiff.Compare([Stored("a")], [Stored("a", creation: 2)], Now)));

    [Fact]
    public void IdenticalCookieSetByOriel_IsANoChangeOverwrite()
        => Assert.Equal("a=v:Overwrite:removed a=v:InsertedNoChangeOverwrite:added",
            Events(CookieDiff.Compare([Stored("a")], [Stored("a")], Now, new CookieDiff.Set(KeyA))));

    [Fact]
    public void SetByOrielOfAnotherKey_LeavesUnchangedCookiesAlone()
        => Assert.Empty(CookieDiff.Compare([Stored("a")], [Stored("a")], Now, new CookieDiff.Set(KeyA with { Path = "/x" })));

    [Fact]
    public void CookieOrielSetThatVanished_WasAnExpiredOverwrite()
        => Assert.Equal("a=v:ExpiredOverwrite:removed", Events(CookieDiff.Compare([Stored("a", expires: Now - 1)], [], Now, new CookieDiff.Set(KeyA))));

    [Fact]
    public void CookieOrielRemoved_IsExplicit()
        => Assert.Equal("a=v:Explicit:removed", Events(CookieDiff.Compare([Stored("a", expires: Now - 60)], [], Now, new CookieDiff.Remove("a"))));

    [Fact]
    public void PastExpiry_IsExpired()
    {
        Assert.Equal("a=v:Expired:removed", Events(CookieDiff.Compare([Stored("a", expires: Now)], [], Now)));
        Assert.Equal("a=v:ExpiredOverwrite:removed", Events(CookieDiff.Compare([Stored("a", expires: Now + 1)], [], Now)));
    }

    [Fact]
    public void UnexplainedRemovalOfASessionCookie_IsAnExpiredOverwrite()
        => Assert.Equal("a=v:ExpiredOverwrite:removed", Events(CookieDiff.Compare([Stored("a")], [], Now, new CookieDiff.Remove("b"))));

    [Fact]
    public void Key_IsNameDomainAndPath()
        => Assert.Equal("a=v:ExpiredOverwrite:removed a=v:Inserted:added",
            Events(CookieDiff.Compare([Stored("a")], [Stored("a", domain: ".oriel.test")], Now)));

    [Fact]
    public void DuplicateKeys_TheFirstStandsForThem()
        => Assert.Empty(CookieDiff.Compare([Stored("a")], [Stored("a"), Stored("a", "other")], Now));
}
