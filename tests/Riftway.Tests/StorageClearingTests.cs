namespace Riftway.Tests;

public sealed class StorageClearingTests
{
    [Fact]
    public void StorageTypes_NullIsAll() => Assert.Equal("all", StorageClearing.StorageTypes(null));

    [Fact]
    public void StorageTypes_MapsEveryElectronName() => Assert.Equal(
        "cookies,file_systems,indexeddb,local_storage,shader_cache,service_workers,cache_storage",
        StorageClearing.StorageTypes(["cookies", "filesystem", "indexdb", "localstorage", "shadercache", "serviceworkers", "cachestorage"]));

    [Fact]
    public void StorageTypes_IgnoresCase() => Assert.Equal("local_storage,indexeddb", StorageClearing.StorageTypes(["LocalStorage", "INDEXDB"]));

    [Fact]
    public void StorageTypes_IgnoresUnknownNamesAndRepeats()
        => Assert.Equal("local_storage", StorageClearing.StorageTypes(["localstorage", "websql", "localstorage", "quota"]));

    [Fact]
    public void StorageTypes_NothingKnownIsEmpty() => Assert.Equal("", StorageClearing.StorageTypes(["websql"]));

    private static StorageClearing.ClearDataPlan Plan(ClearDataOptions options) => StorageClearing.Plan(options, o => o.StartsWith("data:"));

    [Fact]
    public void Plan_NoOptionsClearsEverything()
    {
        var plan = Plan(new ClearDataOptions());
        Assert.Equal(("all", (IReadOnlyList<string>?)null, (IReadOnlyList<string>?)null, true, true), (plan.StorageTypes, plan.StorageOrigins, plan.CookieDomains, plan.HttpCache, plan.AuthCache));
        Assert.Equal(StorageClearing.DataTypes, plan.Types);
    }

    [Fact]
    public void Plan_DataTypesMapToStorage()
    {
        var plan = Plan(new ClearDataOptions { DataTypes = ["localStorage", "indexedDB", "serviceWorkers", "fileSystems", "downloads", "webSQL"] });
        Assert.Equal("local_storage,indexeddb,service_workers,file_systems", plan.StorageTypes);
        Assert.False(plan.HttpCache);
        Assert.False(plan.AuthCache);
        Assert.Equal(["downloads", "fileSystems", "indexedDB", "localStorage", "serviceWorkers"], plan.Types);
    }

    [Fact]
    public void Plan_CacheIsCacheStorageShaderCacheAndTheHttpCache()
    {
        var plan = Plan(new ClearDataOptions { DataTypes = ["cache"] });
        Assert.Equal("cache_storage,shader_cache", plan.StorageTypes);
        Assert.True(plan.HttpCache);
        Assert.False(plan.AuthCache);
    }

    [Fact]
    public void Plan_CookiesForEveryOriginAlsoClearAuthentication()
    {
        var plan = Plan(new ClearDataOptions { DataTypes = ["cookies"] });
        Assert.Equal("cookies", plan.StorageTypes);
        Assert.Null(plan.CookieDomains);
        Assert.True(plan.AuthCache);
    }

    [Fact]
    public void Plan_DataTypeNamesAreCaseSensitive() => Assert.Same(StorageClearing.ClearDataPlan.Nothing, Plan(new ClearDataOptions { DataTypes = ["Cookies"] }));

    [Fact]
    public void Plan_EmptyOriginsClearNothing() => Assert.Same(StorageClearing.ClearDataPlan.Nothing, Plan(new ClearDataOptions { Origins = [] }));

    [Fact]
    public void Plan_EmptyExcludeOriginsClearsEverything() => Assert.Equal("all", Plan(new ClearDataOptions { ExcludeOrigins = [] }).StorageTypes);

    [Fact]
    public void Plan_OriginsClearTheirStorageAndTheirSitesCookies()
    {
        var plan = Plan(new ClearDataOptions { DataTypes = ["cookies", "localStorage"], Origins = ["https://a.example.com", "https://b.example.com:8443"] });
        Assert.Equal("local_storage", plan.StorageTypes);
        Assert.Equal(["https://a.example.com", "https://b.example.com:8443"], plan.StorageOrigins);
        Assert.Equal(["example.com"], plan.CookieDomains);
        Assert.False(plan.KeepCookieDomains);
    }

