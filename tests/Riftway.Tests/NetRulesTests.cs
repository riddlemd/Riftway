using Riftway.Cef.Interop;

namespace Riftway.Tests;

public sealed class NetHeaderRulesTests
{
    [Theory]
    [InlineData("X-Custom", true)]
    [InlineData("!#$%&'*+-.^_`|~09az", true)]
    [InlineData("", false)]
    [InlineData("bad name", false)]
    [InlineData("tab\tname", false)]
    [InlineData("caf\u00e9", false)]
    [InlineData("del\u007f", false)]
    [InlineData("a:b", false)]
    [InlineData("a/b", false)]
    [InlineData("a{b", false)]
    public void IsValidHeaderName_IsAToken(string name, bool valid) => Assert.Equal(valid, NetRules.IsValidHeaderName(name));

    [Theory]
    [InlineData("plain value", true)]
    [InlineData("tab\tok", true)]
    [InlineData("a\rb", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\0b", false)]
    public void IsValidHeaderValue_RejectsNulCrLf(string value, bool valid) => Assert.Equal(valid, NetRules.IsValidHeaderValue(value));

    [Fact]
    public void ValidateHeader_NullName() => Assert.Equal("`name` should be a string in setHeader(name, value)",
        Assert.Throws<ArgumentException>(() => NetRules.ValidateHeader(null, "v")).Message);

    [Fact]
    public void ValidateHeader_NullValue() => Assert.Equal("`value` required in setHeader(\"X-A\", value)",
        Assert.Throws<ArgumentException>(() => NetRules.ValidateHeader("X-A", null)).Message);

    [Fact]
    public void ValidateHeader_InvalidName() => Assert.Equal("Invalid header name: 'a b'",
        Assert.Throws<ArgumentException>(() => NetRules.ValidateHeader("a b", "v")).Message);

    [Fact]
    public void ValidateHeader_InvalidValue() => Assert.Equal("Invalid value for header 'X-A': 'a\rb'",
        Assert.Throws<ArgumentException>(() => NetRules.ValidateHeader("X-A", "a\rb")).Message);

    [Fact]
    public void ValidateHeader_AcceptsAValidHeader()
    {
        NetRules.ValidateHeader("X-A", "v");
        Assert.True(NetRules.IsValidHeaderName("X-A"));
    }

    [Fact]
    public void GroupHeaders_LowerCasesAndKeepsEveryValueInOrder()
    {
        var grouped = NetRules.GroupHeaders([new("X-Multi", "1"), new("x-multi", "2"), new("Location", "/a")]);
        Assert.Equal(["1", "2"], grouped["x-multi"]);
        Assert.Equal(["/a"], grouped["location"]);
        Assert.False(grouped.ContainsKey("Location"));
    }

    [Fact]
    public void FlattenHeaders_DiscardableKeepsTheFirst()
        => Assert.Equal(["text/plain"], NetRules.FlattenHeaders([new("Content-Type", "text/plain"), new("Content-Type", "text/html")])["content-type"]);

    [Fact]
    public void FlattenHeaders_OthersAreJoined()
        => Assert.Equal(["1, 2"], NetRules.FlattenHeaders([new("X-Multi", "1"), new("X-Multi", "2")])["x-multi"]);

    [Fact]
    public void FlattenHeaders_SetCookieIsEveryValue()
        => Assert.Equal(["a=1", "b=2"], NetRules.FlattenHeaders([new("Set-Cookie", "a=1"), new("set-cookie", "b=2")])["set-cookie"]);

    [Fact]
    public void FlattenHeaders_NoSetCookieEntryWithoutCookies()
        => Assert.False(NetRules.FlattenHeaders([new("X-A", "1")]).ContainsKey("set-cookie"));
}

public sealed class NetOptionRulesTests
{
    [Fact]
    public void ErrorString_NamesTheCode() => Assert.Equal("net::ERR_CONNECTION_REFUSED", NetRules.ErrorString(-102));

    [Fact]
    public void ErrorString_Zero() => Assert.Equal("net::OK", NetRules.ErrorString(0));

    [Fact]
    public void ErrorString_UnknownCode() => Assert.Equal("net::ERR_<unknown>", NetRules.ErrorString(-99999));

    [Fact]
    public void BuildUrl_UrlWins() => Assert.Equal("https://a.test/x", NetRules.BuildUrl(new ClientRequestOptions { Url = "https://a.test/x", Hostname = "b" }));

    [Fact]
    public void BuildUrl_DefaultsToHttpLocalhostRoot() => Assert.Equal("http://localhost/", NetRules.BuildUrl(new ClientRequestOptions()));

    [Fact]
    public void BuildUrl_HostnamePortProtocolPath()
        => Assert.Equal("https://h.test:8443/p/q?x=1#f",
            NetRules.BuildUrl(new ClientRequestOptions { Protocol = "https:", Hostname = "h.test", Port = 8443, Path = "/p/q?x=1#f" }));

    [Fact]
    public void BuildUrl_HostWinsOverHostnameAndPort()
        => Assert.Equal("http://h.test:81/", NetRules.BuildUrl(new ClientRequestOptions { Host = "h.test:81", Hostname = "other", Port = 9 }));

    [Fact]
    public void BuildUrl_PortZeroIsOmitted() => Assert.Equal("http://h.test/", NetRules.BuildUrl(new ClientRequestOptions { Hostname = "h.test", Port = 0 }));

    [Fact]
    public void BuildUrl_PathWithSpaceThrows() => Assert.Equal("Request path contains unescaped characters",
        Assert.Throws<ArgumentException>(() => NetRules.BuildUrl(new ClientRequestOptions { Path = "/a b" })).Message);

    [Theory]
    [InlineData(null, "GET")]
    [InlineData("", "GET")]
    [InlineData("post", "POST")]
    public void NormalizeMethod_DefaultsAndUpperCases(string? method, string expected) => Assert.Equal(expected, NetRules.NormalizeMethod(method));

    [Fact]
    public void RequestFlags_AlwaysStopOnRedirect()
        => Assert.True(NetRules.RequestFlags(RequestCredentials.Omit, null).HasFlag(cef_urlrequest_flags_t.UR_FLAG_STOP_ON_REDIRECT));

    [Theory]
    [InlineData(null, true)]
    [InlineData(RequestCredentials.Include, true)]
    [InlineData(RequestCredentials.SameOrigin, true)]
    [InlineData(RequestCredentials.Omit, false)]
    public void RequestFlags_CredentialsUnlessOmit(RequestCredentials? credentials, bool allowed)
        => Assert.Equal(allowed, NetRules.RequestFlags(credentials, null).HasFlag(cef_urlrequest_flags_t.UR_FLAG_ALLOW_STORED_CREDENTIALS));

    [Theory]
    [InlineData(RequestCache.NoStore, cef_urlrequest_flags_t.UR_FLAG_DISABLE_CACHE)]
    [InlineData(RequestCache.Reload, cef_urlrequest_flags_t.UR_FLAG_SKIP_CACHE)]
    [InlineData(RequestCache.OnlyIfCached, cef_urlrequest_flags_t.UR_FLAG_ONLY_FROM_CACHE)]
    public void RequestFlags_CacheModes(RequestCache cache, cef_urlrequest_flags_t flag)
        => Assert.Equal(flag, NetRules.RequestFlags(RequestCredentials.Omit, cache) & ~cef_urlrequest_flags_t.UR_FLAG_STOP_ON_REDIRECT);

    [Theory]
    [InlineData(RequestCache.Default)]
    [InlineData(RequestCache.NoCache)]
    [InlineData(RequestCache.ForceCache)]
    public void RequestFlags_CacheModesWithoutAFlag(RequestCache cache)
        => Assert.Equal(cef_urlrequest_flags_t.UR_FLAG_STOP_ON_REDIRECT, NetRules.RequestFlags(RequestCredentials.Omit, cache));

    [Fact]
    public void ReferrerPolicy_DefaultIsStrictOriginWhenCrossOrigin()
        => Assert.Equal(cef_referrer_policy_t.REFERRER_POLICY_REDUCE_REFERRER_GRANULARITY_ON_TRANSITION_CROSS_ORIGIN, NetRules.ReferrerPolicyOf(null));

    [Fact]
    public void ReferrerPolicy_OriginWhenCrossOrigin()
        => Assert.Equal(cef_referrer_policy_t.REFERRER_POLICY_ORIGIN_ONLY_ON_TRANSITION_CROSS_ORIGIN, NetRules.ReferrerPolicyOf(ReferrerPolicy.OriginWhenCrossOrigin));
}

public sealed class NetRedirectRulesTests
{
    [Theory]
    [InlineData("POST", 303, "GET")]
    [InlineData("PUT", 303, "GET")]
    [InlineData("HEAD", 303, "HEAD")]
    [InlineData("POST", 301, "GET")]
    [InlineData("POST", 302, "GET")]
    [InlineData("PUT", 302, "PUT")]
    [InlineData("POST", 307, "POST")]
    [InlineData("POST", 308, "POST")]
    public void RedirectMethod_FollowsChromium(string method, int status, string expected) => Assert.Equal(expected, NetRules.RedirectMethod(method, status));

    [Fact]
    public void RedirectUrl_ResolvesRelative() => Assert.Equal("http://a.test/b/c", NetRules.RedirectUrl("http://a.test/b/x", "c"));

    [Fact]
    public void RedirectUrl_CopiesTheOldFragment() => Assert.Equal("http://a.test/n#frag", NetRules.RedirectUrl("http://a.test/o#frag", "/n"));

    [Fact]
    public void RedirectUrl_KeepsTheNewFragment() => Assert.Equal("http://a.test/n#new", NetRules.RedirectUrl("http://a.test/o#frag", "/n#new"));

    [Fact]
    public void RedirectUrl_InvalidBaseIsNull() => Assert.Null(NetRules.RedirectUrl("not a url", "/n"));

    [Theory]
    [InlineData("http://a.test/", "https://b.test/", true)]
    [InlineData("http://a.test/", "app://x/", true)]
    [InlineData("http://a.test/", "file:///c:/", false)]
    [InlineData("http://a.test/", "data:text/plain,x", false)]
    [InlineData("http://a.test/", "about:blank", false)]
    [InlineData("http://a.test/", "chrome://settings/", false)]
    [InlineData("file:///c:/a", "file:///c:/b", true)]
    [InlineData("file:///c:/a", "chrome://settings/", false)]
    [InlineData("http://a.test/", "not a url", false)]
    public void IsSafeRedirectTarget_FollowsContent(string from, string to, bool safe) => Assert.Equal(safe, NetRules.IsSafeRedirectTarget(from, to));

    private static readonly List<KeyValuePair<string, string>> Headers =
        [new("Content-Type", "text/plain"), new("Origin", "http://a.test"), new("Authorization", "Basic x"), new("X-Keep", "1"), new("Content-Language", "en")];

    [Fact]
    public void RedirectHeaders_MethodChangeDropsBodyHeadersAndBody()
    {
        var (kept, clear) = NetRules.RedirectHeaders(Headers, "POST", "GET", "http://a.test/1", "http://a.test/2");
        Assert.True(clear);
        Assert.Equal(["Authorization", "X-Keep"], kept.Select(h => h.Key));
    }

    [Fact]
    public void RedirectHeaders_SameMethodKeepsAll()
    {
        var (kept, clear) = NetRules.RedirectHeaders(Headers, "POST", "POST", "http://a.test/1", "http://a.test/2");
        Assert.False(clear);
        Assert.Equal(Headers, kept);
    }

    [Fact]
    public void RedirectHeaders_CrossOriginDropsAuthorizationAndNullsOrigin()
    {
        var (kept, _) = NetRules.RedirectHeaders(Headers, "POST", "POST", "http://a.test/1", "http://b.test/2");
        Assert.DoesNotContain(kept, h => h.Key == "Authorization");
        Assert.Contains(new KeyValuePair<string, string>("Origin", "null"), kept);
    }

    [Theory]
    [InlineData("http://a.test/x", "http://A.TEST/y", true)]
    [InlineData("http://a.test:8080/", "https://a.test:8080/", false)]
    [InlineData("http://a.test/", "http://a.test:81/", false)]
    [InlineData("http://a.test/", "http://b.test/", false)]
    [InlineData("bad", "http://a.test/", false)]
    public void SameOrigin_SchemeHostPort(string a, string b, bool same) => Assert.Equal(same, NetRules.SameOrigin(a, b));
}

public sealed class NetworkStatusTests
{
    private static NetworkStatus.Interface Good() => new("{GUID}", "Ethernet", false, true, [true]);

    [Fact]
    public void NlaUnknown_IsOnline() => Assert.True(NetworkStatus.IsOnline(null, () => []));

    [Fact]
    public void NlaNoNetwork_IsOffline() => Assert.False(NetworkStatus.IsOnline(false, () => [Good()]));

    [Fact]
    public void UnreadableInterfaces_IsOnline() => Assert.True(NetworkStatus.IsOnline(true, () => null));

    [Fact]
    public void AUsableInterface_IsOnline() => Assert.True(NetworkStatus.IsOnline(true, () => [Good()]));

    [Fact]
    public void NoInterfaces_IsOffline() => Assert.False(NetworkStatus.IsOnline(true, () => []));

    [Fact]
    public void Loopback_DoesNotCount() => Assert.False(NetworkStatus.IsOnline(true, () => [Good() with { IsLoopback = true }]));

    [Fact]
    public void DownAdapter_DoesNotCount() => Assert.False(NetworkStatus.IsOnline(true, () => [Good() with { IsUp = false }]));

    [Fact]
    public void VmwareHostAdapter_DoesNotCount() => Assert.False(NetworkStatus.IsOnline(true, () => [Good() with { AdapterName = "VMnet8" }]));

    [Fact]
    public void Teredo_DoesNotCount() => Assert.False(NetworkStatus.IsOnline(true, () => [Good() with { FriendlyName = "Teredo Tunneling Pseudo-Interface" }]));

    [Fact]
    public void VmnetFriendlyName_DoesNotCount() => Assert.False(NetworkStatus.IsOnline(true, () => [Good() with { FriendlyName = "VMware VMNet1" }]));

    [Fact]
    public void NoPreferredAddress_DoesNotCount() => Assert.False(NetworkStatus.IsOnline(true, () => [Good() with { AddressesPreferred = [false] }]));
}
