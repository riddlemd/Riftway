using System.Text;

namespace Oriel;

/// <summary>A Windows clipboard format: a predefined <c>CF_</c> id, or a registered name.</summary>
internal readonly record struct ClipboardFormat(uint Id, string? Name)
{
    internal static ClipboardFormat Predefined(uint id) => new(id, null);
    internal static ClipboardFormat Registered(string name) => new(0, name);
}

/// <summary>One step of Chromium's <c>ClipboardMac</c> write, in its order.</summary>
internal abstract record MacPasteboardWrite
{
    /// <summary><c>[pasteboard setData:forType:]</c>.</summary>
    internal sealed record Data(string Type, byte[] Bytes) : MacPasteboardWrite;

    /// <summary><c>[pasteboard setString:forType:]</c> of UTF-8 text.</summary>
    internal sealed record Text(string Type, string Value) : MacPasteboardWrite;

    /// <summary><c>WriteBitmapInternal</c>: a PNG item, else a TIFF-only NSImage.</summary>
    internal sealed record Image(ImageBitmap Bitmap) : MacPasteboardWrite;

    /// <summary><c>WriteURL</c>: Chromium's URL item, merged into the pasteboard by <c>AddDataToPasteboard</c>.</summary>
    internal sealed record Url(string Spec, string Title) : MacPasteboardWrite;

    /// <summary><c>WriteFilesToPasteboard</c>: one file URL item per path.</summary>
    internal sealed record Files(IReadOnlyList<string> Paths) : MacPasteboardWrite;
}

/// <summary>One clipboard write: Chromium's <c>ScopedClipboardWriter</c> collecting the parts, and the OS writers
/// (<c>ClipboardWin</c>, <c>ClipboardMac</c>) turning them into blocks at commit, in Chromium's order.</summary>
/// <remarks>A later part of the same kind replaces an earlier one, as each kind has one slot in Chromium's object
/// map. Raw and web custom formats share a limit of <see cref="ClipboardFormats.MaxRegisteredFormats"/>.</remarks>
internal sealed class ClipboardWriter
{
    // A raw format by name, or a web custom format by its index (named per OS at commit).
    private readonly List<(string? Raw, int Custom, byte[] Data)> _platform = [];
    private readonly Dictionary<string, int> _registered = [];
    private int _counter;
    private string? _text, _html, _rtf, _uriList;
    private ClipboardBookmark? _url;
    private ImageBitmap? _image;

    internal void WriteText(string text) => _text = text;

    internal void WriteHtml(string markup) => _html = markup;

    internal void WriteRtf(string rtf) => _rtf = rtf;

    /// <summary>Chromium skips a bookmark whose URL is invalid or empty; Windows stores no title.</summary>
    internal void WriteUrl(string url, string title = "")
    {
        var spec = Gurl.Spec(url);
        if (spec.Length > 0) _url = new ClipboardBookmark(title, spec);
    }

    internal void WriteFilenames(string uriList) => _uriList = uriList;

    internal void WriteImage(ImageBitmap bitmap)
    {
        if (bitmap.Width > 0 && bitmap.Height > 0) _image = bitmap;
    }

    /// <summary>A W3C custom format, by its bare MIME type: stored under the next custom format name and recorded in
    /// the map; a type already written keeps its first payload.</summary>
    internal void WriteData(string format, byte[] data)
    {
        if (_counter >= ClipboardFormats.MaxRegisteredFormats) return;
        // UTF16ToASCII keeps the low byte of each code unit.
        var ascii = new string(format.Select(c => (char)(byte)c).ToArray());
        if (_registered.ContainsKey(ascii)) return;
        _registered[ascii] = _counter;
        _platform.Add((null, _counter, data));
        _counter++;
    }

    /// <summary>Electron's <c>WriteUnsafeRawData</c> patch: the bytes under the platform format
    /// <paramref name="format"/>.</summary>
    internal void WriteUnsafeRawData(string format, byte[] data)
    {
        if (_counter >= ClipboardFormats.MaxRegisteredFormats) return;
        _counter++;
        _platform.Add((format, 0, data));
    }

    private bool IsEmpty => _text is null && _html is null && _rtf is null && _url is null && _uriList is null && _image is null && _platform.Count == 0 && _registered.Count == 0;

    private string PlatformName((string? Raw, int Custom, byte[] Data) p, string customPrefix) => p.Raw ?? customPrefix + p.Custom;

