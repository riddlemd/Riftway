using System.Net.Sockets;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A browser profile: cookies, cache, storage and network settings shared by the windows that use it.
/// Electron's <c>Session</c>.</summary>
/// <remarks>Sessions are usable once the app is ready. A window picks its session with
/// <see cref="WebPreferences.Session"/> or <see cref="WebPreferences.Partition"/>.</remarks>
public sealed unsafe partial class Session
{
    private const string PersistPrefix = "persist:";
    private static readonly Lock s_gate = new();
    private static readonly Dictionary<string, Session> s_byPath = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Session> s_byPartition = new(StringComparer.Ordinal);
    private static Session? s_default;

    private readonly cef_request_context_t* _context;
    private readonly string? _storagePath;
    // Keeps the handler alive while CEF may still call it.
    private readonly RequestContextHandler? _handler;

    private Session(cef_request_context_t* context, string? storagePath, RequestContextHandler? handler)
    {
        _context = context;
        _storagePath = storagePath;
        _handler = handler;
        Cookies = new Cookies(this);
        Protocol = new SessionProtocol(this);
        _downloadsAtCreation = OrielApp.CurrentOrNull?.GetPath("downloads");
        s_justCreated.Add(this);
    }

    // Sessions made under s_gate, announced once it is released so a handler may ask for another.
    private static readonly List<Session> s_justCreated = [];

    private static T Announce<T>(T result)
    {
        Session[] created;
        lock (s_gate)
        {
            created = [.. s_justCreated];
            s_justCreated.Clear();
        }
        foreach (var session in created) OrielApp.CurrentOrNull?.RaiseSessionCreated(session);
        return result;
    }

    /// <summary>The app's default session, used by windows that name no other. Electron's <c>session.defaultSession</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    public static Session DefaultSession
    {
        get
        {
            RequireReady();
            Session session;
            lock (s_gate)
            {
                session = s_default ??= new Session(CefNative.cef_request_context_get_global_context(), OrielApp.DataDirectory, null);
                if (s_defaultPagelessContext == 0) ShareDefaultForPageless(session._context);
            }
            return Announce(session);
        }
    }

