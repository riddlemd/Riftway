using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Oriel;

/// <summary>The byte formats of the Windows clipboard and Electron's MIME names for them, as Chromium's
/// <c>ClipboardWin</c>, <c>clipboard_util_win</c> and Electron's <c>electron_api_clipboard</c> define them: pure code,
/// no Win32.</summary>
internal static class ClipboardFormats
{
    /// <summary>Electron's prefix for a raw platform format, <c>electron application/osclipboard;format="name"</c>.</summary>
    internal const string OsClipboardPrefix = "electron application/osclipboard";

    /// <summary>Electron's bookmark type.</summary>
    internal const string BookmarkMime = "electron application/bookmark";

    /// <summary>Electron's macOS find pasteboard type; a no-op write on Windows.</summary>
    internal const string FindTextMime = "electron application/findtext";

    /// <summary>The W3C custom format prefix, <c>web </c>.</summary>
    internal const string WebPrefix = "web ";

    /// <summary>Chromium's <c>WebCustomFormatMap</c> format name.</summary>
    internal const string WebCustomFormatMapName = "Web Custom Format Map";

    internal const string HtmlName = "HTML Format";
    internal const string SvgName = "image/svg+xml";
    internal const string RtfName = "Rich Text Format";
    internal const string PngName = "PNG";
    internal const string UrlName = "UniformResourceLocatorW";
    internal const string FileNameWName = "FileNameW";
    internal const string FileNameAName = "FileName";

    // Chromium's ui::kMaxRegisteredClipboardFormats, shared by web custom formats and raw writes.
    internal const int MaxRegisteredFormats = 100;

    // Registered formats Electron's IsStandardClipboardFormat skips because they are listed under a MIME already;
    // the predefined ones are CF_TEXT, CF_UNICODETEXT, CF_HDROP and CF_DIBV5.
    private static readonly string[] s_standardNames = [HtmlName, SvgName, RtfName, PngName, FileNameWName];

    private const string StartFragmentComment = "<!--StartFragment-->";
    private const string EndFragmentComment = "<!--EndFragment-->";
    private const string StartMarkup = "<html>\r\n<body>\r\n";
    private const string EndMarkup = "\r\n</body>\r\n</html>";
    private const int HeaderLength = 105;
    private const int V5HeaderSize = 124;

    /// <summary>The MIME types Chromium's <c>GetStandardFormatsInternal</c> lists, in its order, given which formats are
    /// available.</summary>
    internal static List<string> StandardTypes(bool ansiText, bool html, bool svg, bool rtf, bool dib, bool filenames)
    {
        var types = new List<string>();
        if (ansiText) types.Add("text/plain");
        if (html) types.Add("text/html");
        if (svg) types.Add("image/svg+xml");
        if (rtf) types.Add("text/rtf");
        if (dib) types.Add("image/png");
        if (filenames) types.Add("text/uri-list");
        return types;
    }

    /// <summary>What <c>clipboard.read()[0].types</c> holds on Windows: the W3C custom formats (by name), then the
    /// standard MIME types, then each other registered format as <c>electron application/osclipboard</c>, then the
    /// bookmark type.</summary>
    /// <param name="customFormats">The <c>web </c> names from the custom format map.</param>
    /// <param name="standard">From <see cref="StandardTypes"/>.</param>
    /// <param name="formats">Every format id on the clipboard with its registered name, as enumerated.</param>
    /// <param name="bookmark">Whether <c>UniformResourceLocatorW</c> holds a URL.</param>
    internal static List<string> AvailableTypes(IEnumerable<string> customFormats, IEnumerable<string> standard, IEnumerable<(uint Id, string? Name)> formats, bool bookmark)
    {
        // Chromium's custom names come out of a std::map, so in byte order.
        var types = customFormats.OrderBy(name => name, StringComparer.Ordinal).ToList();
        types.AddRange(standard);
        // GetAllAvailableFormatsInternal keeps registered formats with ASCII names, in a set ordered by id.
        foreach (var (_, name) in formats.Where(f => f.Id >= 0xC000 && !string.IsNullOrEmpty(f.Name) && f.Name.All(char.IsAscii)).OrderBy(f => f.Id))
        {
            if (Array.IndexOf(s_standardNames, name) >= 0) continue;
            var mime = name!.StartsWith(WebPrefix, StringComparison.Ordinal) ? name : WrapOsFormat(name);
            if (!types.Contains(mime)) types.Add(mime);
        }
        if (bookmark) types.Add(BookmarkMime);
        return types;
    }

