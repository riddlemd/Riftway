using System.Runtime.Versioning;
using System.Text;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A titled link: the payload of the <c>electron application/bookmark</c> type. Electron's
/// <c>ClipboardBookmark</c>.</summary>
public readonly record struct ClipboardBookmark(string Title, string Url);

/// <summary>One clipboard entry with one or more MIME-typed payloads. Electron's <c>ClipboardItem</c>.</summary>
/// <remarks>A payload is a <see cref="string"/> (written as UTF-8), a <see cref="byte"/> array (Electron's
/// <c>Blob</c>), a <see cref="ClipboardBookmark"/> for <c>electron application/bookmark</c>, or a <see cref="Task{T}"/>
/// of one of those, awaited by <see cref="Clipboard.WriteAsync"/>. Payload types are checked when the item is
/// written, as in Electron.</remarks>
public sealed class ClipboardItem
{
    private readonly KeyValuePair<string, object?>[]? _data;
    private readonly IReadOnlyList<string>? _readTypes;

    /// <summary>An entry for <see cref="Clipboard.WriteAsync"/>, keyed by MIME type. Electron's
    /// <c>new ClipboardItem(items)</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="items"/> is null or empty; Electron throws a
    /// <c>TypeError</c>.</exception>
    public ClipboardItem(IReadOnlyDictionary<string, object?> items)
    {
        if (items is null)
            throw new ArgumentException("Failed to construct 'ClipboardItem': 1 argument required — an object mapping MIME types to payloads.", nameof(items));
        if (items.Count == 0) throw new ArgumentException("Failed to construct 'ClipboardItem': at least one MIME type is required.", nameof(items));
        _data = [.. items];
    }

    private ClipboardItem(IReadOnlyList<string> types) => _readTypes = types;

    internal static ClipboardItem ForRead(IReadOnlyList<string> types) => new(types);

    internal bool IsReadSide => _readTypes is not null;

    /// <summary>The MIME types this entry carries: the constructor's keys, or what the clipboard held when
    /// <see cref="Clipboard.ReadAsync"/> ran. Electron's <c>clipboardItem.types</c>.</summary>
    public IReadOnlyList<string> Types => _readTypes is { } types ? [.. types] : [.. _data!.Select(p => p.Key)];

    /// <summary>The payload for <paramref name="type"/>: a <see cref="byte"/> array (Electron's <c>Blob</c>), or a
    /// <see cref="ClipboardBookmark"/> for <c>electron application/bookmark</c>. An item from
    /// <see cref="Clipboard.ReadAsync"/> reads the clipboard now. Electron's <c>clipboardItem.getType()</c>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is not one of <see cref="Types"/>.</exception>
    public async Task<object> GetTypeAsync(string type)
    {
        if (_readTypes is { } types)
        {
            if (!types.Contains(type)) throw NotFound(type);
            return await Clipboard.ReadTypeAsync(type).ConfigureAwait(false);
        }
        var entry = _data!.FirstOrDefault(p => p.Key == type);
        if (entry.Key is null) throw NotFound(type);
        var value = await ClipboardPayloads.Resolve(entry.Value).ConfigureAwait(false);
        if (type == ClipboardFormats.BookmarkMime) return value!;
        return value switch
        {
            byte[] bytes => bytes,
            string text => Encoding.UTF8.GetBytes(text),
            _ => throw new ArgumentException($"The payload for '{type}' must be a string or a byte array."),
        };
    }

    /// <summary>The payloads resolved and checked as Electron's native <c>ClipboardItem</c> constructor does, ready for
    /// the writer.</summary>
    internal async Task<IReadOnlyList<(string Mime, object Payload)>> ToNativeAsync()
    {
        if (IsReadSide)
            throw new ArgumentException("clipboard.write cannot accept a ClipboardItem returned from clipboard.read() — construct a new ClipboardItem to write.");
        var resolved = new List<(string, object?)>();
        foreach (var (mime, payload) in _data!) resolved.Add((mime, await ClipboardPayloads.Resolve(payload).ConfigureAwait(false)));
        return ClipboardPayloads.ToNative(resolved);
    }

    private static InvalidOperationException NotFound(string type) => new($"The type '{type}' was not found in the ClipboardItem");
}

