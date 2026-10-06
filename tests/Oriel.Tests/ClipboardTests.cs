using System.Text;

namespace Oriel.Tests;

public sealed class ClipboardTests
{
    private static readonly ImageBitmap s_pixel = new(1, 1, [1, 2, 3, 255]);

    private static List<(ClipboardFormat Format, byte[] Data)>? Commit(ClipboardWriter writer, byte[]? png = null) => writer.Commit(_ => png ?? [9]);

    private static string Names(IEnumerable<(ClipboardFormat Format, byte[] Data)> blocks) => string.Join(",", blocks.Select(b => b.Format.Name ?? b.Format.Id.ToString()));

    [Fact]
    public void Commit_puts_blocks_in_chromiums_order()
    {
        var w = new ClipboardWriter();
        w.WriteText("t");
        w.WriteFilenames("file:///C:/a");
        w.WriteUrl("https://example.com/");
        w.WriteRtf("r");
        w.WriteHtml("h");
        w.WriteImage(s_pixel);
        w.WriteData("x/y", [1]);
        w.WriteUnsafeRawData("Raw", [2]);
        Assert.Equal("Web Custom Format0,Raw,PNG,17,HTML Format,Rich Text Format,UniformResourceLocatorW,13,15,Web Custom Format Map", Names(Commit(w)!));
    }

    [Fact]
    public void Commit_is_null_when_nothing_was_written()
    {
        Assert.Null(Commit(new ClipboardWriter()));
        var w = new ClipboardWriter();
        w.WriteUrl("not a url");
        w.WriteImage(new ImageBitmap(0, 1, []));
        w.WriteImage(new ImageBitmap(1, 0, []));
        Assert.Null(Commit(w));
    }

    [Fact]
    public void Commit_of_empty_parts_empties_the_clipboard_without_blocks()
    {
        var w = new ClipboardWriter();
        w.WriteText("");
        w.WriteHtml("");
        w.WriteRtf("");
        w.WriteFilenames("https://not-a-file/");
        Assert.Empty(Commit(w)!);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("rtf")]
    [InlineData("files")]
    public void Commit_of_one_empty_part_still_empties_the_clipboard(string part)
    {
        var w = new ClipboardWriter();
        if (part == "text") w.WriteText("");
        if (part == "html") w.WriteHtml("");
        if (part == "rtf") w.WriteRtf("");
        if (part == "files") w.WriteFilenames("");
        Assert.Empty(Commit(w)!);
    }

    [Fact]
    public void Commit_encodes_text_html_rtf_and_url_as_chromium_does()
    {
        var w = new ClipboardWriter();
        w.WriteText("é");
        w.WriteHtml("<b>");
        w.WriteRtf("{é}");
        w.WriteUrl("HTTPS://Example.com");
        var blocks = Commit(w)!.ToDictionary(b => b.Format.Name ?? b.Format.Id.ToString(), b => b.Data);
        Assert.Equal(new byte[] { 0xE9, 0, 0, 0 }, blocks["13"]);
        Assert.Equal([.. Encoding.UTF8.GetBytes(ClipboardFormats.BuildCfHtml("<b>")), 0], blocks["HTML Format"]);
        Assert.Equal(Encoding.UTF8.GetBytes("{é}"), blocks["Rich Text Format"]);
        Assert.Equal(Encoding.Unicode.GetBytes("https://example.com/\0"), blocks["UniformResourceLocatorW"]);
    }

    [Fact]
    public void Commit_writes_png_then_a_dibv5_and_skips_an_empty_png()
    {
        var w = new ClipboardWriter();
        w.WriteImage(s_pixel);
        var blocks = Commit(w, [7, 7])!;
        Assert.Equal("PNG,17", Names(blocks));
        Assert.Equal(new byte[] { 7, 7 }, blocks[0].Data);
        Assert.Equal(ClipboardFormats.BuildDibV5(s_pixel), blocks[1].Data);
        var w2 = new ClipboardWriter();
        w2.WriteImage(s_pixel);
        Assert.Equal("17", Names(w2.Commit(_ => [])!));
    }

    [Fact]
    public void Commit_writes_files_as_a_drop_list()
    {
        var w = new ClipboardWriter();
        w.WriteFilenames("file:///C:/a%20b");
        Assert.Equal(ClipboardFormats.BuildDropFiles([@"C:\a b"]), Commit(w)!.Single().Data);
    }

