namespace Riftway;

/// <summary>Electron's storage and data-type names, mapped onto what DevTools and CEF can clear.</summary>
internal static class StorageClearing
{
    // electron_api_session.cc GetStorageMask, as DevTools Storage.StorageType names; matched case-insensitively there.
    private static readonly Dictionary<string, string> s_storages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cookies"] = "cookies",
        ["filesystem"] = "file_systems",
        ["indexdb"] = "indexeddb",
        ["localstorage"] = "local_storage",
        ["shadercache"] = "shader_cache",
        ["serviceworkers"] = "service_workers",
        ["cachestorage"] = "cache_storage",
    };

    /// <summary>The DevTools storage types for <c>clearStorageData</c>'s <c>storages</c>: <c>all</c> when null, "" when
    /// nothing is to be cleared. Unknown names are ignored, as Electron's.</summary>
    internal static string StorageTypes(IReadOnlyList<string>? storages)
    {
        if (storages is null) return "all";
        return string.Join(",", storages.Select(s => s_storages.GetValueOrDefault(s)).OfType<string>().Distinct());
    }

    /// <summary>The <c>clearData</c> data types, exactly as Electron's kDataTypeLookup spells them.</summary>
    internal static readonly string[] DataTypes =
        ["backgroundFetch", "cache", "cookies", "downloads", "fileSystems", "indexedDB", "localStorage", "serviceWorkers"];

    // Per-origin storage behind each data type that has some; cookies and the HTTP cache are handled apart.
    private static readonly Dictionary<string, string[]> s_dataTypeStorage = new(StringComparer.Ordinal)
    {
        ["cache"] = ["cache_storage", "shader_cache"],
        ["cookies"] = ["cookies"],
        ["fileSystems"] = ["file_systems"],
        ["indexedDB"] = ["indexeddb"],
        ["localStorage"] = ["local_storage"],
        ["serviceWorkers"] = ["service_workers"],
    };

    /// <summary>What a <c>clearData</c> call clears.</summary>
    /// <param name="StorageTypes">DevTools storage types for <c>Storage.clearDataForOrigin</c>; "" for none.</param>
    /// <param name="StorageOrigins">The origins to clear them for; null for every origin.</param>
    /// <param name="CookieDomains">Registrable domains whose cookies go, or stay with <paramref name="KeepCookieDomains"/>;
    /// null when cookies are cleared with the storage types or not at all.</param>
    /// <param name="KeepCookieDomains">Clear every cookie outside <paramref name="CookieDomains"/> instead.</param>
    /// <param name="HttpCache">Clear the HTTP cache and the rest of Chromium's cache data type.</param>
    /// <param name="AuthCache">Clear HTTP authentication credentials.</param>
    /// <param name="Types">The Electron data types asked for, to name the ones that fail.</param>
    internal sealed record ClearDataPlan(string StorageTypes, IReadOnlyList<string>? StorageOrigins, IReadOnlyList<string>? CookieDomains,
        bool KeepCookieDomains, bool HttpCache, bool AuthCache, IReadOnlyList<string> Types)
    {
        internal static readonly ClearDataPlan Nothing = new("", null, null, false, false, false, []);
    }

    /// <summary>Plans <c>clearData</c>. <paramref name="isOpaque"/> says whether a URL's origin is opaque.</summary>
    /// <exception cref="ArgumentException">Both origin lists, or an opaque origin: Electron's TypeErrors.</exception>
    /// <exception cref="NotSupportedException">A part DevTools and CEF cannot reach.</exception>
    internal static ClearDataPlan Plan(ClearDataOptions options, Func<string, bool> isOpaque)
    {
        if (options.Origins is not null && options.ExcludeOrigins is not null)
            throw new ArgumentException("Cannot provide both 'origins' and 'excludeOrigins'");
        var origins = options.Origins ?? options.ExcludeOrigins ?? [];
        foreach (var origin in origins)
            if (isOpaque(origin)) throw new ArgumentException($"Invalid origin: '{origin}'");

        var all = options.DataTypes is null;
        var types = all ? [.. DataTypes] : options.DataTypes!.Where(t => Array.IndexOf(DataTypes, t) >= 0).ToHashSet(StringComparer.Ordinal);
        if (types.Count == 0) return ClearDataPlan.Nothing;
        // An explicit empty origins list matches nothing.
        if (options.Origins is { Count: 0 }) return ClearDataPlan.Nothing;
        if (!all && types.Contains("backgroundFetch"))
            throw new NotSupportedException("clearData cannot clear backgroundFetch on its own: DevTools has no storage type for it.");

        var cookies = types.Contains("cookies");
        var httpCache = types.Contains("cache");
        var storage = types.SelectMany(t => s_dataTypeStorage.GetValueOrDefault(t) ?? []).Distinct().ToList();
        string[] asked = [.. DataTypes.Where(types.Contains)];

        if (origins.Count == 0)
            return new ClearDataPlan(all ? "all" : string.Join(",", storage), null, null, false, httpCache, cookies, asked);

        var domains = origins.Select(o => RegistrableDomain(new Uri(o).Host)).Distinct().ToList();
        if (options.Origins is not null)
        {
            var perOrigin = all ? "all" : string.Join(",", storage.Where(s => s != "cookies"));
            return new ClearDataPlan(perOrigin, perOrigin.Length == 0 ? null : origins, cookies ? domains : null, false, httpCache, cookies, asked);
        }

        var kept = storage.Where(s => s != "cookies").ToList();
        if (kept.Count > 0)
            throw new NotSupportedException($"clearData cannot keep some origins' {string.Join(", ", kept)}: DevTools clears storage for one origin or for all.");
        return new ClearDataPlan("", null, cookies ? domains : null, true, httpCache, cookies, asked);
    }

    /// <summary>The domain Chromium files a host's cookies under for <c>clearData</c> (GetDomainAndRegistry, else the
    /// host), with the public suffix list cut to "the last label", as <see cref="CookieRules"/>.</summary>
    internal static string RegistrableDomain(string host)
    {
        var bare = host.TrimStart('.').ToLowerInvariant();
        if (System.Net.IPAddress.TryParse(bare.Trim('[', ']'), out _)) return bare;
        var labels = bare.Split('.');
        return labels.Length < 2 || labels.Any(l => l.Length == 0) ? bare : $"{labels[^2]}.{labels[^1]}";
    }

    /// <summary>Whether <c>url::Origin::Create(GURL(spec))</c> is opaque, given GURL's canonical spec ("" when invalid)
    /// and whether a scheme is a registered standard one.</summary>
    internal static bool IsOpaqueOrigin(string spec, Func<string, bool> isStandardScheme)
    {
        if (spec.Length == 0) return true;
        var colon = spec.IndexOf(':');
        var scheme = spec[..colon];
        // Their origin is the inner URL's.
        if (scheme is "blob" or "filesystem") return IsOpaqueOrigin(spec[(colon + 1)..], isStandardScheme);
        if (scheme == "file") return false;
        if (scheme is "http" or "https" or "ws" or "wss" or "ftp" || isStandardScheme(scheme))
            return !Uri.TryCreate(spec, UriKind.Absolute, out var uri) || uri.Host.Length == 0;
        return true;
    }
}
