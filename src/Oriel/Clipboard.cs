using System.Runtime.Versioning;
using System.Text;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What <see cref="Clipboard.Write"/> puts on the clipboard in one go; Electron's <c>data</c>.</summary>
public sealed record ClipboardData
{
    /// <summary>Plain text. Electron's <c>text</c>.</summary>
    public string? Text { get; init; }

    /// <summary>An HTML fragment. Electron's <c>html</c>.</summary>
    public string? Html { get; init; }

    /// <summary>Rich text. Electron's <c>rtf</c>.</summary>
    public string? Rtf { get; init; }

    /// <summary>An image. Electron's <c>image</c>.</summary>
    public NativeImage? Image { get; init; }

    /// <summary>The title of the bookmark whose URL is <see cref="Text"/>. Electron's <c>bookmark</c>.</summary>
    public string? Bookmark { get; init; }
}

/// <summary>A titled link; Electron's <c>readBookmark()</c> result.</summary>
public readonly record struct ClipboardBookmark(string Title, string Url);

/// <summary>The system clipboard. Electron's <c>clipboard</c>.</summary>
/// <remarks>Reads of missing data return empty strings, buffers and images, never null. Writes replace everything on the
/// clipboard. Images are encoded and decoded through CEF, so use them after <see cref="OrielApp.Create"/>.</remarks>
public static class Clipboard
{
    private static readonly IClipboardBackend s_backend = OperatingSystem.IsWindows() ? new WindowsClipboardBackend() : new UnsupportedClipboardBackend();

    /// <summary>The plain text, or "". Electron's <c>clipboard.readText()</c>.</summary>
    public static string ReadText() => s_backend.ReadText();

    /// <summary>Replaces the clipboard with <paramref name="text"/>. Electron's <c>clipboard.writeText()</c>.</summary>
    public static void WriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        s_backend.Write(new ClipboardParts { Text = text });
    }

    /// <summary>The HTML fragment, or "". Electron's <c>clipboard.readHTML()</c>.</summary>
    public static string ReadHtml() => s_backend.ReadHtml();

    /// <summary>Replaces the clipboard with the HTML fragment <paramref name="markup"/>. Electron's
    /// <c>clipboard.writeHTML()</c>.</summary>
    public static void WriteHtml(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        s_backend.Write(new ClipboardParts { Html = markup });
    }

    /// <summary>The rich text, or "". Electron's <c>clipboard.readRTF()</c>.</summary>
    public static string ReadRtf() => s_backend.ReadRtf();

    /// <summary>Replaces the clipboard with <paramref name="text"/> as rich text. Electron's
    /// <c>clipboard.writeRTF()</c>.</summary>
    public static void WriteRtf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        s_backend.Write(new ClipboardParts { Rtf = text });
    }

    /// <summary>The image, or an empty one. Electron's <c>clipboard.readImage()</c>.</summary>
    public static NativeImage ReadImage()
        => s_backend.ReadImage() is { Width: > 0 } bitmap
            ? NativeImage.CreateFromBitmap(bitmap.Bgra, new NativeImageBitmapOptions { Width = bitmap.Width, Height = bitmap.Height })
            : NativeImage.CreateEmpty();

    /// <summary>Replaces the clipboard with <paramref name="image"/>. Electron's <c>clipboard.writeImage()</c>.</summary>
    public static void WriteImage(NativeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        s_backend.Write(new ClipboardParts { Image = ImageOf(image) });
    }

    /// <summary>The bookmark, or one with an empty title and URL. Electron's <c>clipboard.readBookmark()</c>.</summary>
    public static ClipboardBookmark ReadBookmark() => s_backend.ReadBookmark();

    /// <summary>Replaces the clipboard with a bookmark. Electron's <c>clipboard.writeBookmark()</c>.</summary>
    public static void WriteBookmark(string title, string url)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(url);
        s_backend.Write(new ClipboardParts { Bookmark = new ClipboardBookmark(title, url) });
    }

    /// <summary>Whether the clipboard holds <paramref name="format"/>: a MIME type such as <c>text/plain</c>, or a custom
    /// format name. Electron's <c>clipboard.has()</c>.</summary>
    public static bool Has(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return s_backend.Has(format);
    }

    /// <summary>The MIME types on the clipboard among <c>text/plain</c>, <c>text/html</c>, <c>text/rtf</c> and
    /// <c>image/png</c>, each once. Electron's <c>clipboard.availableFormats()</c>.</summary>
    public static IReadOnlyList<string> AvailableFormats() => s_backend.AvailableFormats();

    /// <summary>Empties the clipboard. Electron's <c>clipboard.clear()</c>.</summary>
    public static void Clear() => s_backend.Clear();

    /// <summary>Replaces the clipboard with every part of <paramref name="data"/>, in one clipboard session. Electron's
    /// <c>clipboard.write()</c>.</summary>
    public static void Write(ClipboardData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        s_backend.Write(new ClipboardParts
        {
            Text = data.Text,
            Html = data.Html,
            Rtf = data.Rtf,
            Image = data.Image is null ? null : ImageOf(data.Image),
            Bookmark = data.Bookmark is { } title ? new ClipboardBookmark(title, data.Text ?? "") : null,
        });
    }

    /// <summary>The raw bytes of <paramref name="format"/> (a MIME type or a custom format name), or an empty array.
    /// Electron's <c>clipboard.readBuffer()</c>.</summary>
    public static byte[] ReadBuffer(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return s_backend.ReadBuffer(format);
    }

    /// <summary>Replaces the clipboard with <paramref name="buffer"/> as <paramref name="format"/> (a MIME type or a
    /// custom format name). Electron's <c>clipboard.writeBuffer()</c>.</summary>
    public static void WriteBuffer(string format, byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(buffer);
        s_backend.WriteBuffer(format, buffer);
    }

    private static ImageBitmap? ImageOf(NativeImage image) => image.IsEmpty() ? null : image.BitmapAt(1.0);
}

