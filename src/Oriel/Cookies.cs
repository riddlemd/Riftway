using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A session's cookie store. Electron's <c>Cookies</c>.</summary>
public sealed class Cookies
{
    // cef_basetime_t counts microseconds from 1601-01-01 UTC.
    private const long UnixEpochMicroseconds = 11_644_473_600_000_000;

    private readonly Session _session;
    private nint _manager;

    internal Cookies(Session session) => _session = session;

    /// <summary>The cookies matching <paramref name="filter"/>, all of them when it is null. Electron's <c>cookies.get()</c>.</summary>
    /// <remarks>With <see cref="CookieFilter.Url"/>, only the cookies a request to that URL would send, HttpOnly ones included.</remarks>
    public async Task<IReadOnlyList<Cookie>> GetAsync(CookieFilter? filter = null)
    {
        filter ??= new CookieFilter();
        var cookies = await StartVisit(filter.Url).ConfigureAwait(false);
        return [.. cookies.Select(ToCookie).Where(filter.Matches)];
    }

    private unsafe Task<IReadOnlyList<CefCookie>> StartVisit(string? url)
    {
        var visitor = new CookieVisitor();
        CefThread.RunOnUi(() =>
        {
            var manager = Manager();
            if (url is not null)
            {
                cef_string_utf16_t u = default;
                CefString.Set(&u, url);
                manager->visit_url_cookies(manager, &u, 1, visitor.Native);
                CefString.Clear(&u);
            }
            else
            {
                manager->visit_all_cookies(manager, visitor.Native);
            }
        });
        return visitor.Task;
    }

    /// <summary>Stores a cookie for <see cref="CookieDetails.Url"/>. Electron's <c>cookies.set()</c>.</summary>
    /// <exception cref="ArgumentException">Chromium rejected the cookie, e.g. its domain does not match the URL.</exception>
    public async Task SetAsync(CookieDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrEmpty(details.Url);
        var (accepted, stored) = StartSet(details);
        if (!accepted || !await stored.ConfigureAwait(false))
            throw new ArgumentException($"Chromium rejected the cookie '{details.Name}' for {details.Url}.", nameof(details));
    }

    private unsafe (bool Accepted, Task<bool> Stored) StartSet(CookieDetails details)
    {
        var callback = new SetCookieCallback();
        var accepted = true;
        CefThread.RunOnUi(() =>
        {
            var manager = Manager();
            var cookie = new cef_cookie_t
            {
                size = (nuint)sizeof(cef_cookie_t),
                secure = details.Secure ? 1 : 0,
                httponly = details.HttpOnly ? 1 : 0,
                has_expires = details.ExpirationDate is null ? 0 : 1,
                expires = new cef_basetime_t { val = details.ExpirationDate is { } seconds ? ToBaseTime(seconds) : 0 },
                same_site = ToCef(details.SameSite),
                priority = cef_cookie_priority_t.CEF_COOKIE_PRIORITY_MEDIUM,
            };
            cef_string_utf16_t url = default;
            CefString.Set(&url, details.Url);
            CefString.Set(&cookie.name, details.Name);
            CefString.Set(&cookie.value, details.Value);
            CefString.Set(&cookie.domain, details.Domain);
            CefString.Set(&cookie.path, details.Path);
            // False means the URL was invalid; the callback is then never called.
            accepted = manager->set_cookie(manager, &url, &cookie, callback.Native) != 0;
            CefString.Clear(&url);
            CefString.Clear(&cookie.name);
            CefString.Clear(&cookie.value);
            CefString.Clear(&cookie.domain);
            CefString.Clear(&cookie.path);
        });
        return (accepted, callback.Task);
    }

    /// <summary>Deletes the cookie named <paramref name="name"/> that <paramref name="url"/> would receive.
    /// Electron's <c>cookies.remove()</c>.</summary>
    public async Task RemoveAsync(string url, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var (accepted, deleted) = StartRemove(url, name);
        if (!accepted) throw new ArgumentException($"'{url}' is not a valid URL.", nameof(url));
        await deleted.ConfigureAwait(false);
    }

    private unsafe (bool Accepted, Task<int> Deleted) StartRemove(string url, string name)
    {
        var callback = new DeleteCookiesCallback();
        var accepted = true;
        CefThread.RunOnUi(() =>
        {
            var manager = Manager();
            cef_string_utf16_t u = default, n = default;
            CefString.Set(&u, url);
            CefString.Set(&n, name);
            accepted = manager->delete_cookies(manager, &u, &n, callback.Native) != 0;
            CefString.Clear(&u);
            CefString.Clear(&n);
        });
        return (accepted, callback.Task);
    }

    /// <summary>Writes unsaved cookies to disk. Electron's <c>cookies.flushStore()</c>.</summary>
    public unsafe Task FlushStoreAsync()
    {
        var callback = new CompletionCallback();
        CefThread.RunOnUi(() =>
        {
            var manager = Manager();
            manager->flush_store(manager, callback.Native);
        });
        return callback.Task;
    }

    // UI thread.
    private unsafe cef_cookie_manager_t* Manager()
    {
        if (_manager == 0)
        {
            var context = _session.Native;
            _manager = (nint)context->get_cookie_manager(context, null);
            if (_manager == 0) throw new InvalidOperationException("CEF has no cookie store for this session.");
        }
        return (cef_cookie_manager_t*)_manager;
    }