    [Fact]
    public void WriteData_numbers_new_types_keeps_the_first_payload_and_records_the_map()
    {
        var w = new ClipboardWriter();
        w.WriteData("a/b", [1]);
        w.WriteUnsafeRawData("Raw", [2]);
        w.WriteData("c/d", [3]);
        w.WriteData("a/b", [4]);
        var blocks = Commit(w)!;
        Assert.Equal("Web Custom Format0,Raw,Web Custom Format2,Web Custom Format Map", Names(blocks));
        Assert.Equal(new byte[] { 1 }, blocks[0].Data);
        Assert.Equal("{\"a/b\":\"Web Custom Format0\",\"c/d\":\"Web Custom Format2\"}", Encoding.UTF8.GetString(blocks[3].Data));
    }

    [Fact]
    public void Commit_writes_a_raw_format_alone()
    {
        var w = new ClipboardWriter();
        w.WriteUnsafeRawData("Raw", [2]);
        Assert.Equal("Raw", Names(Commit(w)!));
    }

    [Fact]
    public void CommitMac_writes_platform_types_then_the_object_map_in_chromiums_order()
    {
        var w = new ClipboardWriter();
        w.WriteText("t");
        w.WriteFilenames("file:///a%20b\r\nhttps://x/\r\nfile://host/c");
        w.WriteUrl("HTTPS://Example.com", "Ex");
        w.WriteRtf("{é}");
        w.WriteHtml("<b>");
        w.WriteImage(s_pixel);
        w.WriteData("x/y", [1]);
        w.WriteUnsafeRawData("com.oriel.raw", [2]);
        MacPasteboardWrite[] expected =
        [
            new MacPasteboardWrite.Data("org.w3.web-custom-format.type-0", [1]),
            new MacPasteboardWrite.Data("com.oriel.raw", [2]),
            new MacPasteboardWrite.Image(s_pixel),
            new MacPasteboardWrite.Text("public.html", "<meta charset='utf-8'><b>"),
            new MacPasteboardWrite.Data("public.rtf", Encoding.UTF8.GetBytes("{é}")),
            new MacPasteboardWrite.Url("https://example.com/", "Ex"),
            new MacPasteboardWrite.Text("public.utf8-plain-text", "t"),
            new MacPasteboardWrite.Files(["/a b", "//host/c"]),
            new MacPasteboardWrite.Data("org.w3.web-custom-format.map", Encoding.UTF8.GetBytes("{\"x/y\":\"org.w3.web-custom-format.type-0\"}")),
        ];
        var writes = w.CommitMac()!;
        Assert.Equal(expected.Length, writes.Count);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(Describe(expected[i]), Describe(writes[i]));
    }

    private static string Describe(MacPasteboardWrite write) => write switch
    {
        MacPasteboardWrite.Data d => $"data {d.Type} {Convert.ToHexString(d.Bytes)}",
        MacPasteboardWrite.Image i => $"image {i.Bitmap.Width}x{i.Bitmap.Height} {Convert.ToHexString(i.Bitmap.Bgra)}",
        MacPasteboardWrite.Files f => $"files {string.Join("|", f.Paths)}",
        _ => write.ToString(),
    };

    [Fact]
    public void CommitMac_is_null_when_nothing_was_written_and_empty_parts_only_clear()
    {
        Assert.Null(new ClipboardWriter().CommitMac());
        var w = new ClipboardWriter();
        w.WriteUrl("not a url", "title");
        Assert.Null(w.CommitMac());
        var empty = new ClipboardWriter();
        empty.WriteText("");
        empty.WriteHtml("");
        empty.WriteRtf("");
        empty.WriteFilenames("https://not-a-file/");
        Assert.Empty(empty.CommitMac()!);
    }

    [Fact]
    public void CommitMac_keeps_an_untitled_bookmark()
    {
        var w = new ClipboardWriter();
        w.WriteUrl("https://e.com/");
        Assert.Equal([new MacPasteboardWrite.Url("https://e.com/", "")], w.CommitMac()!);
    }

