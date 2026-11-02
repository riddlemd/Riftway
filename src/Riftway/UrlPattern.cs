using System.Text.RegularExpressions;

namespace Riftway;

/// <summary>Chromium's URL match patterns (<c>extensions::URLPattern</c> with every scheme valid), as Electron's
/// <c>webRequest</c> filters use them.</summary>
internal sealed class UrlPattern
{
    private readonly bool _all;
    private readonly string _scheme = "";
    private readonly string _host = "";
    private readonly bool _subdomains;
    private readonly string? _port;
    private readonly Regex? _path;

    private UrlPattern(bool all) => _all = all;

    private UrlPattern(string scheme, string host, bool subdomains, string? port, Regex path)
    {
        _scheme = scheme;
        _host = host;
        _subdomains = subdomains;
        _port = port;
        _path = path;
    }

    /// <summary>Parses <paramref name="pattern"/>; throws <see cref="ArgumentException"/> for one Chromium rejects.</summary>
    public static UrlPattern Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern == "<all_urls>") return new UrlPattern(all: true);
        var separator = pattern.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0) throw Invalid(pattern);
        var scheme = pattern[..separator].ToLowerInvariant();
        if (scheme != "*" && !Regex.IsMatch(scheme, "^[a-z][a-z0-9+.-]*$")) throw Invalid(pattern);
        var rest = pattern[(separator + 3)..];
        var slash = rest.IndexOf('/');
        if (slash < 0) throw Invalid(pattern);
        var authority = rest[..slash];
        var path = rest[slash..];
        if (authority.Length == 0 && scheme != "file") throw Invalid(pattern);

        string? port = null;
        var colon = authority.LastIndexOf(':');
        if (colon >= 0 && !authority.EndsWith(']'))
        {
            port = authority[(colon + 1)..];
            authority = authority[..colon];
            if (port != "*" && !(port.Length > 0 && port.All(char.IsAsciiDigit))) throw Invalid(pattern);
        }
        var subdomains = false;
        var host = authority.ToLowerInvariant();
        if (host == "*") (host, subdomains) = ("", true);
        else if (host.StartsWith("*.", StringComparison.Ordinal)) (host, subdomains) = (host[2..], true);
        if (host.Contains('*')) throw Invalid(pattern);

        var glob = "^" + string.Join(".*", path.Split('*').Select(Regex.Escape)) + "$";
        return new UrlPattern(scheme, host, subdomains, port, new Regex(glob, RegexOptions.CultureInvariant));
    }

    private static ArgumentException Invalid(string pattern) => new($"Invalid url pattern {pattern}", nameof(pattern));

    /// <summary>Whether <paramref name="url"/> matches.</summary>
    public bool Matches(string url)
    {
        if (_all) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (_scheme == "*" ? scheme is not ("http" or "https") : scheme != _scheme) return false;
        var host = uri.IdnHost;
        if (!(_subdomains && _host.Length == 0) && host != _host && !(_subdomains && host.EndsWith("." + _host, StringComparison.Ordinal)))
            return false;
        if (_port is { } port && port != "*" && port != (uri.IsDefaultPort ? DefaultPort(scheme) : uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            return false;
        return _path!.IsMatch(uri.AbsolutePath + uri.Query);
    }

    private static string DefaultPort(string scheme) => scheme switch
    {
        "http" or "ws" => "80",
        "https" or "wss" => "443",
        "ftp" => "21",
        _ => "",
    };
}