    /// <summary>The session for <paramref name="partition"/>: one named <c>persist:</c><i>name</i> is kept on disk
    /// under the app's data directory; any other name lives in memory until the app quits. The empty string is
    /// <see cref="DefaultSession"/>. The same name always returns the same session. Electron's <c>session.fromPartition()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    public static Session FromPartition(string partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (partition.Length == 0) return DefaultSession;
        RequireReady();
        Session session;
        lock (s_gate)
        {
            if (s_byPartition.TryGetValue(partition, out var existing)) return existing;
            session = partition.StartsWith(PersistPrefix, StringComparison.Ordinal)
                ? FromPathLocked(Path.Combine(OrielApp.DataDirectory!, PartitionDirectory(partition[PersistPrefix.Length..])))
                : Create(null);
            s_byPartition[partition] = session;
        }
        return Announce(session);
    }

    /// <summary>The session stored at <paramref name="path"/>. Electron's <c>session.fromPath()</c>.</summary>
    /// <remarks>Unlike Electron, the path must be a directory directly inside the app's data directory: Chrome-style CEF
    /// creates profiles nowhere else.</remarks>
    /// <exception cref="ArgumentException">The path is not absolute, or not directly inside the app's data directory.</exception>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    public static Session FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The path must be absolute.", nameof(path));
        RequireReady();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OrielApp.DataDirectory!));
        if (string.Equals(full, root, PathComparison)) return DefaultSession;
        if (!string.Equals(Path.GetDirectoryName(full), root, PathComparison))
            throw new ArgumentException($"CEF only creates sessions directly inside the app's data directory ({root}).", nameof(path));
        Session session;
        lock (s_gate) session = FromPathLocked(full);
        return Announce(session);
    }

    /// <summary>The session's cookies. Electron's <c>ses.cookies</c>.</summary>
    public Cookies Cookies { get; }

    /// <summary>Custom scheme handlers for this session's pages. Electron's <c>ses.protocol</c>.</summary>
    /// <remarks>The static <see cref="Oriel.Protocol"/> serves <see cref="DefaultSession"/>, as in Electron.</remarks>
    public SessionProtocol Protocol { get; }

    internal bool IsDefault => _handler is null;

    /// <summary>Where the session keeps its data on disk; null for an in-memory session. Electron's <c>ses.getStoragePath()</c>.</summary>
    public string? GetStoragePath() => _storagePath;

    /// <summary>Whether the session's data outlives the app. Electron's <c>ses.isPersistent()</c>.</summary>
    public bool IsPersistent() => _storagePath is not null;

    /// <summary>Empties the HTTP cache. Electron's <c>ses.clearCache()</c>.</summary>
    public Task ClearCacheAsync() => Complete((context, callback) => context->clear_http_cache(context, callback));

    /// <summary>Forgets HTTP authentication credentials. Electron's <c>ses.clearAuthCache()</c>.</summary>
    public Task ClearAuthCacheAsync() => Complete((context, callback) => context->clear_http_auth_credentials(context, callback));

    /// <summary>Closes idle and active connections; requests in flight fail. Electron's <c>ses.closeAllConnections()</c>.</summary>
    public Task CloseAllConnectionsAsync() => Complete((context, callback) => context->close_all_connections(context, callback));

    /// <summary>Forgets certificate errors the user chose to proceed past.</summary>
    /// <remarks>Electron has no equivalent: its <c>certificate-error</c> event decides each time.</remarks>
    public Task ClearCertificateExceptionsAsync() => Complete((context, callback) => context->clear_certificate_exceptions(context, callback));

    /// <summary>Writes unsaved cookies to disk. Electron's <c>ses.flushStorageData()</c>.</summary>
    public Task FlushStorageDataAsync() => Cookies.FlushStoreAsync();

    private unsafe Task<(cef_errorcode_t Error, IReadOnlyList<string> Addresses)> StartResolve(string host)
    {
        var callback = new ResolveCallback();
        CefThread.RunOnUi(() =>
        {
            cef_string_utf16_t name = default;
            // CEF resolves an origin, not a host name.
            CefString.Set(&name, new UriBuilder(Uri.UriSchemeHttp, host).Uri.ToString());
            _context->resolve_host(_context, &name, callback.Native);
            CefString.Clear(&name);
        });
        return callback.Task;
    }

    /// <summary>Routes the session's requests through a proxy. Electron's <c>ses.setProxy()</c>.</summary>
    /// <remarks>Connections already open keep their route; <see cref="CloseAllConnectionsAsync"/> drops them.</remarks>
    /// <exception cref="ArgumentException">Chromium rejected the configuration.</exception>
    public Task SetProxyAsync(ProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var mode = config.Mode ?? (config.PacScript is not null ? ProxyMode.PacScript : config.ProxyRules is not null ? ProxyMode.FixedServers : ProxyMode.Direct);
        CefThread.InvokeOnUi(() =>
        {
            var dictionary = CefNative.cef_dictionary_value_create();
            SetString(dictionary, "mode", ProxyModeName(mode));
            if (mode == ProxyMode.FixedServers)
            {
                SetString(dictionary, "server", config.ProxyRules ?? "");
                if (config.ProxyBypassRules is { } bypass) SetString(dictionary, "bypass_list", bypass);
            }
            else if (mode == ProxyMode.PacScript)
            {
                SetString(dictionary, "pac_url", config.PacScript ?? "");
            }
            var value = CefNative.cef_value_create();
            value->set_dictionary(value, dictionary);
            SetPreference(_context, "proxy", value);
            return true;
        });
        return Task.CompletedTask;
    }

    /// <summary>Sets the user agent, and optionally the accepted languages (e.g. <c>en-US,fr</c>), of the session's
    /// pages, open ones included; a page's own <see cref="WebContents.SetUserAgent"/> still wins. Electron's
    /// <c>ses.setUserAgent()</c>.</summary>
    public void SetUserAgent(string userAgent, string? acceptLanguages = null)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        WebContents[] pages;
        lock (_pages)
        {
            UserAgentOrNull = userAgent;
            AcceptLanguages = acceptLanguages;
            pages = [.. _pages.Select(p => p.TryGetTarget(out var page) ? page : null).OfType<WebContents>()];
        }
        CefThread.RunOnUi(() =>
        {
            foreach (var page in pages) page.OnSessionUserAgent(userAgent, acceptLanguages);
        });
    }

    /// <summary>The session's user agent: its own, else the app's fallback. Electron's <c>ses.getUserAgent()</c>.</summary>
    public string GetUserAgent() => UserAgentOrNull ?? OrielApp.Current.UserAgentFallback;

    internal string? UserAgentOrNull { get; private set; }

    internal string? AcceptLanguages { get; private set; }

    private readonly List<WeakReference<WebContents>> _pages = [];

    /// <summary>A page of this session, for <see cref="SetUserAgent"/>.</summary>
    internal void Track(WebContents page)
    {
        lock (_pages)
        {
            _pages.RemoveAll(p => !p.TryGetTarget(out _));
            _pages.Add(new WeakReference<WebContents>(page));
        }
    }

    /// <summary>Decides the permissions the session's pages ask for: the handler gets the page, Electron's permission name
    /// (<c>notifications</c>, <c>geolocation</c>, <c>media</c>, <c>clipboard-read</c>, ...) and details, and grants with
    /// true. With none, every permission is granted, as Electron. Electron's <c>ses.setPermissionRequestHandler()</c>.</summary>
    /// <remarks>Chromium remembers a grant for the origin for the rest of the session (drift E29).</remarks>
    public void SetPermissionRequestHandler(Func<WebContents, string, PermissionRequestDetails, Task<bool>>? handler)
        => _permissionRequestHandler = handler;

    private volatile Func<WebContents, string, PermissionRequestDetails, Task<bool>>? _permissionRequestHandler;

    /// <summary>Watches and changes this session's requests. Electron's <c>ses.webRequest</c>.</summary>
    public WebRequest WebRequest { get; } = new();

    /// <summary>The native context with a reference for the caller.</summary>
    internal cef_request_context_t* AddRefNative()
    {
        NativeObject.AddRef(_context);
        return _context;
    }

    internal cef_request_context_t* Native => _context;

    /// <summary>Settings every session gets: Electron behaviour that Chrome-style profiles differ on.</summary>
    internal static void ApplyElectronDefaults(cef_request_context_t* context)
    {
        // Electron has no popup blocker: window.open works without a user gesture, and WebContents.SetWindowOpenHandler decides.
        context->set_content_setting(context, null, null, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_POPUPS,
            cef_content_setting_values_t.CEF_CONTENT_SETTING_VALUE_ALLOW);

        // Chrome-style profiles save each origin's zoom across launches; Electron keeps zoom for the session only.
        var value = CefNative.cef_value_create();
        value->set_dictionary(value, CefNative.cef_dictionary_value_create());
        try { SetPreference(context, "partition.per_host_zoom_levels", value); }
        catch (ArgumentException e) { Console.Error.WriteLine($"[Oriel] Could not reset saved zoom levels: {e.Message}"); }

        ForgetRememberedPermissions(context);
    }

    internal static string ProxyModeName(ProxyMode mode) => mode switch
    {
        ProxyMode.Direct => "direct",
        ProxyMode.AutoDetect => "auto_detect",
        ProxyMode.PacScript => "pac_script",
        ProxyMode.FixedServers => "fixed_servers",
        ProxyMode.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    // Chrome-style CEF only creates profiles directly inside the root cache path, so not Electron's Partitions/<name>.
    internal static string PartitionDirectory(string name)
    {
        if (name.Length == 0) throw new ArgumentException("A persistent partition needs a name after 'persist:'.");
        return "Partition " + Uri.EscapeDataString(name);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static void RequireReady()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true })
            throw new InvalidOperationException("Use sessions after OrielApp.Ready.");
    }

    private static Session FromPathLocked(string path)
    {
        if (s_byPath.TryGetValue(path, out var existing)) return existing;
        var session = Create(path);
        s_byPath[path] = session;
        return session;
    }

    // An empty cache path gives an in-memory profile; a path inside the root cache path gives a profile on disk.
    private static Session Create(string? path)
    {
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Session? session = null;
        var handler = WebRequest.PagelessHandler(() => session?.WebRequest, context =>
        {
            ApplyElectronDefaults((cef_request_context_t*)context);
            initialized.TrySetResult();
        });
        var context = CefThread.InvokeOnUi(() =>
        {
            var settings = new cef_request_context_settings_t { size = (nuint)sizeof(cef_request_context_settings_t) };
            CefString.Set(&settings.cache_path, path);
            NativeObject.AddRef(handler.Native);
            var created = CefNative.cef_request_context_create_context(&settings, handler.Native);
            CefString.Clear(&settings.cache_path);
            return (nint)created;
        });
        if (context == 0) throw new InvalidOperationException($"CEF could not create a session{(path is null ? "" : $" at {path}")}.");
        return session = new Session((cef_request_context_t*)context, path, handler) { _initialized = initialized.Task };
    }

    // The global context can have no handler, but CEF hands a profile's worker requests to the handler of any context
    // sharing it (CefBrowserContext::UpdateWorkerRequestContextHandler), so this context exists only to carry one.
    private static readonly RequestContextHandler s_defaultPageless = WebRequest.PagelessHandler(() => Volatile.Read(ref s_default)?.WebRequest);
    private static nint s_defaultPagelessContext;

    private static void ShareDefaultForPageless(cef_request_context_t* global) => s_defaultPagelessContext = CefThread.InvokeOnUi(() =>
    {
        NativeObject.AddRef(s_defaultPageless.Native);
        NativeObject.AddRef(global);
        return (nint)CefNative.cef_request_context_cef_create_context_shared(global, s_defaultPageless.Native);
    });

    // Preferences read before the profile finishes loading can be wrong. The default session is ready with the app.
    private Task _initialized = Task.CompletedTask;

    /// <summary>Completes once the session's profile has loaded.</summary>
    internal Task Initialized => _initialized;

    /// <summary>Waits, off the UI thread, until the session's profile has loaded.</summary>
    internal void WaitUntilInitialized()
    {
        if (!CefThread.IsOnUiThread) _initialized.Wait(TimeSpan.FromSeconds(10));
    }

    private Task Complete(CompletionStart start)
    {
        var callback = new CompletionCallback();
        CefThread.RunOnUi(() => start(_context, callback.Native));
        return callback.Task;
    }

    private delegate void CompletionStart(cef_request_context_t* context, cef_completion_callback_t* callback);

    private static void SetString(cef_dictionary_value_t* dictionary, string key, string value)
    {
        cef_string_utf16_t k = default, v = default;
        CefString.Set(&k, key);
        CefString.Set(&v, value);
        dictionary->set_string(dictionary, &k, &v);
        CefString.Clear(&k);
        CefString.Clear(&v);
    }

    // Takes the value's reference. UI thread.
    private static void SetPreference(cef_request_context_t* context, string name, cef_value_t* value)
    {
        cef_string_utf16_t key = default, error = default;
        CefString.Set(&key, name);
        var manager = (cef_preference_manager_t*)context;
        var ok = manager->set_preference(manager, &key, value, &error) != 0;
        var message = CefString.Read(&error);
        CefString.Clear(&key);
        CefString.Clear(&error);
        if (!ok) throw new ArgumentException($"Chromium rejected the '{name}' preference: {message}");
    }
}

