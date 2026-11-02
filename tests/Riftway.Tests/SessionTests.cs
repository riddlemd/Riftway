using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway.Tests;

public sealed class CookieFilterTests
{
    [Theory]
    [InlineData("riftway.test", "riftway.test", true)]
    [InlineData("riftway.test", ".riftway.test", true)]
    [InlineData(".riftway.test", "riftway.test", true)]
    [InlineData("riftway.test", "sub.riftway.test", true)]
    [InlineData("riftway.test", ".a.b.riftway.test", true)]
    [InlineData("RIFTWAY.test", "riftway.TEST", true)]
    [InlineData("sub.riftway.test", "riftway.test", false)]
    [InlineData("riftway.test", "notriftway.test", false)]
    [InlineData("riftway.test", "riftway.testing", false)]
    public void MatchesDomain_IsTheDomainOrASubdomain(string filter, string domain, bool matches)
        => Assert.Equal(matches, CookieFilter.MatchesDomain(filter, domain));

    private static readonly Cookie Sample = new("n", "v", ".riftway.test", "/p")
    {
        Secure = true, HttpOnly = false, Session = true,
    };

    [Fact]
    public void EmptyFilter_MatchesAnything() => Assert.True(new CookieFilter().Matches(Sample));

    [Fact]
    public void Url_IsNotFilteredHere() => Assert.True(new CookieFilter { Url = "https://elsewhere.test/" }.Matches(Sample));

    [Theory]
    [InlineData("n", true)]
    [InlineData("other", false)]
    public void Name(string name, bool matches) => Assert.Equal(matches, new CookieFilter { Name = name }.Matches(Sample));

    [Theory]
    [InlineData("/p", true)]
    [InlineData("/", false)]
    public void Path(string path, bool matches) => Assert.Equal(matches, new CookieFilter { Path = path }.Matches(Sample));

    [Theory]
    [InlineData("riftway.test", true)]
    [InlineData("other.test", false)]
    public void Domain(string domain, bool matches) => Assert.Equal(matches, new CookieFilter { Domain = domain }.Matches(Sample));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Secure(bool secure, bool matches) => Assert.Equal(matches, new CookieFilter { Secure = secure }.Matches(Sample));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SessionFlag(bool session, bool matches) => Assert.Equal(matches, new CookieFilter { Session = session }.Matches(Sample));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void HttpOnly(bool httpOnly, bool matches) => Assert.Equal(matches, new CookieFilter { HttpOnly = httpOnly }.Matches(Sample));
}

public sealed class CookieConversionTests
{
    [Fact]
    public void BaseTime_IsMicrosecondsSince1601()
    {
        Assert.Equal(0.0, Cookies.FromBaseTime(11_644_473_600_000_000));
        Assert.Equal(1_700_000_000.5, Cookies.FromBaseTime(11_644_473_600_000_000 + 1_700_000_000_500_000));
        Assert.Equal(11_644_473_600_000_000 + 1_700_000_000_500_000, Cookies.ToBaseTime(1_700_000_000.5));
    }

    [Fact]
    public void ToCookie_HostCookieWithoutExpiry()
    {
        var cookie = Cookies.ToCookie(new CefCookie("n", "v", "riftway.test", "/", false, true, null,
            cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_UNSPECIFIED));
        Assert.Equal(new Cookie("n", "v", "riftway.test", "/")
        {
            HostOnly = true, HttpOnly = true, Session = true, ExpirationDate = null, SameSite = CookieSameSite.Unspecified,
        }, cookie);
    }

    [Fact]
    public void ToCookie_DomainCookieWithExpiry()
    {
        var cookie = Cookies.ToCookie(new CefCookie("n", "v", ".riftway.test", "/p", true, false, Cookies.ToBaseTime(1_800_000_000),
            cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_STRICT_MODE));
        Assert.Equal(new Cookie("n", "v", ".riftway.test", "/p")
        {
            HostOnly = false, Secure = true, Session = false, ExpirationDate = 1_800_000_000, SameSite = CookieSameSite.Strict,
        }, cookie);
    }

    [Theory]
    [InlineData(CookieSameSite.Unspecified, cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_UNSPECIFIED)]
    [InlineData(CookieSameSite.NoRestriction, cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_NO_RESTRICTION)]
    [InlineData(CookieSameSite.Lax, cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_LAX_MODE)]
    [InlineData(CookieSameSite.Strict, cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_STRICT_MODE)]
    public void SameSite_RoundTrips(CookieSameSite sameSite, cef_cookie_same_site_t cef)
    {
        Assert.Equal(cef, Cookies.ToCef(sameSite));
        Assert.Equal(sameSite, Cookies.ToCookie(new CefCookie("n", "v", "d", "/", false, false, null, cef)).SameSite);
    }

    [Fact]
    public void CookieDetails_DefaultToLax() => Assert.Equal(CookieSameSite.Lax, new CookieDetails("https://riftway.test/").SameSite);
}

public sealed class SessionTests
{
    [Theory]
    [InlineData(ProxyMode.Direct, "direct")]
    [InlineData(ProxyMode.AutoDetect, "auto_detect")]
    [InlineData(ProxyMode.PacScript, "pac_script")]
    [InlineData(ProxyMode.FixedServers, "fixed_servers")]
    [InlineData(ProxyMode.System, "system")]
    public void ProxyModeName_IsChromiumsPrefValue(ProxyMode mode, string name) => Assert.Equal(name, Session.ProxyModeName(mode));

    [Theory]
    [InlineData("disk", "Partition disk")]
    [InlineData("a/b c", "Partition a%2Fb%20c")]
    public void PartitionDirectory_IsOneEscapedName(string name, string directory)
        => Assert.Equal(directory, Session.PartitionDirectory(name));

    [Fact]
    public void PartitionDirectory_NeedsAName() => Assert.Throws<ArgumentException>(() => Session.PartitionDirectory(""));

    [Fact]
    public void DefaultSession_NeedsAReadyApp() => Assert.Throws<InvalidOperationException>(() => Session.DefaultSession);

    [Fact]
    public void FromPartition_NeedsAReadyApp() => Assert.Throws<InvalidOperationException>(() => Session.FromPartition("memory"));
}