    /// <summary><c>electron application/osclipboard;format="name"</c>.</summary>
    internal static string WrapOsFormat(string name) => $"{OsClipboardPrefix};format=\"{name}\"";

    /// <summary>The name inside <c>electron application/osclipboard;format="name"</c>, or null; Electron's
    /// <c>ParseOSClipboardFormat</c>.</summary>
    internal static string? ParseOsFormat(string mime)
    {
        const string Middle = ";format=\"";
        if (!mime.StartsWith(OsClipboardPrefix, StringComparison.Ordinal)) return null;
        var rest = mime.AsSpan(OsClipboardPrefix.Length);
        if (!rest.StartsWith(Middle, StringComparison.Ordinal)) return null;
        rest = rest[Middle.Length..];
        return rest.EndsWith("\"", StringComparison.Ordinal) ? rest[..^1].ToString() : null;
    }

    /// <summary>Chromium's <c>HtmlToCFHtml</c> with no source URL: the CF_HTML header of UTF-8 byte offsets and the
    /// markup between fragment comments inside a minimal document; empty for empty markup.</summary>
    internal static string BuildCfHtml(string markup)
    {
        if (markup.Length == 0) return "";
        var startFragment = HeaderLength + StartMarkup.Length + StartFragmentComment.Length;
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(markup);
        var endHtml = endFragment + EndFragmentComment.Length + EndMarkup.Length;
        return $"Version:0.9\r\nStartHTML:{HeaderLength:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n"
            + StartMarkup + StartFragmentComment + markup + EndFragmentComment + EndMarkup;
    }

    /// <summary>The fragment of a CF_HTML block as Electron's <c>getType('text/html')</c> reads it: Chromium's
    /// <c>ReadHTMLInternal</c> and <c>CFHtmlExtractMetadata</c>, then the substring between the fragment offsets; empty
    /// when the block has no usable fragment.</summary>
    internal static string HtmlFragment(byte[] block)
    {
        var cf = TrimAfterNull(block);
        const ulong Npos = ulong.MaxValue;
        var markupStart = IndexOf(Lower(cf), "<html"u8, 0);
        ulong fragmentStart = Npos, fragmentEnd = Npos;
        var tagStart = IndexOf(cf, "<!--StartFragment"u8, markupStart);
        if (tagStart == Npos)
        {
            var at = IndexOf(cf, "StartFragment:"u8, 0);
            if (at != Npos) fragmentStart = unchecked((ulong)Atoi(cf, (int)at + 14));
            at = IndexOf(cf, "EndFragment:"u8, 0);
            if (at != Npos) fragmentEnd = unchecked((ulong)Atoi(cf, (int)at + 12));
        }
        else
        {
            // npos + 1 wraps to 0 in Chromium too.
            fragmentStart = unchecked(IndexOf(cf, ">"u8, tagStart) + 1);
            var tagEnd = LastIndexOf(cf, "<!--EndFragment"u8, Npos);
            fragmentEnd = LastIndexOf(cf, "<"u8, tagEnd);
        }
        if (fragmentStart == Npos || fragmentEnd == Npos || markupStart == Npos) return "";

        var html = cf.AsSpan((int)markupStart);
        var markup = Encoding.UTF8.GetString(html);
        // A start before the markup or after the end clamps to an empty fragment as Chromium's offset adjustment does.
        var end = Math.Min(Utf16Offset(html, fragmentEnd - markupStart), (ulong)markup.Length);
        var start = Math.Min(Utf16Offset(html, fragmentStart - markupStart), end);
        return markup[(int)start..(int)end];
    }

    // base::UTF8ToUTF16AndAdjustOffsets for one offset: npos past the end or inside a character.
    private static ulong Utf16Offset(ReadOnlySpan<byte> utf8, ulong offset)
    {
        if (offset > (ulong)utf8.Length) return ulong.MaxValue;
        var at = (int)offset;
        if (at < utf8.Length && (utf8[at] & 0xC0) == 0x80) return ulong.MaxValue;
        return (ulong)Encoding.UTF8.GetCharCount(utf8[..at]);
    }

