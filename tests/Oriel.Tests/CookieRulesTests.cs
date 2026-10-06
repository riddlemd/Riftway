namespace Oriel.Tests;

// Expected messages follow Electron's InclusionStatusToString (shell/browser/api/electron_api_cookies.cc, v44.5.1) over
// Chromium 152's CanonicalCookie::CreateSanitizedCookie (net/cookies/canonical_cookie.cc) and
// CookieBase::IsSetPermittedInContext (net/cookies/cookie_base.cc).
public sealed class CookieRulesTests
{
    private const string Prefix = "Failed to set cookie - ";
    private const string InvalidDomain = "The cookie was set with an invalid Domain attribute.";
    private const string Tail = "DO_NOT_WARN, NO_EXEMPTION";

    private static CookieRules.Verdict Check(string url, string name = "n", string value = "v", string? domain = null, string? path = null,
        bool secure = false, bool httpOnly = false, CookieSameSite sameSite = CookieSameSite.Lax)
        => CookieRules.Check(url, name, value, domain, path, secure, httpOnly, sameSite);

    [Fact]
    public void ValidHostCookie_Passes_WithTheHostAndTheUrlDirectory()
    {
        var verdict = Check("https://Oriel.test/app/page");
        Assert.Equal(new CookieRules.Verdict(null, "oriel.test", "/app"), verdict);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative")]
    [InlineData(@"\\server\share")]
    [InlineData("")]
    public void InvalidUrl_IsAnInvalidDomain(string url)
        => Assert.Equal($"{Prefix}{InvalidDomain}EXCLUDE_INVALID_DOMAIN, {Tail}", Check(url).Rejection);

    [Fact]
    public void DomainOfAnotherSite_IsAMismatchAndInvalid()
        => Assert.Equal($"{Prefix}The cookie's domain attribute did not match the domain of the URL attempting access.{InvalidDomain}"
            + $"EXCLUDE_DOMAIN_MISMATCH, EXCLUDE_INVALID_DOMAIN, {Tail}", Check("https://oriel.test/", domain: "elsewhere.test").Rejection);

    [Fact]
    public void SiblingDomainOfTheSameSite_IsOnlyInvalid()
        => Assert.Equal($"{Prefix}{InvalidDomain}EXCLUDE_INVALID_DOMAIN, {Tail}", Check("https://a.oriel.test/", domain: "b.oriel.test").Rejection);

    [Theory]
    [InlineData("oriel.test")]
    [InlineData(".oriel.test")]
    [InlineData("ORIEL.test")]
    public void ParentDomain_MakesADomainCookie(string domain)
        => Assert.Equal(new CookieRules.Verdict(null, ".oriel.test", "/"), Check("https://sub.oriel.test/", domain: domain));

    [Fact]
    public void PublicSuffixAsDomain_IsRejected()
        => Assert.Contains("EXCLUDE_DOMAIN_MISMATCH, EXCLUDE_INVALID_DOMAIN", Check("https://oriel.test/", domain: "test").Rejection);

    [Fact]
    public void IpHost_TakesOnlyItsOwnAddressAsDomain()
    {
        Assert.Equal(new CookieRules.Verdict(null, "127.0.0.1", "/"), Check("http://127.0.0.1/", domain: "127.0.0.1"));
        Assert.Equal($"{Prefix}{InvalidDomain}EXCLUDE_INVALID_DOMAIN, {Tail}", Check("http://127.0.0.1/", domain: "0.1").Rejection);
    }

    [Fact]
    public void SingleLabelHost_TakesOnlyItselfAsDomain()
    {
        Assert.Equal(new CookieRules.Verdict(null, "localhost", "/"), Check("http://localhost/", domain: ".localhost"));
        Assert.Equal($"{Prefix}{InvalidDomain}EXCLUDE_INVALID_DOMAIN, {Tail}", Check("http://localhost/", domain: "other").Rejection);
    }

    [Fact]
    public void DomainWithASemicolon_IsInvalidAndIgnored()
    {
        var verdict = Check("https://oriel.test/", domain: "oriel.test;x");
        Assert.Equal($"{Prefix}{InvalidDomain}EXCLUDE_INVALID_DOMAIN, {Tail}", verdict.Rejection);
        Assert.Equal("oriel.test", verdict.Domain);
    }
    [Fact]
    public void PercentInDomain_IsInvalid()
        => Assert.Equal($"{Prefix}{InvalidDomain}EXCLUDE_INVALID_DOMAIN, {Tail}", Check("https://oriel.test/", domain: "oriel%2etest").Rejection);

    [Fact]
    public void ForbiddenHostCharacterInDomain_IsInvalid()
        => Assert.Equal($"{Prefix}{InvalidDomain}EXCLUDE_INVALID_DOMAIN, {Tail}", Check("https://oriel.test/", domain: "oriel test").Rejection);

    [Fact]
    public void NonAsciiDomain_WarnsAndIsPunycoded()
    {
        var verdict = Check("https://xn--bcher-kva.test/", domain: "bücher.test", secure: true, sameSite: CookieSameSite.NoRestriction);
        Assert.Equal(new CookieRules.Verdict(null, ".xn--bcher-kva.test", "/"), verdict);
        Assert.Equal($"{Prefix}The cookie's domain attribute did not match the domain of the URL attempting access.{InvalidDomain}EXCLUDE_DOMAIN_MISMATCH, EXCLUDE_INVALID_DOMAIN, WARN_DOMAIN_NON_ASCII, NO_EXEMPTION",
            Check("https://oriel.test/", domain: "bücher.test").Rejection);
    }

    private const string Disallowed = $"{Prefix}The cookie contains ASCII control charactersEXCLUDE_DISALLOWED_CHARACTER, {Tail}";

    [Theory]
    [InlineData("a;b", "v")]
    [InlineData("a=b", "v")]
    [InlineData(" n", "v")]
    [InlineData("n\t", "v")]
    [InlineData("n\u0001", "v")]
    [InlineData("n", "v;w")]
    [InlineData("n", " v")]
    [InlineData("n", "v\u007f")]
    [InlineData("n", "v\rw")]
    public void BadNameOrValue_IsADisallowedCharacter(string name, string value) => Assert.Equal(Disallowed, Check("https://oriel.test/", name, value).Rejection);

    [Fact]
    public void BadPathCharacter_IsADisallowedCharacter() => Assert.Equal(Disallowed, Check("https://oriel.test/", path: "/a;b").Rejection);

    [Fact]
    public void NoNameAndNoValue_IsNoContent()
        => Assert.Equal($"{Prefix}The cookie contains no content or only whitespace.EXCLUDE_NO_COOKIE_CONTENT, {Tail}", Check("https://oriel.test/", "", "").Rejection);

    [Fact]
    public void NameAndValueOver4096Bytes_ExceedTheLimit()
    {
        Assert.Null(Check("https://oriel.test/", "n", new string('v', 4095)).Rejection);
        Assert.Equal($"{Prefix}The cookie exceeded the name/value pair size limit.EXCLUDE_NAME_VALUE_PAIR_EXCEEDS_MAX_SIZE, {Tail}",
            Check("https://oriel.test/", "n", new string('v', 4096)).Rejection);
    }

    [Fact]
    public void NameAndValueSize_CountsUtf8Bytes()
        => Assert.Equal($"{Prefix}The cookie exceeded the name/value pair size limit.EXCLUDE_NAME_VALUE_PAIR_EXCEEDS_MAX_SIZE, {Tail}",
            Check("https://oriel.test/", "n", new string('é', 2048)).Rejection);

    [Fact]
    public void PathNotStartingWithSlash_IsInvalid()
        => Assert.Equal($"{Prefix}The cookie was set with an invalid Path attribute.EXCLUDE_INVALID_PATH, {Tail}", Check("https://oriel.test/", path: "app").Rejection);

    [Fact]
    public void PathOver1024Bytes_ExceedsTheAttributeLimit()
    {
        Assert.Null(Check("https://oriel.test/", path: "/" + new string('p', 1023)).Rejection);
        Assert.Equal($"{Prefix}Cookie exceeded the attribute size limit.EXCLUDE_ATTRIBUTE_VALUE_EXCEEDS_MAX_SIZE, {Tail}",
            Check("https://oriel.test/", path: "/" + new string('p', 1024)).Rejection);
    }

    [Fact]
    public void PathSize_CountsPercentEncoding()
        => Assert.Equal($"{Prefix}Cookie exceeded the attribute size limit.EXCLUDE_ATTRIBUTE_VALUE_EXCEEDS_MAX_SIZE, {Tail}",
            Check("https://oriel.test/", path: "/a" + new string(' ', 400) + "b").Rejection);

    [Fact]
    public void DomainOver1024Bytes_ExceedsTheLimitAndIsIgnored()
    {
        var verdict = Check("https://oriel.test/", domain: new string('d', 1025));
        Assert.Equal($"{Prefix}Cookie exceeded the attribute size limit.EXCLUDE_ATTRIBUTE_VALUE_EXCEEDS_MAX_SIZE, {Tail}", verdict.Rejection);
        Assert.Equal("oriel.test", verdict.Domain);
    }

    private const string BadPrefix = $"{Prefix}The cookie was set with an invalid __Host- or __Secure- prefix.EXCLUDE_INVALID_PREFIX, ";

    [Theory]
    [InlineData("__Secure-a", false, false, null, null)]
    [InlineData("__secure-a", false, false, null, null)]
    [InlineData("__Host-a", true, false, null, "/app")]
    [InlineData("__Host-a", true, false, "oriel.test", null)]
    [InlineData("__Host-a", false, false, null, null)]
    [InlineData("__Http-a", true, false, null, null)]
    [InlineData("__Host-Http-a", true, false, null, null)]
    [InlineData("__Host-Http-a", true, true, null, "/app")]
    public void PrefixWithoutItsAttributes_IsInvalid(string name, bool secure, bool httpOnly, string? domain, string? path)
        => Assert.Equal(BadPrefix + Tail, Check("https://oriel.test/", name, domain: domain, path: path, secure: secure, httpOnly: httpOnly).Rejection);

    [Theory]
    [InlineData("__Secure-a", false)]
    [InlineData("__Host-a", false)]
    [InlineData("__Http-a", true)]
    [InlineData("__Host-Http-a", true)]
    public void PrefixWithItsAttributes_Passes(string name, bool httpOnly)
        => Assert.Null(Check("https://oriel.test/", name, path: "/", secure: true, httpOnly: httpOnly).Rejection);

    [Fact]
    public void SecurePrefixOnPlainHttp_IsInvalid_AndWarnsOfTheSecureSourceScheme()
        => Assert.Equal(BadPrefix + "WARN_TENTATIVELY_ALLOWING_SECURE_SOURCE_SCHEME, NO_EXEMPTION",
            Check("http://oriel.test/", "__Secure-a", secure: true).Rejection);

    [Fact]
    public void SecurePrefixOnLocalhost_Passes() => Assert.Null(Check("http://localhost/", "__Secure-a", secure: true).Rejection);

    [Fact]
    public void NamelessValueWithAPrefix_IsInvalid() => Assert.Equal(BadPrefix + Tail, Check("https://oriel.test/", "", "__Host-a").Rejection);

    [Fact]
    public void NamelessValueWithEquals_IsAmbiguous()
        => Assert.Equal($"{Prefix}Cookie was rejected in parsing due to having an ambiguous serialization.EXCLUDE_AMBIGUOUS_SERIALIZATION, {Tail}",
            Check("https://oriel.test/", "", "a=b").Rejection);

    [Fact]
    public void SeveralReasons_AreListedInChromiumsOrder()
        => Assert.Equal($"{Prefix}{InvalidDomain}The cookie contains ASCII control characters"
            + $"EXCLUDE_INVALID_DOMAIN, EXCLUDE_DISALLOWED_CHARACTER, {Tail}", Check("https://a.oriel.test/", "a;b", domain: "b.oriel.test").Rejection);

    [Fact]
    public void SecureCookieOnPlainHttp_IsRefusedByTheStore()
        => Assert.Equal($"{Prefix}The cookie was Secure, but the URL was not allowed to access Secure cookies.EXCLUDE_SECURE_ONLY, {Tail}",
            Check("http://oriel.test/", secure: true).Rejection);

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://app.localhost/")]
    [InlineData("http://127.0.0.2/")]
    [InlineData("http://[::1]/")]
    public void SecureCookieOnLocalhost_IsAllowed(string url) => Assert.Null(Check(url, secure: true).Rejection);

    [Fact]
    public void SameSiteNoneWithoutSecure_IsRefusedByTheStore()
        => Assert.Equal($"{Prefix}The cookie specified SameSite=None, but it was not Secure.EXCLUDE_SAMESITE_NONE_INSECURE, {Tail}",
            Check("https://oriel.test/", sameSite: CookieSameSite.NoRestriction).Rejection);

    [Fact]
    public void FailureToStore_IsElectronsFallbackMessage()
        => Assert.Equal($"{Prefix}The cookie was malformed and could not be storedEXCLUDE_FAILURE_TO_STORE, {Tail}", CookieRules.FailureToStoreMessage);

    [Theory]
    [InlineData(null, CookieSameSite.NoRestriction, true)]
    [InlineData(null, CookieSameSite.Lax, false)]
    [InlineData(false, CookieSameSite.NoRestriction, false)]
    [InlineData(true, CookieSameSite.Strict, true)]
    public void Secure_DefaultsToSameSiteNone(bool? secure, CookieSameSite sameSite, bool expected)
        => Assert.Equal(expected, new CookieDetails("https://oriel.test/") { Secure = secure, SameSite = sameSite }.SecureOrDefault);
}
