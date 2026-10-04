using System.Buffers.Binary;
using System.Text;

namespace Oriel;

/// <summary>The byte formats of the Windows clipboard, and how they map to MIME types: pure code, no Win32.</summary>
internal static class ClipboardFormats
{
    private const string FragmentStart = "<!--StartFragment-->";
    private const string FragmentEnd = "<!--EndFragment-->";
    private const string HtmlPrefix = "<html><body>" + FragmentStart;
    private const string HtmlSuffix = FragmentEnd + "</body></html>";

    private const int BiRgb = 0;
    private const int BiBitfields = 3;
    private const int InfoHeaderSize = 40;
    private const int V4HeaderSize = 108;
    private const int V5HeaderSize = 124;
    private const uint RedMask = 0x00FF0000;
    private const uint GreenMask = 0x0000FF00;
    private const uint BlueMask = 0x000000FF;
    private const uint AlphaMask = 0xFF000000;

    // One row per MIME type, in the order AvailableMimes lists them; Windows names are tried in order.
    private static readonly (string Mime, string[] Names)[] s_table =
    [
        ("text/plain", ["CF_UNICODETEXT", "CF_TEXT"]),
        ("text/html", ["HTML Format"]),
        ("text/rtf", ["Rich Text Format"]),
        ("image/png", ["PNG", "CF_DIBV5", "CF_DIB"]),
    ];

    /// <summary>The MIME type for a Windows format name, or null if it has none. Electron's <c>clipboard.has()</c> and
    /// <c>availableFormats()</c> speak MIME.</summary>
    /// <remarks>
    /// <c>CF_UNICODETEXT</c> and <c>CF_TEXT</c> are <c>text/plain</c>; <c>HTML Format</c> is <c>text/html</c>;
    /// <c>Rich Text Format</c> is <c>text/rtf</c>; <c>PNG</c>, <c>CF_DIBV5</c> and <c>CF_DIB</c> are <c>image/png</c>.
    /// Predefined formats are named by their <c>CF_</c> constant, which GetClipboardFormatName cannot return.
    /// Bookmarks (<c>UniformResourceLocatorW</c>) and custom formats have no MIME type.
    /// </remarks>
    internal static string? MimeOf(string windowsFormatName)
    {
        foreach (var (mime, names) in s_table)
            if (Array.IndexOf(names, windowsFormatName) >= 0) return mime;
        return null;
    }

    /// <summary>The MIME type for a format id, taking <paramref name="registeredName"/> for ids above the predefined
    /// ones; null if it has none.</summary>
    internal static string? MimeOf(uint formatId, string? registeredName) => formatId switch
    {
        1 => "text/plain",
        13 => "text/plain",
        8 => "image/png",
        17 => "image/png",
        _ => registeredName is null ? null : MimeOf(registeredName),
    };

    /// <summary>The Windows format names that hold <paramref name="mime"/>, best first; empty for an unknown type.</summary>
    internal static IReadOnlyList<string> WindowsNamesOf(string mime)
    {
        foreach (var (candidate, names) in s_table)
            if (candidate == mime) return names;
        return [];
    }

    /// <summary>The id of a predefined format given its <c>CF_</c> name, or null for any other name.</summary>
    internal static uint? PredefinedId(string name) => name switch
    {
        "CF_TEXT" => 1,
        "CF_DIB" => 8,
        "CF_UNICODETEXT" => 13,
        "CF_DIBV5" => 17,
        _ => null,
    };

    /// <summary>The MIME types among <paramref name="formats"/>, each once, in a fixed order: text, HTML, RTF, image.</summary>
    internal static IReadOnlyList<string> AvailableMimes(IEnumerable<(uint Id, string? Name)> formats)
    {
        var present = new HashSet<string>();
        foreach (var (id, name) in formats)
            if (MimeOf(id, name) is { } mime) present.Add(mime);
        return [.. s_table.Select(row => row.Mime).Where(present.Contains)];
    }

    /// <summary>The Windows "HTML Format" (<c>CF_HTML</c>) for <paramref name="fragment"/>: a header of UTF-8 byte offsets
    /// and the fragment inside a minimal document.</summary>
    internal static string BuildCfHtml(string fragment)
    {
        var fragmentBytes = Encoding.UTF8.GetByteCount(fragment);
        var headerLength = Header(0, 0, 0, 0).Length;
        var startHtml = headerLength;
        var startFragment = startHtml + HtmlPrefix.Length;
        var endFragment = startFragment + fragmentBytes;
        var endHtml = endFragment + HtmlSuffix.Length;
        return Header(startHtml, endHtml, startFragment, endFragment) + HtmlPrefix + fragment + HtmlSuffix;
    }

    private static string Header(int startHtml, int endHtml, int startFragment, int endFragment)
        => $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n";

    /// <summary>The fragment of a <c>CF_HTML</c> string: by the header's byte offsets into its UTF-8 form, else by the
    /// fragment comments, else the whole HTML range; null if it is not <c>CF_HTML</c> or holds none of those.</summary>
    internal static string? FragmentFromCfHtml(string cfHtml)
    {
        if (!cfHtml.StartsWith("Version:", StringComparison.Ordinal)) return null;
        var bodyStart = cfHtml.IndexOf('<');
        var header = ParseHeader(bodyStart < 0 ? cfHtml : cfHtml[..bodyStart]);
        var bytes = Encoding.UTF8.GetBytes(cfHtml);

        if (header.TryGetValue("StartFragment", out var start) && header.TryGetValue("EndFragment", out var end) && InRange(bytes, start, end))
            return Encoding.UTF8.GetString(bytes, start, end - start);

        var from = cfHtml.IndexOf(FragmentStart, StringComparison.Ordinal);
        var to = cfHtml.IndexOf(FragmentEnd, StringComparison.Ordinal);
        if (from >= 0 && to >= from + FragmentStart.Length) return cfHtml[(from + FragmentStart.Length)..to];

        if (header.TryGetValue("StartHTML", out var htmlStart) && header.TryGetValue("EndHTML", out var htmlEnd) && InRange(bytes, htmlStart, htmlEnd))
            return Encoding.UTF8.GetString(bytes, htmlStart, htmlEnd - htmlStart);
        return null;
    }

