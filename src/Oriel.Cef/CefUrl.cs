using Oriel.Cef.Interop;

namespace Oriel.Cef;

/// <summary>Chromium's URL canonicalizer through CEF: <c>cef_parse_url</c> builds a <c>GURL</c>.</summary>
public static unsafe class CefUrl
{
    /// <summary><c>GURL(url).spec()</c>: the canonical URL, or "" when GURL calls it invalid.</summary>
    /// <remarks>Call only once CEF is initialized: GURL's IDN host handling needs the ICU data CEF loads.</remarks>
    public static string Spec(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        cef_string_utf16_t input = default;
        var parts = new cef_urlparts_t { size = (nuint)sizeof(cef_urlparts_t) };
        CefString.Set(&input, url);
        try
        {
            return CefNative.cef_parse_url(&input, &parts) != 0 ? CefString.Read(&parts.spec) : "";
        }
        finally
        {
            CefString.Clear(&input);
            CefString.Clear(&parts.spec);
            CefString.Clear(&parts.scheme);
            CefString.Clear(&parts.username);
            CefString.Clear(&parts.password);
            CefString.Clear(&parts.host);
            CefString.Clear(&parts.port);
            CefString.Clear(&parts.origin);
            CefString.Clear(&parts.path);
            CefString.Clear(&parts.query);
            CefString.Clear(&parts.fragment);
        }
    }
}