    [Fact]
    public void Plan_OriginsWithOnlyCookiesNeedNoStorageCall()
    {
        var plan = Plan(new ClearDataOptions { DataTypes = ["cookies"], Origins = ["https://a.example.com"] });
        Assert.Equal("", plan.StorageTypes);
        Assert.Null(plan.StorageOrigins);
    }

    [Fact]
    public void Plan_OriginsWithEveryTypeClearAllTheirStorage()
        => Assert.Equal("all", Plan(new ClearDataOptions { Origins = ["https://a.example.com"] }).StorageTypes);

    [Fact]
    public void Plan_ExcludeOriginsKeepTheirCookies()
    {
        var plan = Plan(new ClearDataOptions { DataTypes = ["cookies", "downloads"], ExcludeOrigins = ["https://a.example.com"] });
        Assert.Equal(["example.com"], plan.CookieDomains);
        Assert.True(plan.KeepCookieDomains);
        Assert.True(plan.AuthCache);
        Assert.Equal("", plan.StorageTypes);
    }

    [Theory]
    [InlineData("localStorage")]
    [InlineData("cache")]
    public void Plan_ExcludeOriginsCannotKeepStorage(string type)
        => Assert.Throws<NotSupportedException>(() => Plan(new ClearDataOptions { DataTypes = [type], ExcludeOrigins = ["https://a.example.com"] }));

    [Fact]
    public void Plan_ExcludeOriginsCannotKeepEveryType()
        => Assert.Throws<NotSupportedException>(() => Plan(new ClearDataOptions { ExcludeOrigins = ["https://a.example.com"] }));

    [Fact]
    public void Plan_BackgroundFetchAloneIsUnreachable()
        => Assert.Throws<NotSupportedException>(() => Plan(new ClearDataOptions { DataTypes = ["backgroundFetch"] }));

    [Fact]
    public void Plan_BothOriginListsIsElectronsTypeError()
        => Assert.Equal("Cannot provide both 'origins' and 'excludeOrigins'",
            Assert.Throws<ArgumentException>(() => Plan(new ClearDataOptions { Origins = [], ExcludeOrigins = [] })).Message);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plan_OpaqueOriginIsElectronsTypeError(bool exclude)
    {
        string[] origins = ["https://a.example.com", "data:text/plain,x"];
        var options = exclude ? new ClearDataOptions { ExcludeOrigins = origins } : new ClearDataOptions { Origins = origins };
        Assert.Equal("Invalid origin: 'data:text/plain,x'", Assert.Throws<ArgumentException>(() => Plan(options)).Message);
    }

