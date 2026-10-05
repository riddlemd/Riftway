using System.Text;

namespace Oriel;

/// <summary>A Windows clipboard format: a predefined <c>CF_</c> id, or a registered name.</summary>
internal readonly record struct ClipboardFormat(uint Id, string? Name)
{
    internal static ClipboardFormat Predefined(uint id) => new(id, null);
    internal static ClipboardFormat Registered(string name) => new(0, name);
}

/// <summary>One clipboard write: Chromium's <c>ScopedClipboardWriter</c> collecting the parts, and
/// <c>ClipboardWin</c>'s writers turning them into Windows blocks at <see cref="Commit"/>, in Chromium's order.</summary>
/// <remarks>A later part of the same kind replaces an earlier one, as each kind has one slot in Chromium's object
/// map. Raw and web custom formats share a limit of <see cref="ClipboardFormats.MaxRegisteredFormats"/>.</remarks>
internal sealed class ClipboardWriter
{
    private readonly List<(string Name, byte[] Data)> _platform = [];
    private readonly Dictionary<string, string> _registered = [];
    private int _counter;
    private string? _text, _html, _rtf, _url, _uriList;
    private ImageBitmap? _image;

    internal void WriteText(string text) => _text = text;

    internal void WriteHtml(string markup) => _html = markup;

    internal void WriteRtf(string rtf) => _rtf = rtf;

    /// <summary>Chromium skips a bookmark whose URL is invalid or empty; the title is not stored on Windows.</summary>
    internal void WriteUrl(string url)
    {
        var spec = Gurl.Spec(url);
        if (spec.Length > 0) _url = spec;
    }

    internal void WriteFilenames(string uriList) => _uriList = uriList;

    internal void WriteImage(ImageBitmap bitmap)
    {
        if (bitmap.Width > 0 && bitmap.Height > 0) _image = bitmap;
    }

    /// <summary>A W3C custom format, by its bare MIME type: stored as the next <c>Web Custom FormatN</c> and recorded in
    /// the map; a type already written keeps its first payload.</summary>
    internal void WriteData(string format, byte[] data)
    {
        if (_counter >= ClipboardFormats.MaxRegisteredFormats) return;
        // UTF16ToASCII keeps the low byte of each code unit.
        var ascii = new string(format.Select(c => (char)(byte)c).ToArray());
        if (_registered.ContainsKey(ascii)) return;
        var name = "Web Custom Format" + _counter;
        _registered[ascii] = name;
        _counter++;
        _platform.Add((name, data));
    }

    /// <summary>Electron's <c>WriteUnsafeRawData</c> patch: the bytes under the registered format
    /// <paramref name="format"/>.</summary>
    internal void WriteUnsafeRawData(string format, byte[] data)
    {
        if (_counter >= ClipboardFormats.MaxRegisteredFormats) return;
        _counter++;
        _platform.Add((format, data));
    }

    /// <summary>The blocks to put on the clipboard, in Chromium's order (raw and custom formats, then image, HTML, RTF,
    /// URL, text, files, custom format map); null when nothing was written, so the clipboard is left alone. Empty
    /// parts empty the clipboard without adding a block.</summary>
    internal List<(ClipboardFormat Format, byte[] Data)>? Commit(Func<ImageBitmap, byte[]> encodePng)
    {
        if (_text is null && _html is null && _rtf is null && _url is null && _uriList is null && _image is null && _platform.Count == 0 && _registered.Count == 0)
            return null;
        var blocks = _platform.Select(p => (ClipboardFormat.Registered(p.Name), p.Data)).ToList();
        if (_image is { } image)
        {
            if (encodePng(image) is { Length: > 0 } png) blocks.Add((ClipboardFormat.Registered(ClipboardFormats.PngName), png));
            blocks.Add((ClipboardFormat.Predefined(17 /* CF_DIBV5 */), ClipboardFormats.BuildDibV5(image)));
        }
        if (_html is { Length: > 0 } html) blocks.Add((ClipboardFormat.Registered(ClipboardFormats.HtmlName), Nul(Encoding.UTF8.GetBytes(ClipboardFormats.BuildCfHtml(html)), 1)));
        if (_rtf is { Length: > 0 } rtf) blocks.Add((ClipboardFormat.Registered(ClipboardFormats.RtfName), Encoding.UTF8.GetBytes(rtf)));
        if (_url is { } url) blocks.Add((ClipboardFormat.Registered(ClipboardFormats.UrlName), Nul(Encoding.Unicode.GetBytes(url), 2)));
        if (_text is { Length: > 0 } text) blocks.Add((ClipboardFormat.Predefined(13 /* CF_UNICODETEXT */), Nul(Encoding.Unicode.GetBytes(text), 2)));
        if (_uriList is { Length: > 0 } uriList && ClipboardFormats.BuildDropFiles(ClipboardFormats.UriListToPaths(uriList)) is { } drop)
            blocks.Add((ClipboardFormat.Predefined(15 /* CF_HDROP */), drop));
        if (_registered.Count > 0)
            blocks.Add((ClipboardFormat.Registered(ClipboardFormats.WebCustomFormatMapName), Encoding.UTF8.GetBytes(ClipboardFormats.WebCustomFormatMapJson(_registered))));
        return blocks;
    }

    private static byte[] Nul(byte[] bytes, int width) => [.. bytes, .. new byte[width]];
}
