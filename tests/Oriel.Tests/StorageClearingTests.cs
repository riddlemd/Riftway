namespace Oriel.Tests;

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

    [Fact]
    public void CacheBytes_CountsEntryFilesOnly()
    {
        var directory = Directory.CreateTempSubdirectory("oriel-cache-").FullName;
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
    public void CacheBytes_NoDirectoryIsZero() => Assert.Equal(0, Session.CacheBytes(Path.Combine(Path.GetTempPath(), "oriel-no-cache-" + Guid.NewGuid())));
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
