using System.Globalization;
using System.Net;
using System.Text;

namespace Oriel;

/// <summary>Chromium's checks on a cookie set through the API, ported so <see cref="Cookies.SetAsync"/> can reject with
/// Electron's message: CEF's <c>set_cookie</c> reports only success. Electron's <c>cookies.set()</c> builds the cookie
/// with <c>CanonicalCookie::CreateSanitizedCookie</c> (net/cookies/canonical_cookie.cc) and stores it through
/// <c>CookieMonster::SetCanonicalCookie</c>, whose context checks are <c>CookieBase::IsSetPermittedInContext</c>.</summary>
/// <remarks>The public suffix list is approximated by the last label: a host's registrable domain is its last two
/// labels. Checks that need the store's contents (overwriting a Secure or HttpOnly cookie) are left to CEF.</remarks>
internal static class CookieRules
{
    /// <summary><c>net::CookieInclusionStatus::ExclusionReason</c>, with Chromium's values; the order of the message.</summary>
    internal enum Exclusion
    {
        SecureOnly = 2,
        DomainMismatch = 3,
        SameSiteNoneInsecure = 8,
        FailureToStore = 10,
        InvalidDomain = 14,
        InvalidPrefix = 15,
        NameValuePairExceedsMaxSize = 17,
        AttributeValueExceedsMaxSize = 18,
        DisallowedCharacter = 24,
        NoCookieContent = 26,
        InvalidPath = 28,
        AmbiguousSerialization = 29,
    }

    /// <summary><c>net::CookieInclusionStatus::WarningReason</c>, with Chromium's values.</summary>
    internal enum Warning
    {
        DomainNonAscii = 6,
        TentativelyAllowingSecureSourceScheme = 9,
    }

    // ParsedCookie::kMaxCookieNamePlusValueSize and kMaxCookieAttributeValueSize (net/cookies/parsed_cookie.h).
    internal const int MaxNamePlusValueBytes = 4096;
    internal const int MaxAttributeValueBytes = 1024;

    private static readonly string[] s_prefixes = ["__Secure-", "__Host-Http-", "__Http-", "__Host-"];

    /// <summary>A cookie's inclusion status: the reasons it was excluded and the warnings raised.</summary>
    internal sealed class Status
    {
        public SortedSet<Exclusion> Exclusions { get; } = [];
        public SortedSet<Warning> Warnings { get; } = [];
        public bool IsInclude => Exclusions.Count == 0;
    }

    /// <summary>What Chromium would store, or why it would not.</summary>
    /// <param name="Rejection">Electron's rejection message; null when the cookie passes.</param>
    /// <param name="Domain">The stored domain: the host for a host-only cookie, else the domain with a leading dot.</param>
    /// <param name="Path">The stored path, before percent-encoding.</param>
    internal sealed record Verdict(string? Rejection, string Domain, string Path);

    /// <summary>Electron's message for a cookie Chromium refused for a reason Oriel cannot see; the same fallback
    /// Electron uses when <c>CreateSanitizedCookie</c> gives no reason.</summary>
    internal static string FailureToStoreMessage { get; } = Message(StatusOf(Exclusion.FailureToStore));