    [Theory]
    [InlineData("a.example.com", "example.com")]
    [InlineData(".Example.COM", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("[::1]", "[::1]")]
    public void RegistrableDomain_IsTheLastTwoLabelsOrTheHost(string host, string domain)
        => Assert.Equal(domain, StorageClearing.RegistrableDomain(host));

    [Theory]
    [InlineData("", true)]
    [InlineData("data:text/plain,x", true)]
    [InlineData("about:blank", true)]
    [InlineData("custom://host/", true)]
    [InlineData("app://host/", false)]
    [InlineData("https://a.example.com/", false)]
    [InlineData("file:///etc/hosts", false)]
    [InlineData("blob:https://a.example.com/uuid", false)]
    [InlineData("blob:data:x", true)]
    public void IsOpaqueOrigin_FollowsUrlOriginCreate(string spec, bool opaque)
        => Assert.Equal(opaque, StorageClearing.IsOpaqueOrigin(spec, scheme => scheme == "app"));

    [Fact]
    public void DownloadHeaders_AddOnlyMissingNames()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "*/*" };
        var added = DownloadHeaders.Merge(existing, new Dictionary<string, string> { ["accept"] = "text/plain", ["X-Token"] = "t" }).ToList();
        Assert.Equal([new KeyValuePair<string, string>("X-Token", "t")], added);
    }

    [Fact]
    public void DownloadHeaders_NoneAsked() => Assert.Empty(DownloadHeaders.Merge(new Dictionary<string, string> { ["Accept"] = "*/*" }, null));

    [Theory]
    [InlineData("https://h.example/f.zip#x", "https://h.example/f.zip")]
    [InlineData("https://h.example", "https://h.example/")]
    [InlineData("HTTPS://H.Example:443/a?b#c", "https://h.example/a?b")]
    [InlineData("http://h.example:8080/a", "http://h.example:8080/a")]
    [InlineData("https://b\u00fccher.example/x", "https://xn--bcher-kva.example/x")]
    [InlineData("http://[::1]:81/a", "http://[::1]:81/a")]
    [InlineData("http://h.example/a b", "http://h.example/a%20b")]
    [InlineData("file:///tmp/a.bin#x", "file:///tmp/a.bin")]
    [InlineData("data:text/plain,hi#x", "data:text/plain,hi")]
    [InlineData("not a url", "not a url")]
    public void DownloadHeaderMatcher_CanonicalIsDevToolsRequestUrl(string url, string canonical)
        => Assert.Equal(canonical, DownloadHeaderMatcher.Canonical(url));

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static Dictionary<string, string> Token(string value) => new() { ["X-Token"] = value };

    [Fact]
    public void DownloadHeaderMatcher_MatchesTheCanonicalUrl()
    {
        var matcher = new DownloadHeaderMatcher(TimeSpan.FromMinutes(1));
        matcher.Expect("https://b\u00fccher.example#top", Token("a"), T0);
        Assert.Equal("a", matcher.Match("https://xn--bcher-kva.example/", "f1", "n1", null, T0)?["X-Token"]);
    }

    [Fact]
    public void DownloadHeaderMatcher_AnUnmatchedDownloadBlocksNoOther()
    {
        var matcher = new DownloadHeaderMatcher(TimeSpan.FromMinutes(1));
        matcher.Expect("x-never://nothing", Token("lost"), T0);
        matcher.Expect("https://h.example/a", Token("a"), T0);
        matcher.Expect("https://h.example/b", Token("b"), T0);
        Assert.Equal("b", matcher.Match("https://h.example/b", "f1", "n1", null, T0)?["X-Token"]);
        Assert.Equal("a", matcher.Match("https://h.example/a", "f2", "n2", null, T0)?["X-Token"]);
        Assert.Equal(1, matcher.Waiting);
    }

    [Fact]
    public void DownloadHeaderMatcher_SameUrlKeepsOrder()
    {
        var matcher = new DownloadHeaderMatcher(TimeSpan.FromMinutes(1));
        matcher.Expect("https://h.example/a", null, T0);
        matcher.Expect("https://h.example/a", Token("second"), T0);
        Assert.Null(matcher.Match("https://h.example/a", "f1", "n1", null, T0));
        Assert.Equal("second", matcher.Match("https://h.example/a", "f2", "n2", null, T0)?["X-Token"]);
        Assert.Null(matcher.Match("https://other.example/hop", "f3", "n1", "f1", T0));
    }

    [Fact]
    public void DownloadHeaderMatcher_FollowsRedirectsByRequestId()
    {
        var matcher = new DownloadHeaderMatcher(TimeSpan.FromMinutes(1));
        matcher.Expect("https://h.example/r", Token("a"), T0);
        matcher.Match("https://h.example/r", "f1", null, null, T0);
        Assert.Equal("a", matcher.Match("https://h.example/hop", "f2", null, "f1", T0)?["X-Token"]);
        Assert.Equal("a", matcher.Match("https://h.example/final", "f3", null, "f2", T0)?["X-Token"]);
        Assert.Null(matcher.Match("https://h.example/other", "f4", null, "f1", T0));
    }

    [Fact]
    public void DownloadHeaderMatcher_FollowsRedirectsByNetworkId()
    {
        var matcher = new DownloadHeaderMatcher(TimeSpan.FromMinutes(1));
        matcher.Expect("https://h.example/r", Token("a"), T0);
        matcher.Match("https://h.example/r", "f1", "n1", null, T0);
        Assert.Equal("a", matcher.Match("https://h.example/final", "f2", "n1", null, T0)?["X-Token"]);
        Assert.Null(matcher.Match("https://h.example/final", "f3", "n2", null, T0));
    }

    [Fact]
    public void DownloadHeaderMatcher_CrossOriginHopNullsOrigin()
    {
        var matcher = new DownloadHeaderMatcher(TimeSpan.FromMinutes(1));
        matcher.Expect("https://a.example/r", new Dictionary<string, string> { ["Origin"] = "https://a.example", ["X-Token"] = "t" }, T0);
        matcher.Match("https://a.example/r", "f1", "n1", null, T0);
        Assert.Equal("https://a.example", matcher.Match("https://a.example/same", "f2", "n1", null, T0)?["Origin"]);
        var cross = matcher.Match("https://b.example/final", "f3", "n1", null, T0);
        Assert.Equal(("null", "t"), (cross?["Origin"], cross?["X-Token"]));
    }

    [Fact]
    public void DownloadHeaderMatcher_ExpiresWhatNeverComes()
    {
        var matcher = new DownloadHeaderMatcher(TimeSpan.FromMinutes(1));
        matcher.Expect("https://h.example/a", Token("a"), T0);
        Assert.Null(matcher.Match("https://h.example/a", "f1", "n1", null, T0.AddMinutes(2)));
        Assert.Equal(0, matcher.Waiting);
        matcher.Expect("https://h.example/b", Token("b"), T0);
        Assert.NotNull(matcher.Match("https://h.example/b", "f2", "n2", null, T0.AddSeconds(60)));
        // Each hop restarts the minute, so a slow redirect chain keeps its headers.
        Assert.NotNull(matcher.Match("https://h.example/hop1", "f3", "n2", null, T0.AddSeconds(60 + 59)));
        Assert.NotNull(matcher.Match("https://h.example/hop2", "f4", "n2", null, T0.AddSeconds(60 + 59 + 59)));
        Assert.Null(matcher.Match("https://h.example/c", "f5", "n2", null, T0.AddSeconds(60 + 59 + 59 + 61)));
        Assert.Equal(0, matcher.Following);
    }

    [Fact]
    public void CacheBytes_CountsEntryFilesOnly()
    {
        var directory = Directory.CreateTempSubdirectory("riftway-cache-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "0123456789abcdef_0"), new byte[300]);
            File.WriteAllBytes(Path.Combine(directory, "0123456789abcdef_s"), new byte[20]);
            File.WriteAllBytes(Path.Combine(directory, "index"), new byte[24]);
            File.WriteAllBytes(Path.Combine(directory, "data_0"), new byte[8192]);
            Directory.CreateDirectory(Path.Combine(directory, "index-dir"));
            File.WriteAllBytes(Path.Combine(directory, "index-dir", "the-real-index"), new byte[100]);
            Assert.Equal(320, Session.CacheBytes(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CacheBytes_NoDirectoryIsZero() => Assert.Equal(0, Session.CacheBytes(Path.Combine(Path.GetTempPath(), "riftway-no-cache-" + Guid.NewGuid())));
}

public sealed class UserCacheDirectoryTests
{
    private static readonly (string, string) Roots = ("/home/u/.config", "/home/u/.cache");

    [Fact]
    public void UnderTheDataRoot_MovesToTheCacheRoot()
        => Assert.Equal(Path.Combine("/home/u/.cache", "App", "Partition x"), Session.UserCacheDirectory("/home/u/.config/App/Partition x", Roots));

    [Theory]
    [InlineData("/srv/app/data")]
    [InlineData("/home/u/.config")]
    [InlineData("/home/u/.configured/App")]
    public void ElsewhereStaysInTheProfile(string profile) => Assert.Equal(profile, Session.UserCacheDirectory(profile, Roots));

    [Fact]
    public void NoRoots_IsTheProfile() => Assert.Equal("/p", Session.UserCacheDirectory("/p", null));
}
