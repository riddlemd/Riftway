using System.Text.Json;
using Riftway.Cef;

namespace Riftway;

public sealed partial class Session
{
    private readonly Lock _helperGate = new();
    private Task<SessionHelperPage>? _helper;

    // The hidden page is made on first use and lives until the app quits; one that failed or closed is made again.
    private Task<SessionHelperPage> Helper()
    {
        lock (_helperGate)
        {
            if (_helper is { IsFaulted: true } or { IsCanceled: true } || (_helper is { IsCompletedSuccessfully: true } done && !done.Result.IsAlive))
                _helper = null;
            return _helper ??= SessionHelperPage.CreateAsync(this);
        }
    }

    private async Task<JsonElement> DevToolsAsync(string method, Action<Utf8JsonWriter>? writeParams = null)
    {
        var helper = await Helper().ConfigureAwait(false);
        return await helper.SendAsync(method, writeParams).ConfigureAwait(false);
    }

    /// <summary>Clears web storage: for <see cref="ClearStorageDataOptions.Origin"/> only when it is set, else for every
    /// origin. Electron's <c>ses.clearStorageData()</c>.</summary>
    /// <remarks>As Electron, an origin that is not a valid URL clears every origin, and unknown storage names are ignored.
    /// Runs through DevTools on a hidden page of the session, made on first use.</remarks>
    public async Task ClearStorageDataAsync(ClearStorageDataOptions? options = null)
    {
        var types = StorageClearing.StorageTypes(options?.Storages);
        // DevTools refuses an empty mask; Electron's ClearData with one clears nothing.
        if (types.Length == 0) return;
        await ClearForOriginAsync(options?.Origin ?? "", types).ConfigureAwait(false);
    }

    // An origin that is not a valid URL becomes an opaque storage key, which Chromium's ClearData reads as "every origin".
    private Task ClearForOriginAsync(string origin, string types) => DevToolsAsync("Storage.clearDataForOrigin", w =>
    {
        w.WriteString("origin", origin);
        w.WriteString("storageTypes", types);
    });

    /// <summary>Clears data of the given types, for some origins, all but some, or all. Electron's <c>ses.clearData()</c>.</summary>
    /// <remarks>Cookies go by registrable domain, as Electron's, with the public suffix list cut to the last label.
    /// The HTTP cache and HTTP authentication credentials are cleared for every origin even when origins are named
    /// (drift E35).</remarks>
    /// <exception cref="ArgumentException">Both <see cref="ClearDataOptions.Origins"/> and
    /// <see cref="ClearDataOptions.ExcludeOrigins"/>, or an origin with no host.</exception>
    /// <exception cref="NotSupportedException"><see cref="ClearDataOptions.ExcludeOrigins"/> with storage other than
    /// cookies, or <c>backgroundFetch</c> named on its own (drift E35).</exception>
    /// <exception cref="ClearDataException">Some data could not be cleared.</exception>
    public Task ClearDataAsync(ClearDataOptions? options = null)
    {
        var plan = StorageClearing.Plan(options ?? new ClearDataOptions(),
            origin => StorageClearing.IsOpaqueOrigin(Gurl.Spec(origin), Riftway.Protocol.IsStandardScheme));
        return RunAsync(plan);
    }

    private async Task RunAsync(StorageClearing.ClearDataPlan plan)
    {
        var failed = new List<string>();
        async Task Step(Func<Task> step, params string[] dataTypes)
        {
            try { await step().ConfigureAwait(false); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine($"[Riftway] clearData: {e.Message}");
                failed.AddRange(dataTypes);
            }
        }

        string[] storageTypes = [.. plan.Types.Where(t => t is not "downloads")];
        if (plan.StorageTypes.Length > 0)
            foreach (var origin in plan.StorageOrigins ?? [""])
                await Step(() => ClearForOriginAsync(origin, plan.StorageTypes), storageTypes).ConfigureAwait(false);
        if (plan.CookieDomains is { } domains)
            await Step(() => RemoveCookiesAsync(domains, plan.KeepCookieDomains), "cookies").ConfigureAwait(false);
        // BrowsingDataRemover's DATA_TYPE_CACHE: HTTP, code and preflight caches and networking history, every origin.
        if (plan.HttpCache) await Step(() => DevToolsAsync("Network.clearBrowserCache"), "cache").ConfigureAwait(false);
        if (plan.AuthCache) await Step(ClearAuthCacheAsync, "cookies").ConfigureAwait(false);
        if (failed.Count > 0) throw new ClearDataException([.. plan.Types.Where(failed.Contains)]);
    }

