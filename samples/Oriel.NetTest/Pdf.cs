using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Just enough PDF reading to check Chromium's output: pages, media boxes, and the text drawn
/// (through the fonts' ToUnicode maps).</summary>
sealed partial class Pdf
{
    private readonly string _raw;
    private readonly List<string> _streams = [];
    // Object number to its dictionary text, and to its decoded stream, including objects packed in object streams.
    private readonly Dictionary<int, string> _objects = [];
    private readonly Dictionary<int, string> _streamOf = [];

    public Pdf(byte[] bytes)
    {
        Bytes = bytes;
        _raw = Encoding.Latin1.GetString(bytes);
        var headers = ObjHeaderRegex().Matches(_raw).ToList();
        for (var i = 0; i < headers.Count; i++)
        {
            var bodyStart = headers[i].Index + headers[i].Length;
            var bodyEnd = _raw.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
            if (bodyEnd < 0) continue;
            var body = _raw[bodyStart..bodyEnd];
            var streamAt = body.IndexOf("stream", StringComparison.Ordinal);
            _objects[int.Parse(headers[i].Groups[1].Value, CultureInfo.InvariantCulture)] = streamAt >= 0 ? body[..streamAt] : body;
        }
        foreach (Match m in StreamRegex().Matches(_raw))
        {
            var start = m.Index + m.Length;
            var end = _raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            var data = bytes.AsSpan(start, end - start).ToArray();
            var dictStart = _raw.LastIndexOf("<<", m.Index, StringComparison.Ordinal);
            var dict = dictStart >= 0 ? _raw[dictStart..m.Index] : "";
            var decoded = dict.Contains("/FlateDecode", StringComparison.Ordinal) ? Inflate(data) : Encoding.Latin1.GetString(data);
            _streams.Add(decoded);
            var owner = headers.LastOrDefault(h => h.Index < m.Index);
            if (owner is null) continue;
            var number = int.Parse(owner.Groups[1].Value, CultureInfo.InvariantCulture);
            _streamOf[number] = decoded;
            if (ObjStmRegex().IsMatch(dict)) Unpack(dict, decoded);
        }
    }