/// <summary>The system clipboard. Electron's <c>clipboard</c>, the W3C-style API of Electron 44.</summary>
/// <remarks>Reads run off the calling thread, as Chromium's non-blocking clipboard reads do. Writes replace everything
/// on the clipboard. A clipboard another process holds reads as empty and drops a write, as in Chromium.</remarks>
public static class Clipboard
{
    private static readonly IClipboardBackend s_backend = OperatingSystem.IsWindows() ? new WindowsClipboardBackend() : new UnsupportedClipboardBackend();

    /// <summary>The plain text, or "". Electron's <c>clipboard.readText()</c>.</summary>
    public static Task<string> ReadTextAsync() => Task.Run(() => ClipboardReader.ReadText(s_backend));

    /// <summary>Replaces the clipboard with <paramref name="text"/>. Electron's <c>clipboard.writeText()</c>.</summary>
    public static Task WriteTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var writer = new ClipboardWriter();
        writer.WriteText(text);
        Commit(writer);
        return Task.CompletedTask;
    }

    /// <summary>One <see cref="ClipboardItem"/> whose <see cref="ClipboardItem.Types"/> lists what the clipboard holds.
    /// Electron's <c>clipboard.read()</c>.</summary>
    public static async Task<IReadOnlyList<ClipboardItem>> ReadAsync()
        => [ClipboardItem.ForRead(await Task.Run(s_backend.AvailableTypes).ConfigureAwait(false))];

    /// <summary>Replaces the clipboard with every entry of <paramref name="data"/>, in one clipboard session. Electron's
    /// <c>clipboard.write()</c>.</summary>
    /// <exception cref="ArgumentException">An entry is null, came from <see cref="ReadAsync"/>, or has a payload of the
    /// wrong type; nothing is written. Electron rejects with a <c>TypeError</c>.</exception>
    public static async Task WriteAsync(IReadOnlyList<ClipboardItem> data)
    {
        if (data is null) throw new ArgumentException("clipboard.write expects an array of ClipboardItem", nameof(data));
        var natives = new List<IReadOnlyList<(string, object)>>();
        foreach (var item in data)
        {
            if (item is null) throw new ArgumentException("clipboard.write expects an array of ClipboardItem", nameof(data));
            natives.Add(await item.ToNativeAsync().ConfigureAwait(false));
        }
        var writer = new ClipboardWriter();
        foreach (var native in natives) ClipboardPayloads.WriteTo(writer, native, Decode);
        Commit(writer);
    }

    /// <summary>Whether <see cref="ReadAsync"/> would list <paramref name="mimetype"/>. Electron's
    /// <c>clipboard.has()</c>.</summary>
    public static async Task<bool> HasAsync(string mimetype)
    {
        ArgumentNullException.ThrowIfNull(mimetype);
        return (await Task.Run(s_backend.AvailableTypes).ConfigureAwait(false)).Contains(mimetype);
    }

    /// <summary>Empties the clipboard. Electron's <c>clipboard.clear()</c>.</summary>
    public static void Clear() => s_backend.Clear();

    internal static Task<object> ReadTypeAsync(string mime) => Task.Run(() => ClipboardReader.ReadType(s_backend, mime, () => OrielApp.CurrentOrNull is { IsReady: true }));

    private static void Commit(ClipboardWriter writer)
    {
        if (writer.Commit(ImageCodec.EncodePng) is { } blocks) s_backend.Write(blocks);
    }

    private static ImageBitmap? Decode(byte[] bytes) => ImageCodec.Decode(bytes);
}

/// <summary>Electron's payload handling for <c>ClipboardItem</c>: resolving promises, the native constructor's type
/// checks, and <c>ClipboardItem::WriteTo</c>.</summary>
internal static class ClipboardPayloads
{
    /// <summary>A task's result, or the value itself.</summary>
    internal static async Task<object?> Resolve(object? payload)
    {
        if (payload is not Task task) return payload;
        await task.ConfigureAwait(false);
        return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
    }

    /// <summary>Electron's native <c>ClipboardItem</c> constructor: the bookmark type takes a bookmark, the text types a
    /// string or bytes (stored as text), every other type a string or bytes (stored as bytes).</summary>
    internal static IReadOnlyList<(string Mime, object Payload)> ToNative(IEnumerable<(string Mime, object? Value)> entries)
    {
        var native = new List<(string, object)>();
        foreach (var (mime, value) in entries)
        {
            if (mime == ClipboardFormats.BookmarkMime)
            {
                if (value is not ClipboardBookmark bookmark)
                    throw new ArgumentException("ClipboardItem payload for `electron application/bookmark` must be an object with `title` and `url` properties");
                native.Add((mime, bookmark));
                continue;
            }
            var bytes = value switch
            {
                byte[] b => b,
                string s => Encoding.UTF8.GetBytes(s),
                _ => throw new ArgumentException("ClipboardItem payload values must be a string or a Blob"),
            };
            native.Add(mime is "text/plain" or "text/html" or "text/rtf" or ClipboardFormats.FindTextMime ? (mime, Encoding.UTF8.GetString(bytes)) : (mime, bytes));
        }
        return native;
    }