    private string MapJson(string customPrefix) => ClipboardFormats.WebCustomFormatMapJson(_registered.Select(p => KeyValuePair.Create(p.Key, customPrefix + p.Value)));

    /// <summary>The blocks to put on the Windows clipboard, in Chromium's order (raw and custom formats, then image,
    /// HTML, RTF, URL, text, files, custom format map); null when nothing was written, so the clipboard is left alone.
    /// Empty parts empty the clipboard without adding a block.</summary>
    internal List<(ClipboardFormat Format, byte[] Data)>? Commit(Func<ImageBitmap, byte[]> encodePng)
    {
        if (IsEmpty) return null;
        const string Custom = "Web Custom Format";
        var blocks = _platform.Select(p => (ClipboardFormat.Registered(PlatformName(p, Custom)), p.Data)).ToList();
        if (_image is { } image)
        {
            if (encodePng(image) is { Length: > 0 } png) blocks.Add((ClipboardFormat.Registered(ClipboardFormats.PngName), png));
            blocks.Add((ClipboardFormat.Predefined(17 /* CF_DIBV5 */), ClipboardFormats.BuildDibV5(image)));
        }
        if (_html is { Length: > 0 } html) blocks.Add((ClipboardFormat.Registered(ClipboardFormats.HtmlName), Nul(Encoding.UTF8.GetBytes(ClipboardFormats.BuildCfHtml(html)), 1)));
        if (_rtf is { Length: > 0 } rtf) blocks.Add((ClipboardFormat.Registered(ClipboardFormats.RtfName), Encoding.UTF8.GetBytes(rtf)));
        if (_url is { } url) blocks.Add((ClipboardFormat.Registered(ClipboardFormats.UrlName), Nul(Encoding.Unicode.GetBytes(url.Url), 2)));
        if (_text is { Length: > 0 } text) blocks.Add((ClipboardFormat.Predefined(13 /* CF_UNICODETEXT */), Nul(Encoding.Unicode.GetBytes(text), 2)));
        if (_uriList is { Length: > 0 } uriList && ClipboardFormats.BuildDropFiles(ClipboardFormats.UriListToPaths(uriList)) is { } drop)
            blocks.Add((ClipboardFormat.Predefined(15 /* CF_HDROP */), drop));
        if (_registered.Count > 0)
            blocks.Add((ClipboardFormat.Registered(ClipboardFormats.WebCustomFormatMapName), Encoding.UTF8.GetBytes(MapJson(Custom))));
        return blocks;
    }

    /// <summary>The steps of Chromium's <c>ClipboardMac</c> write after <c>prepareForNewContents</c>: platform
    /// representations, then the object map in <c>Clipboard::Data</c> order (bitmap, HTML, RTF, URL, text, files,
    /// custom format map); null when nothing was written, so the pasteboard is left alone. Empty parts clear it
    /// without adding a type.</summary>
    internal List<MacPasteboardWrite>? CommitMac()
    {
        if (IsEmpty) return null;
        var mac = ClipboardFormats.Mac.WebCustomFormatPrefix;
        var writes = _platform.Select(p => (MacPasteboardWrite)new MacPasteboardWrite.Data(PlatformName(p, mac), p.Data)).ToList();
        if (_image is { } image) writes.Add(new MacPasteboardWrite.Image(image));
        if (_html is { Length: > 0 } html) writes.Add(new MacPasteboardWrite.Text(ClipboardFormats.Mac.Html, ClipboardFormats.Mac.HtmlCharsetPrefix + html));
        if (_rtf is { Length: > 0 } rtf) writes.Add(new MacPasteboardWrite.Data(ClipboardFormats.Mac.Rtf, Encoding.UTF8.GetBytes(rtf)));
        if (_url is { } url) writes.Add(new MacPasteboardWrite.Url(url.Url, url.Title));
        if (_text is { Length: > 0 } text) writes.Add(new MacPasteboardWrite.Text(ClipboardFormats.Mac.Text, text));
        if (_uriList is { Length: > 0 } uriList && ClipboardFormats.UriListToPaths(uriList, posix: true) is { Count: > 0 } paths)
            writes.Add(new MacPasteboardWrite.Files(paths));
        if (_registered.Count > 0)
            writes.Add(new MacPasteboardWrite.Data(ClipboardFormats.Mac.WebCustomFormatMap, Encoding.UTF8.GetBytes(MapJson(mac))));
        return writes;
    }

    private static byte[] Nul(byte[] bytes, int width) => [.. bytes, .. new byte[width]];
}