/// <summary>What one write puts on the clipboard; null parts are left out.</summary>
internal sealed record ClipboardParts
{
    internal string? Text { get; init; }
    internal string? Html { get; init; }
    internal string? Rtf { get; init; }
    internal ImageBitmap? Image { get; init; }
    internal ClipboardBookmark? Bookmark { get; init; }
}

/// <summary>The raw clipboard operations of one OS; <see cref="Clipboard"/> adds argument checks and the Electron shapes.</summary>
internal interface IClipboardBackend
{
    /// <summary>The plain text, or "".</summary>
    string ReadText();

    /// <summary>The HTML fragment, or "".</summary>
    string ReadHtml();

    /// <summary>The rich text, or "".</summary>
    string ReadRtf();

    /// <summary>The image as premultiplied BGRA, or null.</summary>
    ImageBitmap? ReadImage();

    /// <summary>The bookmark, or an empty one.</summary>
    ClipboardBookmark ReadBookmark();

    /// <summary>Whether the clipboard holds the MIME type or custom format.</summary>
    bool Has(string format);

    /// <summary>The known MIME types on the clipboard.</summary>
    IReadOnlyList<string> AvailableFormats();

    /// <summary>Empties the clipboard.</summary>
    void Clear();

    /// <summary>Replaces the clipboard with the non-null parts, in one session.</summary>
    void Write(ClipboardParts parts);

    /// <summary>The bytes of the MIME type or custom format, or an empty array.</summary>
    byte[] ReadBuffer(string format);

    /// <summary>Replaces the clipboard with the bytes as the MIME type or custom format.</summary>
    void WriteBuffer(string format, byte[] buffer);
}

/// <summary>The backend on an OS without clipboard support yet.</summary>
internal sealed class UnsupportedClipboardBackend : IClipboardBackend
{
    public string ReadText() => throw Unsupported();
    public string ReadHtml() => throw Unsupported();
    public string ReadRtf() => throw Unsupported();
    public ImageBitmap? ReadImage() => throw Unsupported();
    public ClipboardBookmark ReadBookmark() => throw Unsupported();
    public bool Has(string format) => throw Unsupported();
    public IReadOnlyList<string> AvailableFormats() => throw Unsupported();
    public void Clear() => throw Unsupported();
    public void Write(ClipboardParts parts) => throw Unsupported();
    public byte[] ReadBuffer(string format) => throw Unsupported();
    public void WriteBuffer(string format, byte[] buffer) => throw Unsupported();

    private static PlatformNotSupportedException Unsupported() => new("The clipboard is not implemented on this OS yet.");
}