    /// <summary>Electron's <c>ClipboardItem::WriteTo</c>: each payload, in byte order of its MIME type, to the writer
    /// call for its type.</summary>
    internal static void WriteTo(ClipboardWriter writer, IReadOnlyList<(string Mime, object Payload)> payloads, Func<byte[], ImageBitmap?> decode)
    {
        foreach (var (mime, payload) in payloads.OrderBy(p => Encoding.UTF8.GetBytes(p.Mime), Utf8Order.Instance))
        {
            switch (payload)
            {
                case ClipboardBookmark bookmark:
                    writer.WriteUrl(bookmark.Url ?? "");
                    break;
                case string text when mime == "text/plain": writer.WriteText(text); break;
                case string text when mime == "text/html": writer.WriteHtml(text); break;
                case string text when mime == "text/rtf": writer.WriteRtf(text); break;
                case string:
                    // The macOS find pasteboard; nothing on Windows.
                    break;
                case byte[] bytes when mime.StartsWith(ClipboardFormats.WebPrefix, StringComparison.Ordinal):
                    writer.WriteData(mime[ClipboardFormats.WebPrefix.Length..], bytes);
                    break;
                case byte[] bytes when ClipboardFormats.ParseOsFormat(mime) is { } format:
                    writer.WriteUnsafeRawData(format, bytes);
                    break;
                case byte[] bytes when mime == "text/uri-list":
                    writer.WriteFilenames(Encoding.UTF8.GetString(bytes));
                    break;
                case byte[] bytes when mime.StartsWith("image/", StringComparison.Ordinal):
                    if (decode(bytes) is { } image) writer.WriteImage(image);
                    break;
                case byte[] bytes:
                    writer.WriteUnsafeRawData(mime, bytes);
                    break;
            }
        }
    }

    private sealed class Utf8Order : IComparer<byte[]>
    {
        internal static readonly Utf8Order Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

/// <summary>Electron's read side: <c>clipboard.readText()</c> and the per-type dispatch of a read
/// <c>ClipboardItem</c>'s <c>getType()</c>, over one OS's primitives.</summary>
internal static class ClipboardReader
{
    /// <summary>Unicode text, else the ANSI text widened byte by byte as <c>base::ASCIIToUTF16</c> does.</summary>
    internal static string ReadText(IClipboardBackend backend)
    {
        var text = backend.ReadText();
        return text.Length > 0 ? text : new string(backend.ReadAsciiText().Select(b => (char)(ushort)(sbyte)b).ToArray());
    }

    /// <summary>Electron's <c>ReadMime</c>: a byte array, or a <see cref="ClipboardBookmark"/> for the bookmark type.</summary>
    /// <exception cref="InvalidOperationException">An image type before the app is ready.</exception>
    internal static object ReadType(IClipboardBackend backend, string mime, Func<bool> appReady)
    {
        if (mime.StartsWith(ClipboardFormats.WebPrefix, StringComparison.Ordinal)) return ReadRaw(backend, mime);
        if (ClipboardFormats.ParseOsFormat(mime) is { } format) return ReadRaw(backend, format);
        switch (mime)
        {
            case "text/plain":
                var text = backend.ReadText();
                return text.Length > 0 ? Encoding.UTF8.GetBytes(text) : backend.ReadAsciiText();
            case "text/html":
                return Encoding.UTF8.GetBytes(backend.ReadHtmlFragment());
            case "text/rtf":
                return backend.ReadRtf();
            case ClipboardFormats.BookmarkMime:
                return new ClipboardBookmark("", Gurl.Spec(backend.ReadUrl()));
            case "text/uri-list":
                return Encoding.UTF8.GetBytes(ClipboardFormats.PathsToUriList(backend.ReadFilenames()));
        }
        if (mime.StartsWith("image/", StringComparison.Ordinal))
        {
            // ReadPng decodes on Chromium's thread pool, which exists only once the app is ready.
            if (!appReady()) throw new InvalidOperationException("clipboard.read of image/* is available only after app ready");
            var png = backend.ReadPng();
            if (backend.DecodePng(png) is not { } bitmap) return Array.Empty<byte>();
            return mime == "image/jpeg" ? backend.EncodeJpeg(bitmap) : png;
        }
        return ReadRaw(backend, mime);
    }

