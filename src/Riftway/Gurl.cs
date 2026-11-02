using System.Text;
using Riftway.Cef;

namespace Riftway;

/// <summary>Chromium's <c>GURL(string).spec()</c>, for the places Electron turns a JS string into a GURL
/// (<c>shell.openExternal</c>, the clipboard bookmark): the canonical URL, or "" when GURL calls it invalid.</summary>
/// <remarks>Once CEF is initialized this is GURL itself, through <see cref="CefUrl"/>. Before that (or in unit tests)
/// <see cref="Approximate"/> stands in: special schemes go through <see cref="Uri"/>, other schemes keep their text
/// after the lower-cased scheme with non-ASCII and control characters percent-encoded as UTF-8.</remarks>
internal static class Gurl
{
    private static readonly string[] s_special = ["http", "https", "ws", "wss", "ftp", "file"];

    /// <summary>The canonical spec of <paramref name="text"/>, or "" if it is not a valid URL.</summary>
    internal static string Spec(string text) => RiftwayApp.CurrentOrNull is { IsReady: true } ? CefUrl.Spec(text) : Approximate(text);

    /// <summary>The stand-in for GURL before CEF is initialized.</summary>
    internal static string Approximate(string text)
    {
        // GURL trims C0 controls and spaces at both ends and drops tabs and newlines anywhere.
        var trimmed = text.Trim(Enumerable.Range(0, 33).Select(c => (char)c).ToArray()).Replace("\t", "").Replace("\n", "").Replace("\r", "");
        var colon = trimmed.IndexOf(':');
        if (colon < 0 || !char.IsAsciiLetter(trimmed[0]) || !trimmed.AsSpan(1, colon - 1).ToString().All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
            return "";
        var scheme = trimmed[..colon].ToLowerInvariant();
        if (Array.IndexOf(s_special, scheme) >= 0)
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : "";
        var spec = new StringBuilder(scheme).Append(':');
        foreach (var b in Encoding.UTF8.GetBytes(trimmed[(colon + 1)..]))
        {
            if (b < 0x20 || b >= 0x7F) spec.Append('%').Append(b.ToString("X2"));
            else spec.Append((char)b);
        }
        return spec.ToString();
    }
}
