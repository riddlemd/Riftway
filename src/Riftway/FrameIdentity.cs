using System.Globalization;

namespace Riftway;

/// <summary>One frame as CEF lists it in the browser process.</summary>
internal readonly record struct CefFrameInfo(string Id, string? ParentId, string Name, string Url, bool IsMain);

/// <summary>Chromium's frame ids as CEF and the process table carry them.</summary>
internal static class FrameIdentity
{
    private const string GeneratedPrefix = "<!--";
    private const string UnnamedPrefix = "<!--framePath ";
    private const string PositionMarker = "<!--frame";

    /// <summary>The render process id and frame token in CEF's frame identifier, or null when it is not one.</summary>
    /// <remarks>CEF writes <c>GlobalRenderFrameHostToken</c> as the child process id in hex (<c>A-</c> is process 10),
    /// a dash, then the <c>LocalFrameToken</c> as 32 upper-case hex digits.</remarks>
    public static (int ProcessId, string FrameToken)? Parse(string? identifier)
    {
        if (identifier is null) return null;
        var dash = identifier.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0) return null;
        var token = identifier[(dash + 1)..];
        if (token.Length != 32 || !token.All(IsUpperHex)) return null;
        if (!int.TryParse(identifier.AsSpan(0, dash), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var process) || process < 0) return null;
        return (process, token);
    }

    private static bool IsUpperHex(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'F';

    /// <summary>The <c>--renderer-client-id</c> a renderer was launched with: its render process id. Null for any other process.</summary>
    public static int? RendererClientId(IReadOnlyList<string> arguments)
    {
        if (!arguments.Contains("--type=renderer")) return null;
        const string Switch = "--renderer-client-id=";
        var value = arguments.FirstOrDefault(a => a.StartsWith(Switch, StringComparison.Ordinal))?[Switch.Length..];
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>Which of a CEF frame's children is the DevTools frame named <paramref name="name"/> at
    /// <paramref name="url"/>, the <paramref name="lookAlikeIndex"/>-th such sibling to be created; null when none fits.</summary>
    public static string? MatchChild(IReadOnlyList<CefFrameInfo> children, string name, string url, int lookAlikeIndex)
    {
        // CEF names an unnamed frame by Blink's unique name: "<!--framePath //<!--frameN-->-->" for one the parser made, N
        // being its creation slot, or "<!--dynamicFrame...-->" with a random part for one a script made.
        var lookAlikes = children
            .Where(c => c.Url == url && (name.Length > 0 ? c.Name == name : c.Name.Length == 0 || c.Name.StartsWith(GeneratedPrefix, StringComparison.Ordinal)))
            .Select((c, order) => (c.Id, Slot: UnnamedSlot(c.Name) ?? int.MaxValue, order))
            .OrderBy(c => c.Slot).ThenBy(c => c.order)
            .ToList();
        return lookAlikeIndex >= 0 && lookAlikeIndex < lookAlikes.Count ? lookAlikes[lookAlikeIndex].Id : null;
    }

    /// <summary>Which of a CEF frame's children is a frame DevTools has not seen commit: the
    /// <paramref name="unseenIndex"/>-th to be created of those in another process than <paramref name="parentProcessId"/>
    /// that no seen sibling took.</summary>
    /// <remarks>DevTools reports neither URL nor name for a frame in another process; one in the page's own process that it
    /// has not seen has not committed yet, and has no ids to match.</remarks>
    public static string? MatchUnseen(IReadOnlyList<CefFrameInfo> children, IReadOnlySet<string> taken, int parentProcessId, int unseenIndex)
    {
        var left = children.Where(c => !taken.Contains(c.Id) && Parse(c.Id) is { } ids && ids.ProcessId != parentProcessId)
            .Select((c, order) => (c.Id, Slot: UnnamedSlot(c.Name) ?? int.MaxValue, order))
            .OrderBy(c => c.Slot).ThenBy(c => c.order)
            .ToList();
        return unseenIndex >= 0 && unseenIndex < left.Count ? left[unseenIndex].Id : null;
    }

    /// <summary>N in a unique name ending <c>&lt;!--frameN--&gt;--&gt;</c>, or null.</summary>
    public static int? UnnamedSlot(string uniqueName)
    {
        if (!uniqueName.StartsWith(UnnamedPrefix, StringComparison.Ordinal)) return null;
        var at = uniqueName.LastIndexOf(PositionMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        var digits = uniqueName[(at + PositionMarker.Length)..].TakeWhile(char.IsAsciiDigit).ToArray();
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) ? slot : null;
    }
}