    /// <summary>Runs Chromium's checks for a cookie set on <paramref name="url"/>.</summary>
    internal static Verdict Check(string url, string name, string value, string? domain, string? path, bool secure, bool httpOnly,
        CookieSameSite sameSite)
    {
        domain ??= "";
        path ??= "";
        // Electron rejects a URL GURL finds invalid this way; .NET alone would read "/x" as a file URI on Linux.
        if (!HasScheme(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new Verdict(Message(StatusOf(Exclusion.InvalidDomain)), "", "");
        var sanitized = Sanitize(uri, name, value, domain, path, secure, httpOnly, out var cookieDomain, out var cookiePath);
        if (!sanitized.IsInclude) return new Verdict(Message(sanitized), cookieDomain, cookiePath);
        var stored = SetPermitted(uri, secure, sameSite);
        return new Verdict(stored.IsInclude ? null : Message(stored), cookieDomain, cookiePath);
    }

    private static bool HasScheme(string url)
    {
        var colon = url.IndexOf(':');
        return colon > 0 && char.IsAsciiLetter(url[0]) && url[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    /// <summary><c>CanonicalCookie::CreateSanitizedCookie</c>'s checks, in its order.</summary>
    internal static Status Sanitize(Uri url, string name, string value, string domain, string path, bool secure, bool httpOnly,
        out string cookieDomain, out string cookiePath)
    {
        var status = new Status();
        if (ParseTokenString(name) != name || ParseValueString(value) != value || ParseValueString(path) != path)
            status.Exclusions.Add(Exclusion.DisallowedCharacter);
        IsValidNameValuePair(name, value, status);

        var domainIsValid = true;
        if (ParseValueString(domain) != domain || !HasValidCharSet(domain))
        {
            status.Exclusions.Add(Exclusion.InvalidDomain);
            domainIsValid = false;
        }
        if (Utf8Length(domain) > MaxAttributeValueBytes)
        {
            status.Exclusions.Add(Exclusion.AttributeValueExceedsMaxSize);
            domainIsValid = false;
        }
        var domainAttribute = domainIsValid ? domain : "";

        var host = Host(url);
        string? canonicalDomain = null;
        if (host.StartsWith('.') || (canonicalDomain = CookieDomain(url, host, domainAttribute, status)) is null)
            status.Exclusions.Add(Exclusion.InvalidDomain);
        cookieDomain = canonicalDomain ?? "";

        if (secure && !IsCryptographic(url)) status.Warnings.Add(Warning.TentativelyAllowingSecureSourceScheme);

        cookiePath = CanonPath(url, path);
        if (path.Length > 0)
        {
            if (cookiePath != path) status.Exclusions.Add(Exclusion.InvalidPath);
            else if (EncodedPathLength(cookiePath) > MaxAttributeValueBytes) status.Exclusions.Add(Exclusion.AttributeValueExceedsMaxSize);
        }

        if (!IsPrefixValid(name, url, secure, httpOnly, domainAttribute, cookiePath)) status.Exclusions.Add(Exclusion.InvalidPrefix);
        if (name.Length == 0 && HasHiddenPrefixName(value)) status.Exclusions.Add(Exclusion.InvalidPrefix);
        if (name.Length == 0 && value.Contains('=')) status.Exclusions.Add(Exclusion.AmbiguousSerialization);
        return status;
    }

    /// <summary>The checks of <c>CookieBase::IsSetPermittedInContext</c> that hold for any store contents, with
    /// Electron's options: HttpOnly allowed for an HttpOnly cookie, an inclusive SameSite context. Its warning for a
    /// Secure cookie on localhost is left out: no exclusion can come with it, so no message shows it.</summary>
    internal static Status SetPermitted(Uri url, bool secure, CookieSameSite sameSite)
    {
        var status = new Status();
        if (secure && !IsCryptographic(url) && !IsLocalhost(url)) status.Exclusions.Add(Exclusion.SecureOnly);

        if (sameSite == CookieSameSite.NoRestriction && !secure) status.Exclusions.Add(Exclusion.SameSiteNoneInsecure);
        return status;
    }

    /// <summary>Electron's <c>InclusionStatusToString</c>: each reason's text, then
    /// <c>CookieInclusionStatus::GetDebugString</c>.</summary>
    internal static string Message(Status status)
    {
        var text = new StringBuilder("Failed to set cookie - ");
        foreach (var reason in status.Exclusions) text.Append(Describe(reason));
        foreach (var reason in status.Exclusions) text.Append(DebugName(reason)).Append(", ");
        if (status.Warnings.Count == 0) text.Append("DO_NOT_WARN, ");
        foreach (var warning in status.Warnings) text.Append(DebugName(warning)).Append(", ");
        return text.Append("NO_EXEMPTION").ToString();
    }

    private static Status StatusOf(Exclusion reason)
    {
        var status = new Status();
        status.Exclusions.Add(reason);
        return status;
    }

    // Electron's text for each reason (shell/browser/api/electron_api_cookies.cc).
    private static string Describe(Exclusion reason) => reason switch
    {
        Exclusion.SecureOnly => "The cookie was Secure, but the URL was not allowed to access Secure cookies.",
        Exclusion.DomainMismatch => "The cookie's domain attribute did not match the domain of the URL attempting access.",
        Exclusion.SameSiteNoneInsecure => "The cookie specified SameSite=None, but it was not Secure.",
        Exclusion.FailureToStore => "The cookie was malformed and could not be stored",
        Exclusion.InvalidDomain => "The cookie was set with an invalid Domain attribute.",
        Exclusion.InvalidPrefix => "The cookie was set with an invalid __Host- or __Secure- prefix.",
        Exclusion.NameValuePairExceedsMaxSize => "The cookie exceeded the name/value pair size limit.",
        Exclusion.AttributeValueExceedsMaxSize => "Cookie exceeded the attribute size limit.",
        Exclusion.DisallowedCharacter => "The cookie contains ASCII control characters",
        Exclusion.NoCookieContent => "The cookie contains no content or only whitespace.",
        Exclusion.InvalidPath => "The cookie was set with an invalid Path attribute.",
        Exclusion.AmbiguousSerialization => "Cookie was rejected in parsing due to having an ambiguous serialization.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static string DebugName(Exclusion reason) => reason switch
    {
        Exclusion.SecureOnly => "EXCLUDE_SECURE_ONLY",
        Exclusion.DomainMismatch => "EXCLUDE_DOMAIN_MISMATCH",
        Exclusion.SameSiteNoneInsecure => "EXCLUDE_SAMESITE_NONE_INSECURE",
        Exclusion.FailureToStore => "EXCLUDE_FAILURE_TO_STORE",
        Exclusion.InvalidDomain => "EXCLUDE_INVALID_DOMAIN",
        Exclusion.InvalidPrefix => "EXCLUDE_INVALID_PREFIX",
        Exclusion.NameValuePairExceedsMaxSize => "EXCLUDE_NAME_VALUE_PAIR_EXCEEDS_MAX_SIZE",
        Exclusion.AttributeValueExceedsMaxSize => "EXCLUDE_ATTRIBUTE_VALUE_EXCEEDS_MAX_SIZE",
        Exclusion.DisallowedCharacter => "EXCLUDE_DISALLOWED_CHARACTER",
        Exclusion.NoCookieContent => "EXCLUDE_NO_COOKIE_CONTENT",
        Exclusion.InvalidPath => "EXCLUDE_INVALID_PATH",
        Exclusion.AmbiguousSerialization => "EXCLUDE_AMBIGUOUS_SERIALIZATION",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static string DebugName(Warning warning) => warning switch
    {
        Warning.DomainNonAscii => "WARN_DOMAIN_NON_ASCII",
        Warning.TentativelyAllowingSecureSourceScheme => "WARN_TENTATIVELY_ALLOWING_SECURE_SOURCE_SCHEME",
        _ => throw new ArgumentOutOfRangeException(nameof(warning)),
    };

    // ParsedCookie's tokenizer: a string is valid when parsing it gives it back unchanged.
    private const string Whitespace = " \t";

    private static int FirstTerminator(string s)
    {
        var end = s.IndexOfAny(['\n', '\r', '\0']);
        return end < 0 ? s.Length : end;
    }

    /// <summary><c>ParsedCookie::ParseTokenString</c>: the token up to <c>;</c> or <c>=</c>, without surrounding whitespace.</summary>
    internal static string ParseTokenString(string token)
    {
        var end = FirstTerminator(token);
        var start = 0;
        while (start < end && Whitespace.Contains(token[start])) start++;
        if (start == end) return "";
        var realEnd = start;
        while (realEnd < end && token[realEnd] != ';' && token[realEnd] != '=') realEnd++;
        var tokenEnd = realEnd;
        while (tokenEnd > start && Whitespace.Contains(token[tokenEnd - 1])) tokenEnd--;
        return token[start..tokenEnd];
    }

    /// <summary><c>ParsedCookie::ParseValueString</c>: the value up to <c>;</c>, without surrounding whitespace.</summary>
    internal static string ParseValueString(string value)
    {
        var end = FirstTerminator(value);
        var start = 0;
        while (start < end && Whitespace.Contains(value[start])) start++;
        var valueEnd = start;
        while (valueEnd < end && value[valueEnd] != ';') valueEnd++;
        while (valueEnd > start && Whitespace.Contains(value[valueEnd - 1])) valueEnd--;
        return value[start..valueEnd];
    }

    // HttpUtil::IsControlChar.
    private static bool IsControl(char c) => c <= 0x1F || c == 0x7F;

    private static bool HasValidCharSet(string value) => !value.Any(c => IsControl(c) || c == ';');

    /// <summary><c>ParsedCookie::IsValidCookieNameValuePair</c>: it stops at the first failure.</summary>
    internal static bool IsValidNameValuePair(string name, string value, Status status)
    {
        if (name.Length == 0 && value.Length == 0)
        {
            status.Exclusions.Add(Exclusion.NoCookieContent);
            return false;
        }
        if (Utf8Length(name) + Utf8Length(value) > MaxNamePlusValueBytes)
        {
            status.Exclusions.Add(Exclusion.NameValuePairExceedsMaxSize);
            return false;
        }
        if (name.Any(c => IsControl(c) || c == ';' || c == '=') || !HasValidCharSet(value))
        {
            status.Exclusions.Add(Exclusion.DisallowedCharacter);
            return false;
        }
        return true;
    }

    private static int Utf8Length(string s) => Encoding.UTF8.GetByteCount(s);

    private static string Host(Uri url) => url.HostNameType == UriHostNameType.IPv6 ? $"[{url.IdnHost.Trim('[', ']')}]" : url.IdnHost.ToLowerInvariant();

    private static bool IsIp(Uri url) => url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;

    // GURL::SchemeIsCryptographic.
    private static bool IsCryptographic(Uri url) => url.Scheme is "https" or "wss";

    // net::IsLocalhost: localhost, *.localhost, 127.0.0.0/8 and ::1.
    internal static bool IsLocalhost(Uri url)
    {
        if (IsIp(url) && IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var address)) return IPAddress.IsLoopback(address);
        var host = url.IdnHost.ToLowerInvariant().TrimEnd('.');
        return host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal);
    }

    /// <summary><c>cookie_util::GetCookieDomainWithString</c>; null when the domain is invalid for the URL.</summary>
    internal static string? CookieDomain(Uri url, string host, string domain, Status status)
    {
        if (!domain.All(char.IsAscii)) status.Warnings.Add(Warning.DomainNonAscii);
        if (host.EndsWith("..", StringComparison.Ordinal)) return null;
        var isIp = IsIp(url);
        var matchesHost = string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || domain.StartsWith('.') && string.Equals(host, domain[1..], StringComparison.OrdinalIgnoreCase);
        if (domain.Length == 0 || isIp && matchesHost) return host;
        if (isIp) return null;
        if (domain.Contains('%')) return null;
        var cookieDomain = CanonicalizeHost(domain);
        if (cookieDomain.Length == 0) return null;
        if (cookieDomain[0] != '.') cookieDomain = "." + cookieDomain;

        var hostRegistrable = EffectiveDomain(url.Scheme, host);
        if (hostRegistrable.Length == 0)
        {
            // An exact match is a host cookie; a single-label host cannot set a domain cookie.
            var normalized = (domain[0] == '.' ? domain[1..] : domain).ToLowerInvariant();
            return host == normalized ? normalized : null;
        }
        if (hostRegistrable != EffectiveDomain(url.Scheme, cookieDomain))
        {
            status.Exclusions.Add(Exclusion.DomainMismatch);
            return null;
        }
        var notSuffix = host.Length < cookieDomain.Length
            ? cookieDomain[1..] != host
            : !host.EndsWith(cookieDomain, StringComparison.Ordinal);
        return notSuffix ? null : cookieDomain;
    }

    // cookie_util::GetEffectiveDomain, with the public suffix list cut to "the last label is the suffix".
    private static string EffectiveDomain(string scheme, string host)
    {
        var bare = host.TrimStart('.');
        if (scheme is not ("http" or "https" or "ws" or "wss")) return bare;
        var labels = bare.Split('.');
        return labels.Length < 2 || labels.Any(l => l.Length == 0) ? "" : $"{labels[^2]}.{labels[^1]}";
    }

    // url::CanonicalizeHost for a domain attribute: lower-case, punycode, no forbidden host code points.
    private static string CanonicalizeHost(string domain)
    {
        const string forbidden = " #/:<>?@[\\]^|";
        if (domain.Any(c => IsControl(c) || forbidden.Contains(c))) return "";
        if (domain.All(char.IsAscii)) return domain.ToLowerInvariant();
        try { return new IdnMapping().GetAscii(domain.TrimStart('.')) is var ascii && domain.StartsWith('.') ? "." + ascii : ascii; }
        catch (ArgumentException) { return ""; }
    }

    /// <summary><c>cookie_util::CanonPathWithString</c>: the given path when it starts with <c>/</c>, else the URL's
    /// directory.</summary>
    internal static string CanonPath(Uri url, string path)
    {
        if (path.Length > 0 && path[0] == '/') return path;
        var urlPath = url.AbsolutePath;
        var slash = urlPath.LastIndexOf('/');
        return slash <= 0 ? "/" : urlPath[..slash];
    }

    // url::CanonicalizePath's escaping of a path's characters, for the length check.
    private static int EncodedPathLength(string path)
    {
        const string escaped = " \"#<>`{}";
        var length = 0;
        foreach (var b in Encoding.UTF8.GetBytes(path))
            length += b <= 0x20 || b >= 0x7F || escaped.Contains((char)b) ? 3 : 1;
        return length;
    }

    /// <summary><c>cookie_util::IsCookiePrefixValid</c> for a name's <c>__Secure-</c>, <c>__Host-</c>, <c>__Http-</c> or
    /// <c>__Host-Http-</c> prefix, matched without case.</summary>
    internal static bool IsPrefixValid(string name, Uri url, bool secure, bool httpOnly, string domainAttribute, string cookiePath)
    {
        var prefix = s_prefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        var secureOk = secure && (IsCryptographic(url) || IsLocalhost(url));
        var hostOk = secureOk && cookiePath == "/" && (domainAttribute.Length == 0 || IsIp(url) && Host(url) == domainAttribute);
        return prefix switch
        {
            "__Secure-" => secureOk,
            "__Host-Http-" => secureOk && httpOnly && hostOk,
            "__Http-" => secureOk && httpOnly,
            "__Host-" => hostOk,
            _ => true,
        };
    }

    // cookie_util::HasHiddenPrefixName: a nameless cookie whose value starts with a prefix.
    private static bool HasHiddenPrefixName(string value)
    {
        var trimmed = value.TrimStart(' ', '\t');
        return s_prefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