    private async Task RemoveCookiesAsync(IReadOnlyList<string> domains, bool keep)
    {
        foreach (var cookie in await Cookies.GetAsync().ConfigureAwait(false))
        {
            var bare = cookie.Domain.TrimStart('.');
            if (domains.Contains(StorageClearing.RegistrableDomain(bare)) == keep) continue;
            var url = $"{(cookie.Secure ? "https" : "http")}://{bare}{cookie.Path}";
            await Cookies.RemoveAsync(url, cookie.Name).ConfigureAwait(false);
        }
    }

    /// <summary>The size of the session's HTTP cache in bytes. Electron's <c>ses.getCacheSize()</c>.</summary>
    /// <remarks>The files of the cache on disk, so it can differ from Chromium's own count by its index and rounding
    /// (drift E34).</remarks>
    /// <exception cref="NotSupportedException">The session is in memory: its cache has no files (drift E34).</exception>
    public Task<long> GetCacheSizeAsync()
    {
        if (_storagePath is null) throw new NotSupportedException("GetCacheSizeAsync needs a session on disk: CEF cannot measure an in-memory cache.");
        return Task.Run(() => CacheBytes(Path.Combine(UserCacheDirectory(_storagePath, CacheRoots()), "Cache", "Cache_Data")));
    }

    // chrome_paths' DIR_APP_DATA (macOS) or XDG config home (Linux), and DIR_CACHE; none on Windows.
    private static (string Data, string Cache)? CacheRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return (Path.Combine(home, "Library", "Application Support"), Path.Combine(home, "Library", "Caches"));
        if (!OperatingSystem.IsLinux()) return null;
        static string Xdg(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : fallback;
        return (Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config")), Xdg("XDG_CACHE_HOME", Path.Combine(home, ".cache")));
    }

    /// <summary>chrome_paths' GetUserCacheDirectory: a profile under the data root keeps its cache at the same place under
    /// the cache root; any other profile keeps it in itself.</summary>
    internal static string UserCacheDirectory(string profile, (string Data, string Cache)? roots)
    {
        if (roots is not { } r) return profile;
        var relative = Path.GetRelativePath(r.Data, profile);
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? profile : Path.Combine(r.Cache, relative);
    }

    // The simple cache's entry files; its index and the blockfile cache's preallocated files are bookkeeping.
    internal static long CacheBytes(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            if (file.Name is "index" || file.Name.StartsWith("data_", StringComparison.Ordinal)) continue;
            try { total += file.Length; }
            catch (FileNotFoundException) { }
        }
        return total;
    }

    /// <summary>Downloads <paramref name="url"/> in this session, with no page: <see cref="WillDownload"/> is raised with a
    /// null <see cref="WillDownloadEventArgs.WebContents"/>. Electron's <c>ses.downloadURL()</c>.</summary>
    /// <remarks>Starts from a hidden page of the session, made on first use; headers are added through DevTools request
    /// interception on that page.</remarks>
    public void DownloadUrl(string url, DownloadUrlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        var headers = options?.Headers is { Count: > 0 } h ? new Dictionary<string, string>(h) : null;
        _ = StartDownloadAsync(url, headers);
    }

    // A request DevTools never pauses (a scheme it does not intercept, a failed start) must not hold the others' headers.
    private readonly DownloadHeaderMatcher _downloadHeaders = new(TimeSpan.FromMinutes(1));
    // Every download with headers waits for the one Fetch.enable, not only the first to ask: until it completes
    // DevTools lets requests through untouched.
    private Task? _interception;