    private static bool InRange(byte[] bytes, int start, int end) => start >= 0 && start <= end && end <= bytes.Length;

    private static Dictionary<string, int> ParseHeader(string header)
    {
        var values = new Dictionary<string, int>();
        foreach (var line in header.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && int.TryParse(line.AsSpan(colon + 1), out var value)) values[line[..colon]] = value;
        }
        return values;
    }

    /// <summary>A <c>CF_DIBV5</c> block for <paramref name="premultiplied"/>: a <c>BITMAPV5HEADER</c> then 32-bit
    /// straight-alpha pixels, bottom row first, as Windows apps expect.</summary>
    internal static byte[] BuildDibV5(ImageBitmap premultiplied)
    {
        var (width, height, source) = premultiplied;
        var dib = new byte[V5HeaderSize + width * height * 4];
        var header = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(header, V5HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], BiBitfields);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], width * height * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], RedMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], GreenMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], BlueMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], AlphaMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0x73524742); // LCS_sRGB
        BinaryPrimitives.WriteInt32LittleEndian(header[108..], 4); // LCS_GM_IMAGES

        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            var from = source.AsSpan((height - 1 - y) * rowBytes, rowBytes);
            var to = dib.AsSpan(V5HeaderSize + y * rowBytes, rowBytes);
            for (var x = 0; x < rowBytes; x += 4)
            {
                var alpha = from[x + 3];
                to[x] = Unpremultiply(from[x], alpha);
                to[x + 1] = Unpremultiply(from[x + 1], alpha);
                to[x + 2] = Unpremultiply(from[x + 2], alpha);
                to[x + 3] = alpha;
            }
        }
        return dib;
    }

    /// <summary>The pixels of a <c>CF_DIB</c> or <c>CF_DIBV5</c> block as premultiplied BGRA, top row first; null for
    /// anything but uncompressed 24 or 32 bits per pixel without a palette.</summary>
    /// <remarks>BI_RGB 32-bit pixels whose alpha bytes are all zero are taken as opaque, as most writers leave them zero.</remarks>
    internal static ImageBitmap? ParseDib(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < InfoHeaderSize) return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize is not (InfoHeaderSize or V4HeaderSize or V5HeaderSize) || dib.Length < headerSize) return null;

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bits = BinaryPrimitives.ReadInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        var paletteColors = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue || paletteColors != 0) return null;
        if (bits is not (24 or 32) || compression is not (BiRgb or BiBitfields) || (compression == BiBitfields && bits != 32)) return null;

        var pixelsAt = headerSize;
        var alphaMasked = false;
        var alphaDeclared = false;
        if (compression == BiBitfields)
        {
            var hasAlphaMask = headerSize != InfoHeaderSize;
            // An INFO header carries its three masks after it; V4 and V5 carry four inside.
            if (!hasAlphaMask) pixelsAt += 12;
            if (dib.Length < pixelsAt) return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]) != RedMask
                || BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]) != GreenMask
                || BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]) != BlueMask) return null;
            if (hasAlphaMask)
            {
                var alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
                if (alphaMask is not (AlphaMask or 0)) return null;
                alphaDeclared = true;
                alphaMasked = alphaMask == AlphaMask;
            }
        }

        var height = Math.Abs(signedHeight);
        var stride = ((long)width * bits + 31) / 32 * 4;
        if (pixelsAt + stride * height > dib.Length) return null;

        var bgra = new byte[checked(width * height * 4)];
        var bytesPerPixel = bits / 8;
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = signedHeight > 0 ? height - 1 - y : y;
            var from = dib[(int)(pixelsAt + sourceRow * stride)..];
            var to = bgra.AsSpan(y * width * 4);
            for (var x = 0; x < width; x++)
            {
                to[x * 4] = from[x * bytesPerPixel];
                to[x * 4 + 1] = from[x * bytesPerPixel + 1];
                to[x * 4 + 2] = from[x * bytesPerPixel + 2];
                var alpha = bytesPerPixel == 4 ? from[x * 4 + 3] : (byte)255;
                to[x * 4 + 3] = alpha;
                anyAlpha |= alpha != 0;
            }
        }

        // A V4/V5 header declares whether alpha is used; every other 32-bit layout may have left it at zero.
        var opaque = alphaDeclared ? !alphaMasked : !anyAlpha;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (opaque) bgra[i + 3] = 255;
            var alpha = bgra[i + 3];
            bgra[i] = Premultiply(bgra[i], alpha);
            bgra[i + 1] = Premultiply(bgra[i + 1], alpha);
            bgra[i + 2] = Premultiply(bgra[i + 2], alpha);
        }
        return new ImageBitmap(width, height, bgra);
    }

    /// <summary>A channel scaled by <paramref name="alpha"/>, rounded.</summary>
    internal static byte Premultiply(byte channel, byte alpha) => (byte)((channel * alpha + 127) / 255);

    /// <summary>A premultiplied channel scaled back up, rounded and capped at 255; 0 when <paramref name="alpha"/> is.</summary>
    internal static byte Unpremultiply(byte channel, byte alpha)
        => alpha == 0 ? (byte)0 : (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);
}
