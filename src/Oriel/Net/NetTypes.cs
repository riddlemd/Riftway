namespace Oriel;

/// <summary>What a request does on a redirect. Electron's <c>redirect</c> option.</summary>
public enum RedirectMode
{
    /// <summary>Follow it; <see cref="ClientRequest.Redirect"/> is still raised. The default.</summary>
    Follow,
    /// <summary>Fail the request.</summary>
    Error,
    /// <summary>Raise <see cref="ClientRequest.Redirect"/> and follow only if its handler calls
    /// <see cref="ClientRequest.FollowRedirect"/>.</summary>
    Manual,
}

/// <summary>Whether a request sends credentials. Electron's <c>credentials</c> option (fetch's credentials mode).</summary>
public enum RequestCredentials
{
    /// <summary>Send the session's cookies and authentication data.</summary>
    Include,
    /// <summary>Send none, and raise no <see cref="ClientRequest.Login"/> on a 401.</summary>
    Omit,
    /// <summary>As <see cref="Include"/> for the request's own origin; needs <see cref="ClientRequestOptions.Origin"/>.</summary>
    SameOrigin,
}

/// <summary>How a request uses the HTTP cache. Electron's <c>cache</c> option (fetch's cache mode).</summary>
public enum RequestCache
{
    /// <summary>Normal caching.</summary>
    Default,
    /// <summary>Neither read nor write the cache.</summary>
    NoStore,
    /// <summary>Ignore cached entries, update the cache.</summary>
    Reload,
    /// <summary>Revalidate cached entries.</summary>
    NoCache,
    /// <summary>Use cached entries even when stale.</summary>
    ForceCache,
    /// <summary>Fail unless the response is cached.</summary>
    OnlyIfCached,
}

/// <summary>A request's network priority. Electron's <c>priority</c> option.</summary>
public enum RequestPriority
{
    /// <summary><c>throttled</c>.</summary>
    Throttled,
    /// <summary><c>idle</c>, the default.</summary>
    Idle,
    /// <summary><c>lowest</c>.</summary>
    Lowest,
    /// <summary><c>low</c>.</summary>
    Low,
    /// <summary><c>medium</c>.</summary>
    Medium,
    /// <summary><c>highest</c>.</summary>
    Highest,
}

/// <summary>What <see cref="Net.Request(ClientRequestOptions)"/> sends. Electron's <c>ClientRequestConstructorOptions</c>.</summary>
public sealed record ClientRequestOptions
{
    /// <summary>The HTTP method, upper-cased; GET when null.</summary>
    public string? Method { get; init; }

    /// <summary>The absolute http: or https: URL; when null it is built from <see cref="Protocol"/>,
    /// <see cref="Host"/> or <see cref="Hostname"/> and <see cref="Port"/>, and <see cref="Path"/>.</summary>
    public string? Url { get; init; }

    /// <summary>Headers to send, validated as <see cref="ClientRequest.SetHeader"/> does.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The session to send from; wins over <see cref="Partition"/>.</summary>
    public Session? Session { get; init; }

    /// <summary>The partition whose session to send from; the empty string or null is the default session.</summary>
    public string? Partition { get; init; }

    /// <summary>Skip the session's custom protocol handlers for this request.</summary>
    public bool BypassCustomProtocolHandlers { get; init; }

    /// <summary>The credentials mode; when null, authentication data is sent and cookies only with
    /// <see cref="UseSessionCookies"/>.</summary>
    public RequestCredentials? Credentials { get; init; }

    /// <summary>Send and store the session's cookies; ignored when <see cref="Credentials"/> is set.</summary>
    public bool UseSessionCookies { get; init; }

    /// <summary><c>http:</c> or <c>https:</c>, for a URL built from parts; <c>http:</c> when null.</summary>
    public string? Protocol { get; init; }

    /// <summary><c>hostname:port</c>, for a URL built from parts.</summary>
    public string? Host { get; init; }

    /// <summary>The host name, for a URL built from parts; <c>localhost</c> when null.</summary>
    public string? Hostname { get; init; }

    /// <summary>The port, for a URL built from parts.</summary>
    public int? Port { get; init; }

    /// <summary>The path, query and fragment, for a URL built from parts.</summary>
    public string? Path { get; init; }

    /// <summary>The redirect mode.</summary>
    public RedirectMode Redirect { get; init; }

    /// <summary>The origin the request is made from.</summary>
    public string? Origin { get; init; }

    /// <summary>The referrer policy.</summary>
    public ReferrerPolicy? ReferrerPolicy { get; init; }

    /// <summary>The cache mode.</summary>
    public RequestCache? Cache { get; init; }

    /// <summary>The network priority.</summary>
    public RequestPriority? Priority { get; init; }

    /// <summary>The RFC 9218 incremental flag; true when null.</summary>
    public bool? PriorityIncremental { get; init; }

    // Set by fetch only: Electron's allowAnyProtocol().
    internal bool AllowNonHttpProtocols { get; init; }
}

/// <summary>Arguments for <see cref="ClientRequest.Redirect"/>.</summary>
/// <param name="StatusCode">The redirect's status code.</param>
/// <param name="Method">The method the redirected request uses.</param>
/// <param name="RedirectUrl">Where it goes.</param>
/// <param name="ResponseHeaders">The redirect response's headers: lower-case names, every value.</param>
public sealed record RedirectEventArgs(int StatusCode, string Method, string RedirectUrl, IReadOnlyDictionary<string, IReadOnlyList<string>> ResponseHeaders);

/// <summary>Upload state. Electron's <c>UploadProgress</c>.</summary>
/// <param name="Active">Whether the request is uploading.</param>
/// <param name="Started">Whether the upload began.</param>
/// <param name="Current">Bytes sent.</param>
/// <param name="Total">Bytes to send.</param>
public sealed record UploadProgress(bool Active, bool Started, long Current, long Total);

/// <summary>A network failure, with Chromium's error name as the message (e.g. <c>net::ERR_CONNECTION_REFUSED</c>),
/// or one of Electron's request errors.</summary>
public sealed class NetException(string message) : Exception(message);

/// <summary>A printer. Electron's <c>PrinterInfo</c>.</summary>
/// <param name="Name">The name the system knows it by.</param>
/// <param name="DisplayName">The name to show.</param>
/// <param name="Description">The printer's comment, empty when it has none.</param>
/// <param name="Options">Extra details, e.g. <c>printer-location</c>.</param>
public sealed record PrinterInfo(string Name, string DisplayName, string Description, IReadOnlyDictionary<string, string> Options);