    private async Task StartDownloadAsync(string url, Dictionary<string, string>? headers)
    {
        try
        {
            var helper = await Helper().ConfigureAwait(false);
            await helper.Ready.ConfigureAwait(false);
            if (headers is not null) await InterceptAsync(helper).ConfigureAwait(false);
            CefThread.RunOnUi(() =>
            {
                // Header-less downloads are expected too, so one to the same URL cannot take another's headers.
                if (_interception is not null) _downloadHeaders.Expect(url, headers, DateTime.UtcNow);
                helper.StartDownload(url);
            });
        }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Session.DownloadUrl({url}) failed: {e.Message}"); }
    }

    // Fetch interception stays on once used: switching it off could drop a download's paused request.
    private Task InterceptAsync(SessionHelperPage helper) => CefThread.InvokeOnUi(() =>
    {
        if (_interception is not null) return _interception;
        helper.EventReceived += (method, args) =>
        {
            if (method == "Fetch.requestPaused") OnDownloadRequestPaused(helper, args);
        };
        return _interception = helper.SendAsync("Fetch.enable");
    });

    // UI thread. DownloadUrlParameters adds its headers only where the request has none of that name.
    private void OnDownloadRequestPaused(SessionHelperPage helper, JsonElement args)
    {
        var id = args.GetProperty("requestId").GetString()!;
        var request = args.GetProperty("request");
        var url = request.GetProperty("url").GetString() ?? "";
        var networkId = args.TryGetProperty("networkId", out var network) ? network.GetString() : null;
        var redirectedFrom = args.TryGetProperty("redirectedRequestId", out var redirected) ? redirected.GetString() : null;
        var extra = _downloadHeaders.Match(url, id, networkId, redirectedFrom, DateTime.UtcNow);
        var merged = request.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in DownloadHeaders.Merge(merged, extra)) merged[name] = value;
        _ = helper.SendAsync("Fetch.continueRequest", w =>
        {
            w.WriteString("requestId", id);
            if (extra is null) return;
            w.WriteStartArray("headers");
            foreach (var (name, value) in merged)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("value", value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }
}

/// <summary>The headers a download's request gains from <see cref="DownloadUrlOptions.Headers"/>.</summary>
internal static class DownloadHeaders
{
    /// <summary>The headers of <paramref name="extra"/> that <paramref name="existing"/> lacks, as
    /// <c>HttpRequestHeaders::SetHeaderIfMissing</c>.</summary>
    internal static IEnumerable<KeyValuePair<string, string>> Merge(IReadOnlyDictionary<string, string> existing, IReadOnlyDictionary<string, string>? extra)
    {
        if (extra is null) yield break;
        var seen = new HashSet<string>(existing.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var header in extra)
            if (seen.Add(header.Key)) yield return header;
    }
}

/// <summary>Pairs <see cref="Session.DownloadUrl"/> calls with the requests DevTools pauses, and keeps each one's headers on
/// its redirects, as the download's own request (DownloadUrlParameters) keeps its extra headers in Chromium. UI thread.</summary>
internal sealed class DownloadHeaderMatcher(TimeSpan lifetime)
{
    private sealed class Entry(string url, Dictionary<string, string>? headers, DateTime seen)
    {
        public string Url { get; } = url;
        public Dictionary<string, string>? Headers { get; } = headers;
        public DateTime Seen { get; set; } = seen;
        public string? FetchId { get; set; }
        public string? NetworkId { get; set; }
    }

    private readonly List<Entry> _waiting = [];
    private readonly List<Entry> _following = [];

    /// <summary>Downloads still waiting for their first request.</summary>
    internal int Waiting => _waiting.Count;

    /// <summary>Downloads whose redirects are still followed.</summary>
    internal int Following => _following.Count;

    /// <summary>Expects a request for <paramref name="url"/>, to get <paramref name="headers"/>.</summary>
    internal void Expect(string url, Dictionary<string, string>? headers, DateTime now)
    {
        Prune(now);
        _waiting.Add(new Entry(Canonical(url), headers is null ? null : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase), now));
    }

    /// <summary>The headers the paused request <paramref name="fetchId"/> gains, or null; <paramref name="redirectedFrom"/>
    /// is Fetch's <c>redirectedRequestId</c>, the paused request this hop follows.</summary>
    internal IReadOnlyDictionary<string, string>? Match(string url, string fetchId, string? networkId, string? redirectedFrom, DateTime now)
    {
        Prune(now);
        var hop = _following.FirstOrDefault(e => (redirectedFrom is not null && e.FetchId == redirectedFrom) || (networkId is not null && e.NetworkId == networkId));
        if (hop is not null)
        {
            // RedirectUtil::UpdateHttpRequest: a cross-origin hop's Origin becomes the opaque "null", and stays so; the
            // first hop that leaves the download's origin is the first to differ from it.
            if (!SameOrigin(hop.Url, url) && hop.Headers!.ContainsKey("Origin")) hop.Headers["Origin"] = "null";
            hop.FetchId = fetchId;
            hop.Seen = now;
            return hop.Headers;
        }

        var canonical = Canonical(url);
        var index = _waiting.FindIndex(e => e.Url == canonical);
        if (index < 0) return null;
        var entry = _waiting[index];
        _waiting.RemoveAt(index);
        if (entry.Headers is null) return null;
        entry.FetchId = fetchId;
        entry.NetworkId = networkId;
        entry.Seen = now;
        _following.Add(entry);
        return entry.Headers;
    }

    private void Prune(DateTime now)
    {
        _waiting.RemoveAll(e => now - e.Seen > lifetime);
        _following.RemoveAll(e => now - e.Seen > lifetime);
    }

    /// <summary><paramref name="url"/> as DevTools reports a request's: GURL's canonical form, without the fragment.</summary>
    internal static string Canonical(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.HostNameType == UriHostNameType.Unknown) return uri.GetLeftPart(UriPartial.Query);
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        var user = uri.UserInfo.Length > 0 ? uri.UserInfo + "@" : "";
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{uri.Scheme}://{user}{host}{port}{uri.PathAndQuery}";
    }