    // Electron's ReadRawAndResolve: a web custom type maps to its platform name; anything else is the name itself.
    private static byte[] ReadRaw(IClipboardBackend backend, string format)
        => backend.ReadData(backend.CustomFormatNames().TryGetValue(format, out var platform) ? platform : format);
}

/// <summary>The clipboard primitives of one OS, each one Chromium <c>ui::Clipboard</c> call.</summary>
internal interface IClipboardBackend
{
    /// <summary>Electron's <c>EnumerateAvailableTypes</c>.</summary>
    IReadOnlyList<string> AvailableTypes();

    /// <summary>Unicode text before its NUL, or "".</summary>
    string ReadText();

    /// <summary>ANSI text bytes before their NUL, or empty.</summary>
    byte[] ReadAsciiText();

    /// <summary>The HTML fragment, or "".</summary>
    string ReadHtmlFragment();

    /// <summary>The RTF as NFC UTF-8, up to its NUL, or empty.</summary>
    byte[] ReadRtf();

    /// <summary>PNG bytes: the PNG format as stored, else the bitmap encoded; empty if there is no image.</summary>
    byte[] ReadPng();

    /// <summary>A PNG's premultiplied pixels, or null if it does not decode.</summary>
    ImageBitmap? DecodePng(byte[] png);

    /// <summary>A JPEG at quality 100, or empty.</summary>
    byte[] EncodeJpeg(ImageBitmap bitmap);

    /// <summary>The bookmark URL text, or "".</summary>
    string ReadUrl();

    /// <summary>The copied files' paths.</summary>
    IReadOnlyList<string> ReadFilenames();

    /// <summary>The W3C custom format map: <c>web type/subtype</c> to platform format name.</summary>
    IReadOnlyDictionary<string, string> CustomFormatNames();

    /// <summary>The whole block of the registered format <paramref name="name"/>, or empty.</summary>
    byte[] ReadData(string name);

    /// <summary>Empties the clipboard and puts the blocks on it.</summary>
    void Write(IReadOnlyList<(ClipboardFormat Format, byte[] Data)> blocks);

    /// <summary>Empties the clipboard.</summary>
    void Clear();
}

/// <summary>The backend on an OS without clipboard support yet.</summary>
internal sealed class UnsupportedClipboardBackend : IClipboardBackend
{
    public IReadOnlyList<string> AvailableTypes() => throw Unsupported();
    public string ReadText() => throw Unsupported();
    public byte[] ReadAsciiText() => throw Unsupported();
    public string ReadHtmlFragment() => throw Unsupported();
    public byte[] ReadRtf() => throw Unsupported();
    public byte[] ReadPng() => throw Unsupported();
    public ImageBitmap? DecodePng(byte[] png) => throw Unsupported();
    public byte[] EncodeJpeg(ImageBitmap bitmap) => throw Unsupported();
    public string ReadUrl() => throw Unsupported();
    public IReadOnlyList<string> ReadFilenames() => throw Unsupported();
    public IReadOnlyDictionary<string, string> CustomFormatNames() => throw Unsupported();
    public byte[] ReadData(string name) => throw Unsupported();
    public void Write(IReadOnlyList<(ClipboardFormat Format, byte[] Data)> blocks) => throw Unsupported();
    public void Clear() => throw Unsupported();