    internal static Cookie ToCookie(CefCookie cookie) => new(
        cookie.Name, cookie.Value, cookie.Domain, cookie.Path)
    {
        HostOnly = !cookie.Domain.StartsWith('.'),
        Secure = cookie.Secure,
        HttpOnly = cookie.HttpOnly,
        Session = cookie.Expires is null,
        ExpirationDate = cookie.Expires is { } expires ? FromBaseTime(expires) : null,
        SameSite = cookie.SameSite switch
        {
            cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_NO_RESTRICTION => CookieSameSite.NoRestriction,
            cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_LAX_MODE => CookieSameSite.Lax,
            cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_STRICT_MODE => CookieSameSite.Strict,
            _ => CookieSameSite.Unspecified,
        },
    };

    internal static cef_cookie_same_site_t ToCef(CookieSameSite sameSite) => sameSite switch
    {
        CookieSameSite.NoRestriction => cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_NO_RESTRICTION,
        CookieSameSite.Lax => cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_LAX_MODE,
        CookieSameSite.Strict => cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_STRICT_MODE,
        _ => cef_cookie_same_site_t.CEF_COOKIE_SAME_SITE_UNSPECIFIED,
    };

    internal static double FromBaseTime(long microseconds) => (microseconds - UnixEpochMicroseconds) / 1_000_000.0;

    internal static long ToBaseTime(double unixSeconds) => (long)Math.Round(unixSeconds * 1_000_000) + UnixEpochMicroseconds;
}

/// <summary>Which sites a cookie is sent from. Electron's <c>sameSite</c>.</summary>
public enum CookieSameSite
{
    /// <summary>No attribute: Chromium treats it as <see cref="Lax"/>.</summary>
    Unspecified,
    /// <summary><c>SameSite=None</c>.</summary>
    NoRestriction,
    /// <summary><c>SameSite=Lax</c>.</summary>
    Lax,
    /// <summary><c>SameSite=Strict</c>.</summary>
    Strict,
}

/// <summary>A stored cookie. Electron's <c>Cookie</c>.</summary>
public sealed record Cookie(string Name, string Value, string Domain, string Path)
{
    /// <summary>Whether only <see cref="Domain"/> itself receives it, not its subdomains.</summary>
    public bool HostOnly { get; init; }

    /// <summary>Whether it is only sent over secure connections.</summary>
    public bool Secure { get; init; }

    /// <summary>Whether page scripts cannot read it.</summary>
    public bool HttpOnly { get; init; }

    /// <summary>Whether it ends with the session, having no expiry.</summary>
    public bool Session { get; init; }

    /// <summary>When it expires, in seconds since the UNIX epoch; null for a session cookie.</summary>
    public double? ExpirationDate { get; init; }

    /// <summary>Which sites it is sent from.</summary>
    public CookieSameSite SameSite { get; init; }
}

/// <summary>Which cookies <see cref="Cookies.GetAsync"/> returns; null properties match anything.
/// Electron's <c>cookies.get()</c> filter.</summary>
public sealed record CookieFilter
{
    /// <summary>Only cookies a request to this URL would send.</summary>
    public string? Url { get; init; }

    /// <summary>Only cookies with this name.</summary>
    public string? Name { get; init; }

    /// <summary>Only cookies for this domain or its subdomains.</summary>
    public string? Domain { get; init; }

    /// <summary>Only cookies with this path.</summary>
    public string? Path { get; init; }

    /// <summary>Only secure, or only insecure, cookies.</summary>
    public bool? Secure { get; init; }

    /// <summary>Only session, or only persistent, cookies.</summary>
    public bool? Session { get; init; }

    /// <summary>Only HttpOnly, or only script-visible, cookies.</summary>
    public bool? HttpOnly { get; init; }

    internal bool Matches(Cookie cookie)
        => (Name is null || Name == cookie.Name)
        && (Path is null || Path == cookie.Path)
        && (Domain is null || MatchesDomain(Domain, cookie.Domain))
        && (Secure is null || Secure == cookie.Secure)
        && (Session is null || Session == cookie.Session)
        && (HttpOnly is null || HttpOnly == cookie.HttpOnly);

    // Electron's rule: the cookie's domain, without its leading dot, is the filter's domain or a subdomain of it.
    internal static bool MatchesDomain(string filter, string domain)
    {
        var f = filter.TrimStart('.');
        return string.Equals(domain, f, StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith("." + f, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A cookie to store. Electron's <c>cookies.set()</c> details.</summary>
/// <param name="Url">The URL the cookie belongs to; its host is the domain when <see cref="Domain"/> is null.</param>
public sealed record CookieDetails(string Url)
{
    /// <summary>The name; empty by default.</summary>
    public string Name { get; init; } = "";

    /// <summary>The value; empty by default.</summary>
    public string Value { get; init; } = "";

    /// <summary>The domain, which makes it a domain cookie its subdomains receive too; null for a host-only cookie.</summary>
    public string? Domain { get; init; }

    /// <summary>The path; null takes it from <see cref="Url"/>.</summary>
    public string? Path { get; init; }

    /// <summary>Whether it is only sent over secure connections.</summary>
    public bool Secure { get; init; }

    /// <summary>Whether page scripts cannot read it.</summary>
    public bool HttpOnly { get; init; }

    /// <summary>When it expires, in seconds since the UNIX epoch; null makes a session cookie.</summary>
    public double? ExpirationDate { get; init; }

    /// <summary>Which sites it is sent from; <see cref="CookieSameSite.Lax"/> by default, as in Electron.</summary>
    public CookieSameSite SameSite { get; init; } = CookieSameSite.Lax;
}