    private static bool SameOrigin(string a, string b)
        => Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
            && x.Scheme == y.Scheme && string.Equals(x.IdnHost, y.IdnHost, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port;
}

/// <summary>What <see cref="Session.ClearStorageDataAsync"/> clears. Electron's <c>clearStorageData</c> options.</summary>
public sealed record ClearStorageDataOptions
{
    /// <summary>The origin, as <c>scheme://host:port</c>; null for every origin.</summary>
    public string? Origin { get; init; }

    /// <summary><c>cookies</c>, <c>filesystem</c>, <c>indexdb</c>, <c>localstorage</c>, <c>shadercache</c>,
    /// <c>serviceworkers</c>, <c>cachestorage</c>; null for all of them.</summary>
    public IReadOnlyList<string>? Storages { get; init; }
}

/// <summary>What <see cref="Session.ClearDataAsync"/> clears. Electron's <c>clearData</c> options.</summary>
public sealed record ClearDataOptions
{
    /// <summary><c>backgroundFetch</c>, <c>cache</c>, <c>cookies</c>, <c>downloads</c>, <c>fileSystems</c>,
    /// <c>indexedDB</c>, <c>localStorage</c>, <c>serviceWorkers</c>; null for every type.</summary>
    public IReadOnlyList<string>? DataTypes { get; init; }

    /// <summary>Clear only these origins' data.</summary>
    public IReadOnlyList<string>? Origins { get; init; }

    /// <summary>Clear every origin's data but these.</summary>
    public IReadOnlyList<string>? ExcludeOrigins { get; init; }

    /// <summary>Has no effect, as in Electron, whose sessions have no embedder to honour it.</summary>
    public bool AvoidClosingConnections { get; init; }

    /// <summary><c>third-parties-included</c> or <c>origin-in-all-contexts</c>. Riftway clears an origin's first-party
    /// storage either way (drift E35).</summary>
    public string? OriginMatchingMode { get; init; }
}

/// <summary>Options for <see cref="Session.DownloadUrl"/>. Electron's <c>downloadURL</c> options.</summary>
public sealed record DownloadUrlOptions
{
    /// <summary>Request headers, added where the request has none of that name.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary><see cref="Session.ClearDataAsync"/> could not clear some types. Electron's "Failed to clear data" error.</summary>
public sealed class ClearDataException(IReadOnlyList<string> failedDataTypes) : Exception("Failed to clear data")
{
    /// <summary>The data types that failed, Electron's <c>failedDataTypes</c>.</summary>
    public IReadOnlyList<string> FailedDataTypes { get; } = failedDataTypes;
}
