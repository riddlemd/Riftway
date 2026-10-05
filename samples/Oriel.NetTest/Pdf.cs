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

    public Pdf(byte[] bytes)
    {
        Bytes = bytes;
        _raw = Encoding.Latin1.GetString(bytes);
        foreach (Match m in StreamRegex().Matches(_raw))
        {
            var start = m.Index + m.Length;
            var end = _raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            var data = bytes.AsSpan(start, end - start).ToArray();
            var dictStart = _raw.LastIndexOf("<<", m.Index, StringComparison.Ordinal);
            var dict = dictStart >= 0 ? _raw[dictStart..m.Index] : "";
            _streams.Add(dict.Contains("/FlateDecode", StringComparison.Ordinal) ? Inflate(data) : Encoding.Latin1.GetString(data));
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

    /// <summary>All content-stream text decoded with the union of the fonts' ToUnicode maps.</summary>
    public string Text()
    {
        var map = new Dictionary<int, string>();
        foreach (var cmap in _streams.Where(s => s.Contains("begincmap", StringComparison.Ordinal)))
        {
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
        }
        var text = new StringBuilder();
        foreach (var content in _streams.Where(s => s.Contains("Tj", StringComparison.Ordinal) || s.Contains("TJ", StringComparison.Ordinal)))
        {
            foreach (Match hex in TextHexRegex().Matches(content))
            {
                var digits = hex.Groups[1].Value;
                for (var i = 0; i + 4 <= digits.Length; i += 4)
                    text.Append(map.GetValueOrDefault(Hex(digits.Substring(i, 4)), ""));
            }
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

    [GeneratedRegex(@"stream\r?\n")]
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
}