    // An object stream holds "number offset" pairs, then the objects themselves from /First on.
    private void Unpack(string dict, string decoded)
    {
        var first = FirstRegex().Match(dict);
        if (!first.Success) return;
        var at = int.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture);
        if (at > decoded.Length) return;
        var numbers = decoded[..at].Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<(int Number, int Offset)>();
        for (var i = 0; i + 1 < numbers.Length; i += 2)
            entries.Add((int.Parse(numbers[i], CultureInfo.InvariantCulture), int.Parse(numbers[i + 1], CultureInfo.InvariantCulture)));
        for (var i = 0; i < entries.Count; i++)
        {
            var from = at + entries[i].Offset;
            var to = i + 1 < entries.Count ? at + entries[i + 1].Offset : decoded.Length;
            if (from <= to && to <= decoded.Length) _objects.TryAdd(entries[i].Number, decoded[from..to]);
        }
    }

    public byte[] Bytes { get; }

    public bool IsPdf => _raw.StartsWith("%PDF-", StringComparison.Ordinal);

    /// <summary>Page objects, in the file and in any decompressed object streams.</summary>
    public int PageCount => PageRegex().Matches(_raw).Count + _streams.Sum(s => PageRegex().Matches(s).Count);

    /// <summary>Every page's media box width and height, in points.</summary>
    public List<(double Width, double Height)> MediaBoxes
        => [.. MediaBoxRegex().Matches(_raw).Concat(_streams.SelectMany(s => MediaBoxRegex().Matches(s))).Select(m =>
            (D(m.Groups[3].Value) - D(m.Groups[1].Value), D(m.Groups[4].Value) - D(m.Groups[2].Value)))];

    /// <summary>Every page's content text, each run decoded with the ToUnicode map of the font its <c>Tf</c> selects.</summary>
    /// <remarks>Fonts number their glyphs independently, so one merged map garbles a page that uses two fonts.</remarks>
    public string Text()
    {
        var text = new StringBuilder();
        foreach (var (_, dict) in _objects.Where(o => PageRegex().IsMatch(o.Value)).OrderBy(o => o.Key))
        {
            var resources = Resources(dict);
            foreach (var content in ContentsOf(dict))
            {
                Walk(content, resources, text, 0);
                text.Append('\n');
            }
        }
        return text.Length > 0 ? text.ToString() : MergedText();
    }

    // One content stream in order: Tf picks the font, hex strings are runs, Do draws a form XObject, whose content is
    // walked with its own resources (Linux Chromium draws header and footer templates this way).
    private void Walk(string content, string? resources, StringBuilder text, int depth)
    {
        var fonts = Fonts(resources);
        var font = (Map: new Dictionary<int, string>(), Bytes: 2);
        foreach (Match token in ContentTokenRegex().Matches(content))
        {
            if (token.Groups[1].Success) font = fonts.GetValueOrDefault(token.Groups[1].Value, (new Dictionary<int, string>(), 2));
            else if (token.Groups[2].Success) AppendRun(text, token.Groups[2].Value, font.Map, font.Bytes);
            else if (depth < 8 && FormXObject(resources, token.Groups[3].Value) is { } form)
                Walk(form.Content, DictValue(form.Dict, "/Resources") ?? resources, text, depth + 1);
        }
    }

    private (string Dict, string Content)? FormXObject(string? resources, string name)
    {
        var xobjects = resources is null ? null : DictValue(resources, "/XObject");
        if (xobjects is null) return null;
        var entry = NamedRefRegex().Matches(xobjects).FirstOrDefault(m => m.Groups[1].Value == name);
        if (entry is null) return null;
        var number = int.Parse(entry.Groups[2].Value, CultureInfo.InvariantCulture);
        return _objects.TryGetValue(number, out var dict) && FormRegex().IsMatch(dict) && _streamOf.TryGetValue(number, out var content)
            ? (dict, content) : null;
    }

    private static void AppendRun(StringBuilder text, string digits, Dictionary<int, string> map, int bytes)
    {
        var width = bytes * 2;
        for (var i = 0; i + width <= digits.Length; i += width)
            text.Append(map.GetValueOrDefault(Hex(digits.Substring(i, width)), ""));
    }

    // The resource name of each font in a resource dictionary, mapped to its ToUnicode CMap and code length in bytes.
    private Dictionary<string, (Dictionary<int, string> Map, int Bytes)> Fonts(string? resources)
    {
        var fonts = new Dictionary<string, (Dictionary<int, string> Map, int Bytes)>();
        if (resources is null) return fonts;
        var fontDict = DictValue(resources, "/Font");
        if (fontDict is null) return fonts;
        foreach (Match entry in NamedRefRegex().Matches(fontDict))
        {
            var font = _objects.GetValueOrDefault(int.Parse(entry.Groups[2].Value, CultureInfo.InvariantCulture));
            var toUnicode = font is null ? null : ToUnicodeRegex().Match(font);
            if (toUnicode is { Success: true } && _streamOf.TryGetValue(int.Parse(toUnicode.Groups[1].Value, CultureInfo.InvariantCulture), out var cmap))
                fonts[entry.Groups[1].Value] = (ParseCMap(cmap), CodeBytes(cmap, font!));
        }
        return fonts;
    }

    // A page's resources: inline, by reference, or inherited from its parents.
    private string? Resources(string node)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            if (DictValue(node, "/Resources") is { } resources) return resources;
            var parent = ParentRegex().Match(node);
            if (!parent.Success || !_objects.TryGetValue(int.Parse(parent.Groups[1].Value, CultureInfo.InvariantCulture), out var next)) return null;
            node = next;
        }
        return null;
    }

    // The value of a dictionary key that is either an inline dictionary or a reference to one.
    private string? DictValue(string dict, string key)
    {
        var at = dict.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = dict[(at + key.Length)..].TrimStart();
        if (rest.StartsWith("<<", StringComparison.Ordinal)) return Balanced(rest);
        var reference = LeadingRefRegex().Match(rest);
        return reference.Success ? _objects.GetValueOrDefault(int.Parse(reference.Groups[1].Value, CultureInfo.InvariantCulture)) : null;
    }

    private static string Balanced(string text)
    {
        var depth = 0;
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '<' && text[i + 1] == '<') { depth++; i++; }
            else if (text[i] == '>' && text[i + 1] == '>') { depth--; i++; if (depth == 0) return text[..(i + 1)]; }
        }
        return text;
    }

    private IEnumerable<string> ContentsOf(string page)
    {
        var at = page.IndexOf("/Contents", StringComparison.Ordinal);
        if (at < 0) yield break;
        var rest = page[(at + "/Contents".Length)..].TrimStart();
        var refs = rest.StartsWith('[') ? rest[..(rest.IndexOf(']') + 1)] : LeadingRefRegex().Match(rest).Value;
        foreach (Match r in RefRegex().Matches(refs))
            if (_streamOf.TryGetValue(int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture), out var content)) yield return content;
    }

    // The CMap's codespace range gives the code length; without one, Type0 fonts use 2-byte codes and simple fonts
    // (Type1, TrueType, Type3: Linux Chromium's header and footer) use 1.
    private static int CodeBytes(string cmap, string font)
    {
        var range = CodespaceRegex().Match(cmap);
        if (range.Success) return Math.Max(1, range.Groups[1].Value.Length / 2);
        return Type0Regex().IsMatch(font) ? 2 : 1;
    }

    private static Dictionary<int, string> ParseCMap(string cmap)
    {
        var map = new Dictionary<int, string>();
        foreach (Match block in BfCharBlockRegex().Matches(cmap))
            foreach (Match pair in HexPairRegex().Matches(block.Groups[1].Value))
                map[Hex(pair.Groups[1].Value)] = Utf16(pair.Groups[2].Value);
        foreach (Match block in BfRangeBlockRegex().Matches(cmap))
        {
            foreach (Match range in HexRangeRegex().Matches(block.Groups[1].Value))
            {
                int lo = Hex(range.Groups[1].Value), hi = Hex(range.Groups[2].Value), dst = Hex(range.Groups[3].Value);
                for (var c = lo; c <= hi; c++) map[c] = char.ConvertFromUtf32(dst + (c - lo));
            }
        }
        return map;
    }

    // Fallback when no page objects resolve: every run through the union of all CMaps.
    private string MergedText()
    {
        var map = new Dictionary<int, string>();
        foreach (var cmap in _streams.Where(s => s.Contains("begincmap", StringComparison.Ordinal)))
            foreach (var (code, value) in ParseCMap(cmap)) map[code] = value;
        var text = new StringBuilder();
        foreach (var content in _streams.Where(s => s.Contains("Tj", StringComparison.Ordinal) || s.Contains("TJ", StringComparison.Ordinal)))
        {
            foreach (Match hex in TextHexRegex().Matches(content)) AppendRun(text, hex.Groups[1].Value, map, 2);
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Whether any content stream sets this fill colour, e.g. <c>1 0 0 rg</c>.</summary>
    public bool HasFill(string operands) => _streams.Any(s => s.Contains(operands + " rg", StringComparison.Ordinal));

    private static string Inflate(byte[] data)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return Encoding.Latin1.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            return "";
        }
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string Utf16(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        return Encoding.BigEndianUnicode.GetString(bytes);
    }

    // Not the "stream" inside "endstream", which would remap the stream before it to the bytes after it.
    [GeneratedRegex(@"(?<!end)stream\r?\n")]
    private static partial Regex StreamRegex();

    [GeneratedRegex(@"/Type\s*/Page(?![s\w])")]
    private static partial Regex PageRegex();

    [GeneratedRegex(@"/MediaBox\s*\[\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\]")]
    private static partial Regex MediaBoxRegex();

    [GeneratedRegex(@"beginbfchar(.*?)endbfchar", RegexOptions.Singleline)]
    private static partial Regex BfCharBlockRegex();

    [GeneratedRegex(@"beginbfrange(.*?)endbfrange", RegexOptions.Singleline)]
    private static partial Regex BfRangeBlockRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>")]
    private static partial Regex HexPairRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>")]
    private static partial Regex HexRangeRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>")]
    private static partial Regex TextHexRegex();

    [GeneratedRegex(@"(\d+)\s+\d+\s+obj\b")]
    private static partial Regex ObjHeaderRegex();

    [GeneratedRegex(@"/Type\s*/ObjStm\b")]
    private static partial Regex ObjStmRegex();

    [GeneratedRegex(@"/First\s+(\d+)")]
    private static partial Regex FirstRegex();

    [GeneratedRegex(@"/([^\s/<>\[\]()]+)\s+(\d+)\s+\d+\s+R")]
    private static partial Regex NamedRefRegex();

    [GeneratedRegex(@"/ToUnicode\s+(\d+)\s+\d+\s+R")]
    private static partial Regex ToUnicodeRegex();

    [GeneratedRegex(@"/Parent\s+(\d+)\s+\d+\s+R")]
    private static partial Regex ParentRegex();

    [GeneratedRegex(@"^(\d+)\s+\d+\s+R")]
    private static partial Regex LeadingRefRegex();

    [GeneratedRegex(@"(\d+)\s+\d+\s+R")]
    private static partial Regex RefRegex();

    // A font selection ("/F1 12 Tf"), a hex string run, or an XObject drawn ("/X1 Do"), in content order.
    [GeneratedRegex(@"/([^\s/<>\[\]()]+)\s+[-\d.]+\s+Tf|<([0-9A-Fa-f]+)>|/([^\s/<>\[\]()]+)\s+Do\b")]
    private static partial Regex ContentTokenRegex();

    [GeneratedRegex(@"begincodespacerange\s*<([0-9A-Fa-f]+)>")]
    private static partial Regex CodespaceRegex();

    [GeneratedRegex(@"/Subtype\s*/Type0\b")]
    private static partial Regex Type0Regex();

    [GeneratedRegex(@"/Subtype\s*/Form\b")]
    private static partial Regex FormRegex();
}
