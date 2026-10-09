using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Electron's and Chromium's request rules that need no network: option parsing, header validation,
/// redirect computation, error names.</summary>
internal static class NetRules
{
    // Node.js keeps the first of these when a response repeats them (net-client-request.ts).
    private static readonly HashSet<string> s_discardableDuplicates = new(StringComparer.Ordinal)
    {
        "content-type", "content-length", "user-agent", "referer", "host", "authorization", "proxy-authorization",
        "if-modified-since", "if-unmodified-since", "from", "location", "max-forwards", "retry-after", "etag",
        "last-modified", "server", "age", "expires",
    };

    // content::IsSafeRedirectTarget's unsafe schemes, plus WebUI schemes (never same-scheme exempt).
    private static readonly HashSet<string> s_unsafeRedirectSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "file", "filesystem", "blob", "data", "chrome", "chrome-untrusted", "devtools",
    };

    /// <summary>Chromium's <c>HttpUtil::IsValidHeaderName</c>: a non-empty RFC 7230 token.</summary>
    public static bool IsValidHeaderName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
        {
            if (c >= 0x7F || c <= 0x20 || "()<>@,;:\\\"/[]?={}".Contains(c)) return false;
        }
        return true;
    }

    /// <summary>Chromium's <c>HttpUtil::IsValidHeaderValue</c>: no NUL, CR or LF.</summary>
    public static bool IsValidHeaderValue(string value) => value.AsSpan().IndexOfAny('\0', '\r', '\n') < 0;

    /// <summary>Electron's <c>validateHeader</c>, with its messages.</summary>
    public static void ValidateHeader(string? name, string? value)
    {
        if (name is null) throw new ArgumentException("`name` should be a string in setHeader(name, value)");
        if (value is null) throw new ArgumentException($"`value` required in setHeader(\"{name}\", value)");
        if (!IsValidHeaderName(name)) throw new ArgumentException($"Invalid header name: '{name}'");
        if (!IsValidHeaderValue(value)) throw new ArgumentException($"Invalid value for header '{name}': '{value}'");
    }

    /// <summary>Chromium's <c>ErrorToString</c>: <c>net::ERR_…</c> for a negative net error code.</summary>
    public static string ErrorString(int code)
        => code == 0 ? "net::OK" : Enum.GetName((cef_errorcode_t)code) is { } name ? "net::" + name : "net::ERR_<unknown>";

    /// <summary>The request URL from Electron's options: <see cref="ClientRequestOptions.Url"/>, else built from its
    /// parts the way Node's <c>url.format</c> does (net-client-request.ts parseOptions).</summary>
    /// <exception cref="ArgumentException">The path contains a space.</exception>
    public static string BuildUrl(ClientRequestOptions options)
    {
        if (!string.IsNullOrEmpty(options.Url)) return options.Url;
        var protocol = options.Protocol ?? "http:";
        string host;
        if (options.Host is { Length: > 0 } h) host = h;
        else host = (options.Hostname is { Length: > 0 } name ? name : "localhost") + (options.Port is { } port and not 0 ? $":{port}" : "");
        if (options.Path is { } p && p.Contains(' ', StringComparison.Ordinal))
            throw new ArgumentException("Request path contains unescaped characters");
        var path = new Uri(new Uri("http://localhost"), options.Path is { Length: > 0 } given ? given : "/");
        // Node's url.format adds "//" for the special schemes.
        return $"{protocol}//{host}{path.AbsolutePath}{path.Query}{path.Fragment}";
    }

    /// <summary>Response headers grouped as Electron's net converter does: lower-case names, values in order.</summary>
    public static Dictionary<string, IReadOnlyList<string>> GroupHeaders(IEnumerable<KeyValuePair<string, string>> raw)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, value) in raw)
        {
            var key = name.ToLowerInvariant();
            if (!grouped.TryGetValue(key, out var list)) grouped[key] = list = [];
            list.Add(value);
        }
        return grouped.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Electron's <c>IncomingMessage.headers</c>: Node's duplicate rules, set-cookie always a list.</summary>
    public static Dictionary<string, IReadOnlyList<string>> FlattenHeaders(IReadOnlyList<KeyValuePair<string, string>> raw)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, values) in GroupHeaders(raw))
            result[name] = [s_discardableDuplicates.Contains(name) ? values[0] : string.Join(", ", values)];
        var cookies = raw.Where(h => h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        if (cookies.Count > 0) result["set-cookie"] = cookies;
        return result;
    }

    /// <summary>Chromium's <c>ComputeMethodForRedirect</c>.</summary>
    public static string RedirectMethod(string method, int status)
        => (status == 303 && method != "HEAD") || ((status == 301 || status == 302) && method == "POST") ? "GET" : method;

    /// <summary>The redirect target: <paramref name="location"/> resolved against the request URL, keeping the old
    /// fragment when the new URL has none (Chromium's <c>RedirectInfo::ComputeRedirectInfo</c>); null when invalid.</summary>
    public static string? RedirectUrl(string currentUrl, string location)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current) || !Uri.TryCreate(current, location, out var target)) return null;
        if (current.Fragment.Length > 1 && target.Fragment.Length == 0)
            target = new UriBuilder(target) { Fragment = current.Fragment[1..] }.Uri;
        return target.AbsoluteUri;
    }

    /// <summary>Chromium's <c>content::IsSafeRedirectTarget</c> for a request from <paramref name="from"/>.</summary>
    public static bool IsSafeRedirectTarget(string from, string to)
    {
        if (!Uri.TryCreate(to, UriKind.Absolute, out var target)) return false;
        if (!s_unsafeRedirectSchemes.Contains(target.Scheme)) return true;
        return Uri.TryCreate(from, UriKind.Absolute, out var source) && source.Scheme == target.Scheme
            && target.Scheme is "file" or "filesystem";
    }

    /// <summary>Chromium's <c>RedirectUtil::UpdateHttpRequest</c> plus fetch's cross-origin Authorization rule:
    /// the headers the redirected request keeps, and whether its body goes.</summary>
    public static (List<KeyValuePair<string, string>> Headers, bool ClearBody) RedirectHeaders(
        IReadOnlyList<KeyValuePair<string, string>> headers, string oldMethod, string newMethod, string oldUrl, string newUrl)
    {
        var kept = headers.ToList();
        var methodChanged = newMethod != oldMethod;
        if (methodChanged)
            kept.RemoveAll(h => h.Key.ToLowerInvariant() is "origin" or "content-length" or "content-type" or "content-encoding"
                or "content-language" or "content-location");
        if (!SameOrigin(oldUrl, newUrl))
        {
            kept.RemoveAll(h => h.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < kept.Count; i++)
            {
                if (kept[i].Key.Equals("origin", StringComparison.OrdinalIgnoreCase)) kept[i] = new(kept[i].Key, "null");
            }
        }
        return (kept, methodChanged);
    }

    /// <summary>Whether two URLs share scheme, host and port.</summary>
    public static bool SameOrigin(string a, string b)
        => Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
            && string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port;

    /// <summary>CEF's request flags for Electron's credentials and cache options. Redirects always stop, to be
    /// reported and followed hop by hop.</summary>
    /// <remarks>CEF ties cookies to credentials: without <c>omit</c> both are sent, so Electron's default (credentials
    /// without cookies) sends cookies too.</remarks>
    public static cef_urlrequest_flags_t RequestFlags(RequestCredentials? credentials, RequestCache? cache)
    {
        var flags = cef_urlrequest_flags_t.UR_FLAG_STOP_ON_REDIRECT;
        if (credentials != RequestCredentials.Omit) flags |= cef_urlrequest_flags_t.UR_FLAG_ALLOW_STORED_CREDENTIALS;
        flags |= cache switch
        {
            RequestCache.NoStore => cef_urlrequest_flags_t.UR_FLAG_DISABLE_CACHE,
            RequestCache.Reload => cef_urlrequest_flags_t.UR_FLAG_SKIP_CACHE,
            RequestCache.OnlyIfCached => cef_urlrequest_flags_t.UR_FLAG_ONLY_FROM_CACHE,
            _ => cef_urlrequest_flags_t.UR_FLAG_NONE,
        };
        return flags;
    }

    /// <summary>Electron's referrer policy names mapped to Chromium's (url_loader.cc's lookup); null and the empty
    /// string are strict-origin-when-cross-origin, Chromium's default net policy.</summary>
    public static cef_referrer_policy_t ReferrerPolicyOf(ReferrerPolicy? policy) => PendingLoad.ToCef(policy ?? ReferrerPolicy.Default);

    /// <summary>Electron's <c>(options.method || 'GET').toUpperCase()</c>.</summary>
    public static string NormalizeMethod(string? method) => (method is { Length: > 0 } m ? m : "GET").ToUpperInvariant();
}