// Outside the unsafe part: C# does not allow await in an unsafe context.
public sealed partial class Session
{
    internal async Task<bool> DecidePermissionAsync(WebContents page, string permission, PermissionRequestDetails details)
    {
        if (_permissionRequestHandler is not { } handler) return true;
        try { return await handler(page, permission, details).ConfigureAwait(false); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Oriel] The permission request handler failed; denying {permission}: {e}");
            return false;
        }
    }

    /// <summary>Resolves <paramref name="host"/> with the session's network settings. Electron's <c>ses.resolveHost()</c>.</summary>
    /// <exception cref="HostResolutionException">The host could not be resolved.</exception>
    public async Task<ResolvedHost> ResolveHostAsync(string host)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var (error, addresses) = await StartResolve(host).ConfigureAwait(false);
        if (error != cef_errorcode_t.ERR_NONE) throw new HostResolutionException(host, (int)error);
        return new ResolvedHost([.. addresses.Select(a => new ResolvedEndpoint(a,
            System.Net.IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4"))]);
    }
}

/// <summary>How a session chooses proxies. Electron's <c>ProxyConfig.mode</c>.</summary>
public enum ProxyMode
{
    /// <summary>No proxy.</summary>
    Direct,
    /// <summary>Discover a PAC script with WPAD.</summary>
    AutoDetect,
    /// <summary>Use <see cref="ProxyConfig.PacScript"/>.</summary>
    PacScript,
    /// <summary>Use <see cref="ProxyConfig.ProxyRules"/>.</summary>
    FixedServers,
    /// <summary>The operating system's proxy settings.</summary>
    System,
}

/// <summary>A session's proxy settings. Electron's <c>ProxyConfig</c>.</summary>
public sealed record ProxyConfig
{
    /// <summary>The mode; null picks <see cref="ProxyMode.PacScript"/> when <see cref="PacScript"/> is set, else
    /// <see cref="ProxyMode.FixedServers"/> when <see cref="ProxyRules"/> is set, else <see cref="ProxyMode.Direct"/>.</summary>
    public ProxyMode? Mode { get; init; }

    /// <summary>The URL of a PAC script.</summary>
    public string? PacScript { get; init; }

    /// <summary>Chromium's proxy rules, e.g. <c>http=foopy:80;ftp=foopy2</c> or <c>socks5://host:1080</c>.</summary>
    public string? ProxyRules { get; init; }

    /// <summary>Hosts that bypass the proxy, comma-separated, e.g. <c>localhost,*.internal</c>.</summary>
    public string? ProxyBypassRules { get; init; }
}

/// <summary>The addresses a host resolved to. Electron's <c>ResolvedHost</c>.</summary>
public sealed record ResolvedHost(IReadOnlyList<ResolvedEndpoint> Endpoints);

/// <summary>One resolved address. Electron's <c>ResolvedEndpoint</c>.</summary>
/// <param name="Address">The IP address.</param>
/// <param name="Family"><c>ipv4</c> or <c>ipv6</c>.</param>
public sealed record ResolvedEndpoint(string Address, string Family);

/// <summary>A host name that could not be resolved.</summary>
public sealed class HostResolutionException(string host, int errorCode)
    : Exception($"Could not resolve '{host}' (net error {errorCode}).")
{
    /// <summary>The host name.</summary>
    public string Host { get; } = host;

    /// <summary>Chromium's net error code (negative).</summary>
    public int ErrorCode { get; } = errorCode;
}