/// <summary>The Windows clipboard: Chromium's formats on top of <see cref="WindowsClipboard"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsClipboardBackend : IClipboardBackend
{
    private static readonly Lazy<uint> s_url = new(() => WindowsClipboard.RegisterFormat("UniformResourceLocatorW"));

    public string ReadText() => WindowsClipboard.Read(WindowsClipboard.CfUnicodeText) is { } bytes ? Utf16(bytes) : "";

    public string ReadHtml()
        => Resolve("HTML Format") is { } id && WindowsClipboard.Read(id) is { } bytes ? ClipboardFormats.FragmentFromCfHtml(Utf8(bytes)) ?? "" : "";

    public string ReadRtf() => Resolve("Rich Text Format") is { } id && WindowsClipboard.Read(id) is { } bytes ? Utf8(bytes) : "";

    public ImageBitmap? ReadImage()
    {
        if (WindowsClipboard.Read(WindowsClipboard.RegisterFormat("PNG")) is { } png && ImageCodec.Decode(png) is { } decoded) return decoded;
        foreach (var format in new[] { WindowsClipboard.CfDibV5, WindowsClipboard.CfDib })
            if (WindowsClipboard.Read(format) is { } dib && ClipboardFormats.ParseDib(dib) is { } bitmap) return bitmap;
        return null;
    }

    public ClipboardBookmark ReadBookmark()
    {
        if (WindowsClipboard.Read(s_url.Value) is not { } bytes) return new ClipboardBookmark("", "");
        var text = Utf16(bytes);
        var newline = text.IndexOf('\n');
        return newline < 0 ? new ClipboardBookmark("", text) : new ClipboardBookmark(text[..newline], text[(newline + 1)..]);
    }

    public bool Has(string format)
    {
        var names = ClipboardFormats.WindowsNamesOf(format);
        if (names.Count == 0) return WindowsClipboard.IsAvailable(WindowsClipboard.RegisterFormat(format));
        return names.Any(name => WindowsClipboard.IsAvailable(Resolve(name)!.Value));
    }

    public IReadOnlyList<string> AvailableFormats()
        => ClipboardFormats.AvailableMimes(WindowsClipboard.Formats().Select(id => (id, WindowsClipboard.FormatName(id))));

    public void Clear() => WindowsClipboard.Clear();

    public void Write(ClipboardParts parts)
    {
        var items = new List<(uint, byte[])>();
        if (parts.Text is { } text) items.Add((WindowsClipboard.CfUnicodeText, Utf16(text)));
        if (parts.Html is { } html) items.Add((WindowsClipboard.RegisterFormat("HTML Format"), Utf8(ClipboardFormats.BuildCfHtml(html))));
        if (parts.Rtf is { } rtf) items.Add((WindowsClipboard.RegisterFormat("Rich Text Format"), Utf8(rtf)));
        if (parts.Image is { Width: > 0 } image)
        {
            if (ImageCodec.EncodePng(image) is { Length: > 0 } png) items.Add((WindowsClipboard.RegisterFormat("PNG"), png));
            items.Add((WindowsClipboard.CfDibV5, ClipboardFormats.BuildDibV5(image)));
        }
        if (parts.Bookmark is { } bookmark) items.Add((s_url.Value, Utf16(bookmark.Title + "\n" + bookmark.Url)));
        WindowsClipboard.Write(items.ToArray());
    }

    public byte[] ReadBuffer(string format)
    {
        foreach (var id in IdsOf(format))
            if (WindowsClipboard.Read(id) is { } bytes) return bytes;
        return [];
    }

    public void WriteBuffer(string format, byte[] buffer)
        => WindowsClipboard.Write([(IdsOf(format).First(), buffer)]);

    // A MIME type stands for its Windows formats, best first; any other name is a custom format.
    private static IEnumerable<uint> IdsOf(string format)
    {
        var names = ClipboardFormats.WindowsNamesOf(format);
        return names.Count == 0 ? [WindowsClipboard.RegisterFormat(format)] : names.Select(name => Resolve(name)!.Value);
    }

    private static uint? Resolve(string name) => ClipboardFormats.PredefinedId(name) ?? WindowsClipboard.RegisterFormat(name);

    // Windows text blocks are NUL-terminated and may be larger than the text.
    private static string Utf16(byte[] bytes)
    {
        var text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        var end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    private static string Utf8(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");
}