    private static int Atoi(byte[] s, int at)
    {
        while (at < s.Length && s[at] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r') at++;
        var negative = false;
        if (at < s.Length && s[at] is (byte)'+' or (byte)'-') negative = s[at++] == '-';
        long value = 0;
        while (at < s.Length && s[at] is >= (byte)'0' and <= (byte)'9' && value < int.MaxValue) value = value * 10 + (s[at++] - '0');
        return (int)Math.Clamp(negative ? -value : value, int.MinValue, int.MaxValue);
    }

    private static byte[] Lower(byte[] s)
    {
        var lower = (byte[])s.Clone();
        for (var i = 0; i < lower.Length; i++) if (lower[i] is >= (byte)'A' and <= (byte)'Z') lower[i] += 32;
        return lower;
    }

    private static ulong IndexOf(byte[] s, ReadOnlySpan<byte> what, ulong from)
    {
        if (from > (ulong)s.Length) return ulong.MaxValue;
        var i = s.AsSpan((int)from).IndexOf(what);
        return i < 0 ? ulong.MaxValue : from + (ulong)i;
    }

    // std::string::rfind: the last match starting at or before `before`.
    private static ulong LastIndexOf(byte[] s, ReadOnlySpan<byte> what, ulong before)
    {
        if (what.Length > s.Length) return ulong.MaxValue;
        var maxStart = s.Length - what.Length;
        var limit = before >= (ulong)maxStart ? maxStart : (int)before;
        var i = s.AsSpan(0, limit + what.Length).LastIndexOf(what);
        return i < 0 ? ulong.MaxValue : (ulong)i;
    }

    /// <summary>Chromium's <c>ReadRTFInternal</c>: the block up to its first NUL, converted to NFC UTF-8 from the encoding
    /// <c>base::DetectEncoding</c> finds: valid UTF-8 (ASCII included) is normalized, and anything else is read as
    /// windows-1252, the stand-in for CED's statistical guess at a legacy encoding.</summary>
    internal static byte[] RtfToUtf8(byte[] block)
    {
        var bytes = TrimAfterNull(block);
        string text;
        try { text = s_strictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(1252).GetString(bytes);
        }
        return Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC));
    }

    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The bytes before the first NUL; Chromium's <c>TrimAfterNull</c>.</summary>
    internal static byte[] TrimAfterNull(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        return end < 0 ? bytes : bytes[..end];
    }

    /// <summary>The UTF-16 text before the first NUL of a text block.</summary>
    internal static string Utf16Text(byte[] bytes)
    {
        var text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        var end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    /// <summary>skia's <c>CreateDIBV5ImageDataFromN32SkBitmap</c>: a <c>BITMAPV5HEADER</c> (BI_RGB, alpha mask, Windows
    /// color space) then the premultiplied BGRA pixels as they are, bottom row first.</summary>
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
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0x57696E20); // LCS_WINDOWS_COLOR_SPACE
        BinaryPrimitives.WriteInt32LittleEndian(header[108..], 4); // LCS_GM_IMAGES
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
            source.AsSpan((height - 1 - y) * rowBytes, rowBytes).CopyTo(dib.AsSpan(V5HeaderSize + y * rowBytes));
        return dib;
    }

    /// <summary>Chromium's fix-up after drawing a <c>CF_DIB</c>: below 32 bits, or with any channel above its alpha, the
    /// alpha bytes are meaningless and every pixel becomes opaque. The pixels are taken as premultiplied BGRA.</summary>
    internal static ImageBitmap FinishDib(int width, int height, byte[] bgra, int bitCount)
    {
        var invalid = bitCount < 32;
        for (var i = 0; !invalid && i < bgra.Length; i += 4)
            invalid = bgra[i] > bgra[i + 3] || bgra[i + 1] > bgra[i + 3] || bgra[i + 2] > bgra[i + 3];
        if (invalid)
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new ImageBitmap(width, height, bgra);
    }

    /// <summary>Chromium's <c>ParseMimeTypeWithoutParameter</c>: <c>type/subtype</c>, both tokens, leading space before
    /// the type and trailing space after the subtype allowed and dropped.</summary>
    internal static bool TryParseMimeType(string text, out string type, out string subtype)
    {
        type = subtype = "";
        var parts = text.Split('/');
        if (parts.Length != 2) return false;
        var top = parts[0].TrimStart(' ', '\t', '\n', '\v', '\f', '\r');
        var sub = parts[1].TrimEnd(' ', '\t', '\n', '\v', '\f', '\r');
        if (!IsToken(top) || !IsToken(sub)) return false;
        (type, subtype) = (top, sub);
        return true;
    }

    private static bool IsToken(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    /// <summary>The <c>Web Custom Format Map</c> JSON Chromium's <c>ScopedClipboardWriter</c> writes: keys (bare MIME
    /// types) in byte order, values the <c>Web Custom FormatN</c> names, escaped by <c>base::EscapeJSONString</c>.</summary>
    internal static string WebCustomFormatMapJson(IEnumerable<KeyValuePair<string, string>> map)
    {
        var json = new StringBuilder("{");
        foreach (var (key, value) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (json.Length > 1) json.Append(',');
            AppendJsonString(json, key).Append(':');
            AppendJsonString(json, value);
        }
        return json.Append('}').ToString();
    }

    private static StringBuilder AppendJsonString(StringBuilder json, string s)
    {
        json.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                case '\\': json.Append("\\\\"); break;
                case '"': json.Append("\\\""); break;
                case '<': json.Append("\\u003C"); break;
                case '\u2028': json.Append("\\u2028"); break;
                case '\u2029': json.Append("\\u2029"); break;
                default:
                    if (c < 32) json.Append($"\\u{(int)c:X4}");
                    else json.Append(c);
                    break;
            }
        }
        return json.Append('"');
    }

    /// <summary>Chromium's <c>OnCustomFormatDataRead</c>: each string entry of the map whose key parses as a MIME type,
    /// keyed <c>web type/subtype</c>, valued by its platform format name; empty if the JSON does not parse.</summary>
    internal static SortedDictionary<string, string> ParseWebCustomFormatMap(byte[] json)
    {
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (json.Length == 0) return names;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return names;
            var last = new Dictionary<string, string?>();
            foreach (var property in document.RootElement.EnumerateObject())
                last[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            foreach (var (key, value) in last)
                if (value is not null && TryParseMimeType(key, out var type, out var subtype)) names[$"{WebPrefix}{type}/{subtype}"] = value;
        }
        catch (JsonException) { }
        return names;
    }

    /// <summary>Chromium's <c>URIListToFileInfos</c>: the paths of the <c>file://</c> lines of an RFC 2483 list
    /// (CRLF-separated, trimmed, empty lines dropped), unescaped as UTF-8 with backslash separators.</summary>
    internal static List<string> UriListToPaths(string uriList)
    {
        var paths = new List<string>();
        foreach (var raw in uriList.Split("\r\n"))
        {
            var line = raw.Trim(' ', '\t', '\n', '\v', '\f', '\r');
            if (line.Length <= 7 || !line.StartsWith("file://", StringComparison.Ordinal)) continue;
            int start;
            if (line[7] == '/')
            {
                // file:///C:/path is the Windows path C:/path; file:///path stays /path.
                start = 7;
                var rest = line.AsSpan(8);
                if (rest.Length > 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] is '/' or '\\') start = 8;
            }
            else
            {
                start = 5; // file://host/path is //host/path.
            }
            var path = UnescapeBinary(line[start..]);
            if (path.Length > 0) paths.Add(path.Replace('/', '\\'));
        }
        return paths;
    }

    private static string UnescapeBinary(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length;)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && char.IsAsciiHexDigit((char)bytes[i + 1]) && char.IsAsciiHexDigit((char)bytes[i + 2]))
            {
                output.Add(Convert.ToByte(Encoding.ASCII.GetString(bytes, i + 1, 2), 16));
                i += 3;
            }
            else output.Add(bytes[i++]);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Chromium's <c>FileInfosToURIList</c> on Windows: a <c>file:</c> URL per path, joined by CRLF. <c>%;#?</c>,
    /// space, controls and every non-ASCII UTF-8 byte (a negative <c>char</c>) are percent-encoded; backslashes become
    /// slashes.</summary>
    internal static string PathsToUriList(IEnumerable<string> paths) => string.Join("\r\n", paths.Select(FilePathToFileUrl));

    private static string FilePathToFileUrl(string path)
    {
        var url = new StringBuilder("file:");
        static bool Separator(char c) => c is '\\' or '/';
        if (path.Length > 1 && Separator(path[0]) && Separator(path[1])) { }
        else if (path.Length > 0 && Separator(path[0])) url.Append("//");
        else url.Append("///");
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            if (b is (byte)'%' or (byte)';' or (byte)'#' or (byte)'?' or <= (byte)' ' or >= 0x80) url.Append('%').Append(b.ToString("X2"));
            else if (b == '\\') url.Append('/');
            else url.Append((char)b);
        }
        return url.ToString();
    }

    /// <summary>Chromium's <c>CreateStorageForFileNames</c>: a 20-byte <c>DROPFILES</c> (wide) then each path and its NUL,
    /// then a final NUL; null for no paths.</summary>
    internal static byte[]? BuildDropFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var names = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + "\0")) + "\0");
        var block = new byte[20 + names.Length];
        BinaryPrimitives.WriteInt32LittleEndian(block, 20);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(16), 1);
        names.CopyTo(block, 20);
        return block;
    }
}