    private static PlatformNotSupportedException Unsupported() => new("The clipboard is not implemented on this OS yet.");
}

/// <summary>The Windows clipboard: Chromium's <c>ClipboardWin</c> reads on top of <see cref="WindowsClipboard"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsClipboardBackend : IClipboardBackend
{
    public IReadOnlyList<string> AvailableTypes()
    {
        var standard = ClipboardFormats.StandardTypes(
            WindowsClipboard.IsAvailable(WindowsClipboard.CfText), Available(ClipboardFormats.HtmlName), Available(ClipboardFormats.SvgName),
            Available(ClipboardFormats.RtfName), WindowsClipboard.IsAvailable(WindowsClipboard.CfDib), FilenamesAvailable());
        var formats = WindowsClipboard.Formats().Select(id => (id, id >= 0xC000 ? WindowsClipboard.FormatName(id) : null)).ToList();
        return ClipboardFormats.AvailableTypes(CustomFormatNames().Keys, standard, formats, ReadUrl().Trim(Controls).Length > 0);
    }

    public string ReadText() => WindowsClipboard.Read(WindowsClipboard.CfUnicodeText) is { } bytes ? ClipboardFormats.Utf16Text(bytes) : "";

    public byte[] ReadAsciiText() => WindowsClipboard.Read(WindowsClipboard.CfText) is { } bytes ? ClipboardFormats.TrimAfterNull(bytes) : [];

    public string ReadHtmlFragment() => Read(ClipboardFormats.HtmlName) is { } bytes ? ClipboardFormats.HtmlFragment(bytes) : "";

    public byte[] ReadRtf() => Read(ClipboardFormats.RtfName) is { } bytes ? ClipboardFormats.RtfToUtf8(bytes) : [];

    public byte[] ReadPng()
    {
        if (Read(ClipboardFormats.PngName) is { Length: > 0 } png) return png;
        if (WindowsClipboard.ReadDibPixels(out var width, out var height, out var bits) is not { } pixels || width == 0 || height == 0) return [];
        return ImageCodec.EncodePng(ClipboardFormats.FinishDib(width, height, pixels, bits));
    }

    public ImageBitmap? DecodePng(byte[] png) => png is [0x89, 0x50, 0x4E, 0x47, ..] ? ImageCodec.Decode(png) : null;

    public byte[] EncodeJpeg(ImageBitmap bitmap) => ImageCodec.EncodeJpeg(bitmap, 100);

    public string ReadUrl() => Read(ClipboardFormats.UrlName) is { } bytes ? ClipboardFormats.Utf16Text(bytes) : "";

    public IReadOnlyList<string> ReadFilenames()
    {
        if (!FilenamesAvailable()) return [];
        if (WindowsClipboard.ReadDropFiles() is { } files) return files;
        if (Read(ClipboardFormats.FileNameWName) is { } wide)
            return ClipboardFormats.Utf16Text(wide) is { Length: > 0 } path ? [path] : [];
        if (Read(ClipboardFormats.FileNameAName) is { } ansi && ClipboardFormats.TrimAfterNull(ansi) is { Length: > 0 } bytes)
            return [Ansi(bytes)];
        return [];
    }

    public IReadOnlyDictionary<string, string> CustomFormatNames()
        => Available(ClipboardFormats.WebCustomFormatMapName) && Read(ClipboardFormats.WebCustomFormatMapName) is { } json
            ? ClipboardFormats.ParseWebCustomFormatMap(json)
            : new SortedDictionary<string, string>();

    public byte[] ReadData(string name) => Read(name) ?? [];

    public void Write(IReadOnlyList<(ClipboardFormat Format, byte[] Data)> blocks)
    {
        var items = blocks.Select(b => (b.Format.Name is { } name ? WindowsClipboard.RegisterFormat(name) : b.Format.Id, b.Data)).ToArray();
        AsOwner(owner => WindowsClipboard.Write(items, owner));
    }

    public void Clear() => AsOwner(WindowsClipboard.Clear);

    private static nint s_owner;

    // Chromium writes on the UI thread with its message window as the clipboard owner. Before CEF's UI thread runs
    // there is no thread to pump such a window, so writes go out with no owner.
    private static void AsOwner(Action<nint> write)
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true })
        {
            write(0);
            return;
        }
        CefThread.InvokeOnUi(() =>
        {
            if (s_owner == 0) s_owner = WindowsClipboard.CreateOwnerWindow();
            write(s_owner);
            return true;
        });
    }

    private static readonly char[] Controls = [.. Enumerable.Range(0, 33).Select(c => (char)c)];

    private static bool FilenamesAvailable()
        => WindowsClipboard.IsAvailable(WindowsClipboard.CfHDrop) || Available(ClipboardFormats.FileNameWName) || Available(ClipboardFormats.FileNameAName);

    private static bool Available(string name) => WindowsClipboard.IsAvailable(WindowsClipboard.RegisterFormat(name));

    private static byte[]? Read(string name) => WindowsClipboard.Read(WindowsClipboard.RegisterFormat(name));

    // base::SysNativeMBToWide: the ANSI code page.
    private static string Ansi(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(0).GetString(bytes);
    }
}