    [Fact]
    public void Raw_and_custom_writes_share_a_limit_of_100()
    {
        var w = new ClipboardWriter();
        for (var i = 0; i < 99; i++) w.WriteUnsafeRawData("R" + i, [1]);
        w.WriteData("a/b", [1]);
        w.WriteData("c/d", [1]);
        w.WriteUnsafeRawData("Late", [1]);
        var names = Names(Commit(w)!);
        Assert.Contains("Web Custom Format99", names);
        Assert.DoesNotContain("Web Custom Format100", names);
        Assert.DoesNotContain("Late", names);
    }

    [Fact]
    public void ToNative_checks_payloads_like_electrons_constructor()
    {
        var bookmark = new ClipboardBookmark("t", "u");
        var native = ClipboardPayloads.ToNative([("electron application/bookmark", bookmark), ("text/plain", (object)new byte[] { 0xC3, 0xA9 }), ("text/html", "h"), ("image/png", "p"), ("x/y", new byte[] { 1 })]);
        Assert.Equal(bookmark, native[0].Payload);
        Assert.Equal("é", native[1].Payload);
        Assert.Equal("h", native[2].Payload);
        Assert.Equal(new byte[] { (byte)'p' }, native[3].Payload);
        Assert.Equal(new byte[] { 1 }, native[4].Payload);
        Assert.Equal("ClipboardItem payload for `electron application/bookmark` must be an object with `title` and `url` properties",
            Assert.Throws<ArgumentException>(() => ClipboardPayloads.ToNative([("electron application/bookmark", "u")])).Message);
        Assert.Equal("ClipboardItem payload values must be a string or a Blob", Assert.Throws<ArgumentException>(() => ClipboardPayloads.ToNative([("text/plain", 3)])).Message);
        Assert.Throws<ArgumentException>(() => ClipboardPayloads.ToNative([("x/y", bookmark)]));
    }

    [Fact]
    public void WriteTo_sends_each_type_to_its_writer_in_byte_order()
    {
        var w = new ClipboardWriter();
        var decoded = new List<byte[]>();
        ClipboardPayloads.WriteTo(w,
        [
            ("web b/b", new byte[] { 2 }), ("web a/a", new byte[] { 1 }), ("electron application/osclipboard;format=\"Raw\"", new byte[] { 3 }),
            ("electron application/findtext", "f"), ("text/uri-list", Encoding.UTF8.GetBytes("file:///C:/x")), ("image/png", new byte[] { 5 }),
            ("z/other", new byte[] { 6 }), ("electron application/bookmark", new ClipboardBookmark("", "https://e.com/")), ("text/plain", "t"),
        ], bytes => { decoded.Add(bytes); return s_pixel; });
        Assert.Equal("Raw,Web Custom Format1,Web Custom Format2,z/other,PNG,17,UniformResourceLocatorW,13,15,Web Custom Format Map", Names(Commit(w)!));
        Assert.Equal(new byte[] { 5 }, Assert.Single(decoded));
        Assert.Equal("{\"a/a\":\"Web Custom Format1\",\"b/b\":\"Web Custom Format2\"}", Encoding.UTF8.GetString(Commit(w)!.Last().Data));
    }

    [Fact]
    public void WriteTo_drops_an_image_that_does_not_decode()
    {
        var w = new ClipboardWriter();
        ClipboardPayloads.WriteTo(w, [("image/jpeg", new byte[] { 1 })], _ => null);
        Assert.Null(Commit(w));
    }

    [Fact]
    public async Task ClipboardItem_reads_back_what_it_was_given()
    {
        var item = new ClipboardItem(new Dictionary<string, object?>
        {
            ["text/plain"] = "é", ["image/png"] = new byte[] { 1 }, ["electron application/bookmark"] = new ClipboardBookmark("t", "u"), ["x/later"] = Task.FromResult("p"),
        });
        Assert.Equal(["text/plain", "image/png", "electron application/bookmark", "x/later"], item.Types);
        Assert.Equal(new byte[] { 0xC3, 0xA9 }, await item.GetTypeAsync("text/plain"));
        Assert.Equal(new byte[] { 1 }, await item.GetTypeAsync("image/png"));
        Assert.Equal(new ClipboardBookmark("t", "u"), await item.GetTypeAsync("electron application/bookmark"));
        Assert.Equal(new byte[] { (byte)'p' }, await item.GetTypeAsync("x/later"));
        Assert.Equal("The type 'text/html' was not found in the ClipboardItem", (await Assert.ThrowsAsync<InvalidOperationException>(() => item.GetTypeAsync("text/html"))).Message);
    }

