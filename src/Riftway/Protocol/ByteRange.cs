using System.Globalization;

namespace Riftway;

/// <summary>A single HTTP byte range resolved against a body of known length.</summary>
internal readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    /// <summary>Resolves a <c>Range</c> header against <paramref name="totalLength"/>; null when the header
    /// is absent, multi-range (a comma leaves a bound unparsable) or unsatisfiable, in which case the
    /// whole body is sent.</summary>
    /// <remarks>Mirrors what CEF's loader accepts: it already rejects malformed ranges before the handler
    /// runs and sends the full body for multi-range requests.</remarks>
    public static ByteRange? Resolve(string? header, long totalLength)
    {
        if (string.IsNullOrWhiteSpace(header) || totalLength <= 0) return null;

        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        value = value["bytes=".Length..];

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0) return null;
        var first = value[..dash].Trim();
        var last = value[(dash + 1)..].Trim();

        // "bytes=-N": the final N bytes.
        if (first.Length == 0)
        {
            if (!TryParse(last, out var suffix) || suffix == 0) return null;
            return new ByteRange(Math.Max(0, totalLength - suffix), totalLength - 1);
        }

        if (!TryParse(first, out var start) || start >= totalLength) return null;
        if (last.Length == 0) return new ByteRange(start, totalLength - 1);
        if (!TryParse(last, out var end) || end < start) return null;
        return new ByteRange(start, Math.Min(end, totalLength - 1));
    }

    private static bool TryParse(string text, out long value)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
