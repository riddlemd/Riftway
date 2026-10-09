using System.Text;

namespace Oriel;

/// <summary>DNS-over-HTTPS use; Electron's <c>secureDnsMode</c>.</summary>
public enum SecureDnsMode
{
    /// <summary>No DoH lookups. Electron's <c>off</c>.</summary>
    Off,

    /// <summary>DoH first where available, insecure DNS as fallback. Electron's <c>automatic</c>.</summary>
    Automatic,

    /// <summary>DoH only. Electron's <c>secure</c>.</summary>
    Secure,
}

/// <summary>How host names are resolved; Electron's <c>app.configureHostResolver()</c> options. Each unset option takes
/// Electron's default, not its current value.</summary>
public sealed record HostResolverOptions
{
    /// <summary>Use Chromium's own resolver in preference to <c>getaddrinfo</c>; Chromium's <c>AsyncDns</c> default (on)
    /// when unset. Electron's <c>enableBuiltInResolver</c>.</summary>
    public bool? EnableBuiltInResolver { get; init; }

    /// <summary>Use Happy Eyeballs V3; off when unset. Electron's <c>enableHappyEyeballs</c>.</summary>
    public bool? EnableHappyEyeballs { get; init; }

    /// <summary>The DoH mode; <see cref="SecureDnsMode.Off"/> when unset, as Electron's code has it. Electron's
    /// <c>secureDnsMode</c>.</summary>
    public SecureDnsMode? SecureDnsMode { get; init; }

    /// <summary>DoH server URI templates (RFC 8484). Electron's <c>secureDnsServers</c>.</summary>
    public IReadOnlyList<string>? SecureDnsServers { get; init; }

    /// <summary>Allow HTTPS and other record types over insecure DNS; on when unset. Electron's
    /// <c>enableAdditionalDnsQueryTypes</c>.</summary>
    public bool? EnableAdditionalDnsQueryTypes { get; init; }
}

public sealed partial class OrielApp
{
    /// <summary>Configures DNS and DNS-over-HTTPS for every request. Electron's <c>app.configureHostResolver()</c>.</summary>
    /// <remarks>Applied through Chromium's local-state DNS preferences, which its stub resolver reader passes to the
    /// network service; that reader still turns DoH off under enterprise policy or Windows parental controls, and the
    /// settings persist in Local State (drift A26).</remarks>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    /// <exception cref="ArgumentException">A server is not a DoH template ("not a valid DoH template: ...").</exception>
    public void ConfigureHostResolver(HostResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsReady) throw new InvalidOperationException("configureHostResolver cannot be called before the app is ready");
        ApplyHostResolver(new CefLocalState(), options);
    }

    // Electron's ConfigureHostResolver, as the preferences Chrome's StubResolverConfigReader reads.
    internal static void ApplyHostResolver(ILocalState localState, HostResolverOptions options)
    {
        foreach (var server in options.SecureDnsServers ?? [])
            if (!IsValidDohTemplate(server)) throw new ArgumentException("not a valid DoH template: " + server, nameof(options));
        localState.Set("async_dns.enabled", options.EnableBuiltInResolver ?? true);
        localState.Set("net.happy_eyeballs_v3_enabled", options.EnableHappyEyeballs ?? false);
        localState.Set("dns_over_https.mode", (options.SecureDnsMode ?? SecureDnsMode.Off) switch
        {
            SecureDnsMode.Automatic => "automatic",
            SecureDnsMode.Secure => "secure",
            _ => "off",
        });
        localState.Set("dns_over_https.templates", string.Join(' ', options.SecureDnsServers ?? []));
        localState.Set("async_dns.additional_dns_query_types_enabled", options.EnableAdditionalDnsQueryTypes ?? true);
    }

    // net's IsValidDohTemplate: the template expands, with dns set, to an HTTPS URL whose host does not hold the query.
    internal static bool IsValidDohTemplate(string template)
    {
        const string query = "this_is_a_test_query";
        if (ExpandUriTemplate(template, "dns", query) is not { } url) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0) return false;
        return !uri.Host.Contains(query, StringComparison.Ordinal);
    }

    // RFC 6570 expansion with one defined variable, others undefined; null when the template is malformed.
    internal static string? ExpandUriTemplate(string template, string name, string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '}') return null;
            if (c != '{')
            {
                result.Append(c);
                continue;
            }
            var end = template.IndexOf('}', i + 1);
            if (end < 0) return null;
            var expression = template[(i + 1)..end];
            if (expression.Contains('{')) return null;
            i = end;
            var op = expression.Length > 0 && "+#./;?&".Contains(expression[0]) ? expression[0] : '\0';
            var names = (op == '\0' ? expression : expression[1..]).Split(',');
            if (names.Any(n => n.Length == 0)) return null;
            var defined = names.Select(n => n.Split(':')[0].TrimEnd('*')).Where(n => n == name).ToList();
            if (defined.Count == 0) continue;
            var (first, separator, named) = op switch
            {
                '+' => ("", ",", false),
                '#' => ("#", ",", false),
                '.' => (".", ".", false),
                '/' => ("/", "/", false),
                ';' => (";", ";", true),
                '?' => ("?", "&", true),
                '&' => ("&", "&", true),
                _ => ("", ",", false),
            };
            var encoded = op is '+' or '#' ? value : Uri.EscapeDataString(value);
            result.Append(first).AppendJoin(separator, defined.Select(n => named ? $"{n}={encoded}" : encoded));
        }
        return result.ToString();
    }
}