    [Fact]
    public void ClipboardItem_needs_a_type()
        => Assert.Equal("Failed to construct 'ClipboardItem': at least one MIME type is required. (Parameter 'items')",
            Assert.Throws<ArgumentException>(() => new ClipboardItem(new Dictionary<string, object?>())).Message);

    [Fact]
    public async Task ToNativeAsync_awaits_task_payloads_and_refuses_a_read_item()
    {
        var native = await new ClipboardItem(new Dictionary<string, object?> { ["x/y"] = Task.FromResult<object>(new byte[] { 4 }) }).ToNativeAsync();
        Assert.Equal(new byte[] { 4 }, native.Single().Payload);
        var read = ClipboardItem.ForRead(["text/plain"]);
        Assert.Equal("clipboard.write cannot accept a ClipboardItem returned from clipboard.read() — construct a new ClipboardItem to write.",
            (await Assert.ThrowsAsync<ArgumentException>(read.ToNativeAsync)).Message);
        Assert.Equal("The type 'text/html' was not found in the ClipboardItem", (await Assert.ThrowsAsync<InvalidOperationException>(() => read.GetTypeAsync("text/html"))).Message);
    }

    [Fact]
    public async Task WriteAsync_refuses_a_missing_item_before_touching_the_clipboard()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Clipboard.WriteAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => Clipboard.WriteAsync([ClipboardItem.ForRead(["text/plain"])]));
        Assert.Equal("clipboard.write expects an array of ClipboardItem (Parameter 'data')", (await Assert.ThrowsAsync<ArgumentException>(() => Clipboard.WriteAsync([null!]))).Message);
    }

    [Fact]
    public void ReadText_falls_back_to_ansi_text_widened_byte_by_byte()
    {
        Assert.Equal("u", ClipboardReader.ReadText(new FakeBackend { Text = "u", Ascii = [(byte)'a'] }));
        Assert.Equal("a\uFF80", ClipboardReader.ReadText(new FakeBackend { Ascii = [(byte)'a', 0x80] }));
    }

    [Fact]
    public void ReadType_dispatches_text_html_rtf_bookmark_and_files()
    {
        var b = new FakeBackend { Text = "é", Html = "<i>", Rtf = [1], Url = "HTTPS://E.com", Files = [@"C:\a b"] };
        Assert.Equal(new byte[] { 0xC3, 0xA9 }, Read(b, "text/plain"));
        Assert.Equal(new byte[] { 2 }, Read(new FakeBackend { Ascii = [2] }, "text/plain"));
        Assert.Equal(Encoding.UTF8.GetBytes("<i>"), Read(b, "text/html"));
        Assert.Equal(new byte[] { 1 }, Read(b, "text/rtf"));
        Assert.Equal(new ClipboardBookmark("", "https://e.com/"), ClipboardReader.ReadType(b, "electron application/bookmark", () => true));
        Assert.Equal(Encoding.UTF8.GetBytes("file:///C:/a%20b"), Read(b, "text/uri-list"));
    }

    [Fact]
    public void ReadType_reads_custom_and_raw_formats_by_platform_name()
    {
        var b = new FakeBackend { Custom = new() { ["web a/b"] = "Web Custom Format3" } };
        Assert.Equal(Encoding.UTF8.GetBytes("Web Custom Format3"), Read(b, "web a/b"));
        Assert.Equal(Encoding.UTF8.GetBytes("web c/d"), Read(b, "web c/d"));
        Assert.Equal(Encoding.UTF8.GetBytes("HTML Format"), Read(b, "electron application/osclipboard;format=\"HTML Format\""));
        Assert.Equal(Encoding.UTF8.GetBytes("x/y"), Read(b, "x/y"));
    }

    [Fact]
    public void ReadType_of_an_image_needs_the_app_ready_and_a_decodable_png()
    {
        var b = new FakeBackend { Png = [7], Decodes = true };
        Assert.Equal("clipboard.read of image/* is available only after app ready", Assert.Throws<InvalidOperationException>(() => ClipboardReader.ReadType(b, "image/png", () => false)).Message);
        Assert.Equal(new byte[] { 7 }, Read(b, "image/png"));
        Assert.Equal(new byte[] { 7 }, Read(b, "image/svg+xml"));
        Assert.Equal(new byte[] { 8 }, Read(b, "image/jpeg"));
        Assert.Empty((byte[])Read(new FakeBackend { Png = [7] }, "image/png"));
    }

    [Fact]
    public void ReadType_keeps_the_bookmark_title_and_escapes_posix_paths()
    {
        var b = new FakeBackend { Title = "Tï", Url = "HTTPS://E.com", Files = [@"/a b/\é", "//host/x"], Posix = true };
        Assert.Equal(new ClipboardBookmark("Tï", "https://e.com/"), ClipboardReader.ReadType(b, "electron application/bookmark", () => true));
        Assert.Equal(Encoding.UTF8.GetBytes("file:///a%20b/%5C%C3%A9\r\nfile://host/x"), Read(b, "text/uri-list"));
    }

    [Fact]
    public void ReadType_reads_the_find_pasteboard_where_there_is_one_and_raw_data_elsewhere()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("fïnd"), Read(new FakeBackend { FindText = "fïnd" }, "electron application/findtext"));
        Assert.Equal(Encoding.UTF8.GetBytes("electron application/findtext"), Read(new FakeBackend(), "electron application/findtext"));
    }

    [Fact]
    public void WriteTo_writes_find_text_at_once_and_passes_the_bookmark_title()
    {
        var w = new ClipboardWriter();
        var found = new List<string>();
        ClipboardPayloads.WriteTo(w, [("electron application/findtext", "needle"), ("electron application/bookmark", new ClipboardBookmark("Title", "https://e.com/"))], _ => null, found.Add);
        Assert.Equal(["needle"], found);
        Assert.Equal([new MacPasteboardWrite.Url("https://e.com/", "Title")], w.CommitMac()!);
    }

    private static object Read(FakeBackend b, string mime) => ClipboardReader.ReadType(b, mime, () => true);

    [Theory]
    [InlineData("https://Example.COM", "https://example.com/")]
    [InlineData("  http://a.b/c d\t ", "http://a.b/c%20d")]
    [InlineData("MyApp:Héllo", "myapp:H%C3%A9llo")]
    [InlineData("mailto:x@y.z", "mailto:x@y.z")]
    [InlineData("not a url", "")]
    [InlineData(":nope", "")]
    [InlineData("1abc:x", "")]
    [InlineData("a b:x", "")]
    [InlineData("http://exa mple.com/", "")]
    [InlineData("http://a.b/x\ty\n", "http://a.b/xy")]
    public void Gurl_Approximate_approximates_chromiums_canonical_form(string input, string spec) => Assert.Equal(spec, Gurl.Approximate(input));

    private sealed class FakeBackend : IClipboardBackend
    {
        public string Text = "", Html = "", Url = "", Title = "";
        public string? FindText;
        public bool Posix;
        public byte[] Ascii = [], Rtf = [], Png = [];
        public bool Decodes;
        public string[] Files = [];
        public Dictionary<string, string> Custom = [];

        public IReadOnlyList<string> AvailableTypes() => [];
        public string ReadText() => Text;
        public byte[] ReadAsciiText() => Ascii;
        public string ReadHtmlFragment() => Html;
        public byte[] ReadRtf() => Rtf;
        public byte[] ReadPng() => Png;
        public ImageBitmap? DecodePng(byte[] png) => Decodes ? s_pixel : null;
        public byte[] EncodeJpeg(ImageBitmap bitmap) => [8];
        public ClipboardBookmark ReadBookmark() => new(Title, Url);
        public bool PosixPaths => Posix;
        public string? ReadFindText() => FindText;
        public void WriteFindText(string text) => throw new InvalidOperationException("unit tests never write");
        public IReadOnlyList<string> ReadFilenames() => Files;
        public IReadOnlyDictionary<string, string> CustomFormatNames() => Custom;
        public byte[] ReadData(string name) => Encoding.UTF8.GetBytes(name);
        public void Write(ClipboardWriter writer) => throw new InvalidOperationException("unit tests never write");
        public void Clear() => throw new InvalidOperationException("unit tests never clear");
    }
}
