using System.Buffers.Binary;
using System.Text;

namespace Riftway.Tests;

public sealed class ClipboardFormatsTests
{
    [Fact]
    public void StandardTypes_follow_chromiums_order()
        => Assert.Equal(["text/plain", "text/html", "image/svg+xml", "text/rtf", "image/png", "text/uri-list"], ClipboardFormats.StandardTypes(true, true, true, true, true, true));

    [Theory]
    [InlineData(0, "text/plain")]
    [InlineData(1, "text/html")]
    [InlineData(2, "image/svg+xml")]
    [InlineData(3, "text/rtf")]
    [InlineData(4, "image/png")]
    [InlineData(5, "text/uri-list")]
    public void StandardTypes_lists_each_type_only_when_its_format_is_there(int which, string mime)
    {
        var f = Enumerable.Range(0, 6).Select(i => i == which).ToArray();
        Assert.Equal([mime], ClipboardFormats.StandardTypes(f[0], f[1], f[2], f[3], f[4], f[5]));
    }

    [Fact]
    public void AvailableTypes_lists_custom_then_standard_then_registered_by_id_then_bookmark()
    {
        (uint, string?)[] formats =
        [
            (0xC005, "Zed"), (0xC001, "Alpha"), (13, null), (0xBFFF, "Below the registered range"), (0xC003, "Ünicode"),
            (0xC004, "web c/d"), (0xC001, "Alpha"), (0xC006, ""),
        ];
        var types = ClipboardFormats.AvailableTypes(["web b/x", "web a/y"], ["text/plain"], formats, bookmark: true);
        Assert.Equal(
            ["web a/y", "web b/x", "text/plain", "electron application/osclipboard;format=\"Alpha\"", "web c/d", "electron application/osclipboard;format=\"Zed\"", "electron application/bookmark"],
            types);
    }

    [Theory]
    [InlineData("HTML Format")]
    [InlineData("image/svg+xml")]
    [InlineData("Rich Text Format")]
    [InlineData("PNG")]
    [InlineData("FileNameW")]
    public void AvailableTypes_skips_the_formats_listed_under_a_mime_type(string name)
        => Assert.Equal(["electron application/osclipboard;format=\"Other\""], ClipboardFormats.AvailableTypes([], [], [(0xC001, name), (0xC002, "Other")], false));

    [Fact]
    public void AvailableTypes_lists_a_registered_web_name_once()
        => Assert.Equal(["web a/b"], ClipboardFormats.AvailableTypes(["web a/b"], [], [(0xC001, "web a/b")], false));

    [Fact]
    public void OsFormat_wraps_and_parses_back()
    {
        var mime = ClipboardFormats.WrapOsFormat("HTML Format");
        Assert.Equal("electron application/osclipboard;format=\"HTML Format\"", mime);
        Assert.Equal("HTML Format", ClipboardFormats.ParseOsFormat(mime));
        Assert.Equal("", ClipboardFormats.ParseOsFormat("electron application/osclipboard;format=\"\""));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("electron application/osclipboardx;format=\"a\"")]
    [InlineData("electron application/osclipboarX;format=\"a\"")]
    [InlineData("electron application/osclipboard;fmt=\"a\"")]
    [InlineData("electron application/osclipboard;format=\"a")]
    public void ParseOsFormat_is_null_for_anything_else(string mime) => Assert.Null(ClipboardFormats.ParseOsFormat(mime));

    [Fact]
    public void BuildCfHtml_is_chromiums_document_with_utf8_byte_offsets()
    {
        // "<b>é</b>" is 9 UTF-8 bytes: the fragment runs from 141 to 150 and the document ends 36 bytes later.
        Assert.Equal(
            "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000186\r\nStartFragment:0000000141\r\nEndFragment:0000000150\r\n"
            + "<html>\r\n<body>\r\n<!--StartFragment--><b>é</b><!--EndFragment-->\r\n</body>\r\n</html>",
            ClipboardFormats.BuildCfHtml("<b>é</b>"));
        Assert.Equal("", ClipboardFormats.BuildCfHtml(""));
    }

    [Fact]
    public void BuildCfHtml_offsets_frame_the_fragment_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes(ClipboardFormats.BuildCfHtml("x✓y"));
        var text = Encoding.ASCII.GetString(bytes);
        int Field(string name) => int.Parse(text.Split(name + ":")[1][..10]);
        Assert.Equal("x✓y", Encoding.UTF8.GetString(bytes, Field("StartFragment"), Field("EndFragment") - Field("StartFragment")));
        Assert.Equal(bytes.Length, Field("EndHTML"));
        Assert.Equal("<html>", Encoding.ASCII.GetString(bytes, Field("StartHTML"), 6));
    }

    [Fact]
    public void HtmlFragment_reads_back_BuildCfHtml_with_a_trailing_nul()
        => Assert.Equal("<i>ü ✓</i>", ClipboardFormats.HtmlFragment([.. Encoding.UTF8.GetBytes(ClipboardFormats.BuildCfHtml("<i>ü ✓</i>")), 0, 0]));

    [Fact]
    public void HtmlFragment_ignores_whatever_follows_a_nul()
        => Assert.Equal("a", Html("Version:0.9\r\n<html><!--StartFragment-->a<!--EndFragment--></html>\0b<!--EndFragment-->"));

    [Fact]
    public void HtmlFragment_finds_the_html_tag_in_any_case_and_ends_at_the_last_end_comment()
        => Assert.Equal("a<!--EndFragment-->b", Html("Version:0.9\r\n<HTML><body><!--StartFragment-->a<!--EndFragment-->b<!--EndFragment--></body></HTML>"));

    [Fact]
    public void HtmlFragment_without_an_end_comment_ends_at_the_last_tag()
        => Assert.Equal("a</b>", Html("Version:0.9\r\n<html><!--StartFragment-->a</b></html>"));

    [Fact]
    public void HtmlFragment_without_comments_uses_the_header_byte_offsets()
    {
        const string Body = "<html><body>xé<p>y</body></html>";
        var header = "Version:0.9\r\nStartFragment:{0:D10}\r\nEndFragment:{1:D10}\r\n";
        var length = string.Format(header, 0, 0).Length;
        // "é<p>" starts one byte after "<html><body>x" and is five bytes long.
        var cf = string.Format(header, length + 13, length + 18) + Body;
        Assert.Equal("é<p>", Html(cf));
    }

    [Fact]
    public void HtmlFragment_is_empty_without_an_html_tag()
    {
        Assert.Equal("", Html("Version:0.9\r\n<body><!--StartFragment-->a<!--EndFragment--></body>"));
        Assert.Equal("", Html("Version:0.9\r\nStartFragment:0000000001\r\nEndFragment:0000000002\r\n<body>a</body>"));
    }

    [Fact]
    public void HtmlFragment_is_empty_when_the_fragment_starts_before_the_html()
        => Assert.Equal("", Html("Version:0.9\r\nStartFragment:0000000002\r\nEndFragment:0000000060\r\n<html>abc</html>"));

    [Fact]
    public void HtmlFragment_is_empty_when_the_fragment_ends_before_it_starts()
        => Assert.Equal("", Html("Version:0.9\r\nStartFragment:0000000070\r\nEndFragment:0000000069\r\n<html>abcdefghijklmnopqrstuvwxyz</html>"));

    [Fact]
    public void HtmlFragment_is_empty_without_offsets_or_comments() => Assert.Equal("", Html("Version:0.9\r\n<html>abc</html>"));

    [Fact]
    public void HtmlFragment_clamps_an_end_past_the_markup()
    {
        var header = "Version:0.9\r\nStartFragment:{0:D10}\r\nEndFragment:{1:D10}\r\n";
        var length = string.Format(header, 0, 0).Length;
        Assert.Equal("bc</html>", Html(string.Format(header, length + 7, length + 999) + "<html>abc</html>"));
    }

    [Fact]
    public void HtmlFragment_drops_a_fragment_starting_inside_a_character()
    {
        var header = "Version:0.9\r\nStartFragment:{0:D10}\r\nEndFragment:{1:D10}\r\n";
        var length = string.Format(header, 0, 0).Length;
        // Byte 7 of "<html>é" is the second byte of "é".
        Assert.Equal("", Html(string.Format(header, length + 7, length + 9) + "<html>é</html>"));
    }

    private static string Html(string cf) => ClipboardFormats.HtmlFragment(Encoding.UTF8.GetBytes(cf));

    [Fact]
    public void BuildDibV5_is_skias_header_and_premultiplied_rows_bottom_up()
    {
        // 1x2: top pixel half-transparent premultiplied red, bottom opaque blue.
        var dib = ClipboardFormats.BuildDibV5(new ImageBitmap(1, 2, [0, 0, 128, 128, 255, 0, 0, 255]));
        Assert.Equal(124 + 8, dib.Length);
        var h = dib.AsSpan();
        Assert.Equal((124, 1, 2), (BinaryPrimitives.ReadInt32LittleEndian(h), BinaryPrimitives.ReadInt32LittleEndian(h[4..]), BinaryPrimitives.ReadInt32LittleEndian(h[8..])));
        Assert.Equal((1, 32), (BinaryPrimitives.ReadInt16LittleEndian(h[12..]), BinaryPrimitives.ReadInt16LittleEndian(h[14..])));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(h[16..])); // BI_RGB
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(h[40..])); // no red mask
        Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(h[52..]));
        Assert.Equal(0x57696E20u, BinaryPrimitives.ReadUInt32LittleEndian(h[56..]));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(h[108..]));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 128, 128 }, dib[124..]);
    }

    [Fact]
    public void FinishDib_keeps_valid_premultiplied_alpha_at_32_bits()
        => Assert.Equal(new byte[] { 10, 20, 30, 40 }, ClipboardFormats.FinishDib(1, 1, [10, 20, 30, 40], 32).Bgra);

    [Theory]
    [InlineData(50, 20, 30)]
    [InlineData(10, 50, 30)]
    [InlineData(10, 20, 50)]
    public void FinishDib_makes_every_pixel_opaque_when_a_channel_exceeds_alpha(byte b, byte g, byte r)
        => Assert.Equal(new byte[] { 1, 1, 1, 255, b, g, r, 255 }, ClipboardFormats.FinishDib(2, 1, [1, 1, 1, 5, b, g, r, 40], 32).Bgra);

    [Fact]
    public void FinishDib_makes_every_pixel_opaque_below_32_bits()
        => Assert.Equal(new byte[] { 0, 0, 0, 255 }, ClipboardFormats.FinishDib(1, 1, [0, 0, 0, 0], 24).Bgra);

    [Theory]
    [InlineData("text/plain", "text", "plain")]
    [InlineData("  application/x.my-app+json  ", "application", "x.my-app+json")]
    [InlineData("A!#$%&'*+-.^_`|~9/b", "A!#$%&'*+-.^_`|~9", "b")]
    public void TryParseMimeType_accepts_tokens(string text, string type, string subtype)
    {
        Assert.True(ClipboardFormats.TryParseMimeType(text, out var t, out var s));
        Assert.Equal((type, subtype), (t, s));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("a/b/c")]
    [InlineData("a /b")]
    [InlineData("a/ b")]
    [InlineData("/b")]
    [InlineData("a/")]
    [InlineData("a/b;c=d")]
    [InlineData("é/b")]
    public void TryParseMimeType_rejects_non_tokens(string text) => Assert.False(ClipboardFormats.TryParseMimeType(text, out _, out _));

    [Fact]
    public void WebCustomFormatMapJson_sorts_keys_and_escapes_like_chromium()
        => Assert.Equal("{\"a\\\"\\u003C\\u003C\\n\\u0001\":\"Web Custom Format1\",\"b/c\":\"Web Custom Format0\"}",
            ClipboardFormats.WebCustomFormatMapJson([new("b/c", "Web Custom Format0"), new("a\"<<\n\u0001", "Web Custom Format1")]));

    [Fact]
    public void ParseWebCustomFormatMap_keys_valid_mime_types_with_the_web_prefix()
    {
        var map = ClipboardFormats.ParseWebCustomFormatMap(Encoding.UTF8.GetBytes("{\"text/x\":\"Web Custom Format0\",\"bad\":\"Web Custom Format1\",\"a/b\":3, \" c/d \":\"F\"}"));
        Assert.Equal([new("web c/d", "F"), new("web text/x", "Web Custom Format0")], map);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[\"a/b\"]")]
    public void ParseWebCustomFormatMap_is_empty_for_anything_but_an_object(string json)
        => Assert.Empty(ClipboardFormats.ParseWebCustomFormatMap(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void UriListToPaths_reads_file_urls_as_windows_paths()
    {
        var list = " file:///C:/a%20b/%C3%A9.txt \r\n\r\nhttps://x/y\r\nfile://host/share/f\r\nfile:///usr/x\r\nfile://\r\nfile:///C:rel";
        Assert.Equal([@"C:\a b\é.txt", @"\\host\share\f", @"\usr\x", @"\C:rel"], ClipboardFormats.UriListToPaths(list));
    }

    [Fact]
    public void PathsToUriList_escapes_like_chromium_on_windows()
        => Assert.Equal("file:///C:/a%20b/%C3%A9%3B%23%3F%25.txt\r\nfile://host/x\r\nfile:///x",
            ClipboardFormats.PathsToUriList([@"C:\a b\é;#?%.txt", @"\\host\x", @"\x"]));

    [Fact]
    public void UriListToPaths_keeps_posix_paths_as_they_are()
    {
        var list = "file:///a%20b/c\\d\r\nfile://host/f\r\nfile:///C:/x\r\nfile:///C:\\y";
        Assert.Equal(["/a b/c\\d", "//host/f", "C:/x", "/C:\\y"], ClipboardFormats.UriListToPaths(list, posix: true));
    }

    [Fact]
    public void PathsToUriList_escapes_like_chromium_on_posix()
        => Assert.Equal("file:///a%20b/%5Cc%C3%A9%3B\r\nfile://host/x\r\nfile:///rel\r\nfile:///%5C%5Cx",
            ClipboardFormats.PathsToUriList(["/a b/\\cé;", "//host/x", "rel", "\\\\x"], posix: true));

    [Fact]
    public void Mac_AllFormats_keeps_ascii_types_and_adds_html_for_rtf_and_both_image_types()
    {
        Assert.Equal(["a.z", "public.html", "public.rtf"], ClipboardFormats.Mac.AllFormats(["public.rtf", "a.z", "ünicode"]));
        Assert.Equal(["public.png", "public.tiff"], ClipboardFormats.Mac.AllFormats(["public.tiff"]));
        Assert.Equal(["public.png", "public.tiff"], ClipboardFormats.Mac.AllFormats(["public.png"]));
        Assert.Equal(["B", "a"], ClipboardFormats.Mac.AllFormats(["a", "B"]));
    }

    [Fact]
    public void Mac_StandardTypes_follow_chromiums_order_and_files_hide_an_image()
    {
        var formats = new HashSet<string> { "public.utf8-plain-text", "public.html", "public.svg-image", "public.rtf", "public.file-url" };
        Assert.Equal(["text/plain", "text/html", "image/svg+xml", "text/rtf", "text/uri-list"], ClipboardFormats.Mac.StandardTypes(formats, () => true));
        Assert.Equal(["image/png"], ClipboardFormats.Mac.StandardTypes(new HashSet<string>(), () => true));
        Assert.Empty(ClipboardFormats.Mac.StandardTypes(new HashSet<string>(), () => false));
    }

    [Theory]
    [InlineData("public.utf8-plain-text", "text/plain")]
    [InlineData("public.html", "text/html")]
    [InlineData("public.svg-image", "image/svg+xml")]
    [InlineData("public.rtf", "text/rtf")]
    [InlineData("public.file-url", "text/uri-list")]
    public void Mac_StandardTypes_lists_each_type_only_when_its_format_is_there(string format, string mime)
        => Assert.Equal([mime], ClipboardFormats.Mac.StandardTypes(new HashSet<string> { format }, () => false));

    [Fact]
    public void Mac_AvailableTypes_lists_custom_standard_findtext_raw_then_bookmark()
    {
        var formats = ClipboardFormats.Mac.AllFormats(
            ["public.utf8-plain-text", "public.url", "org.w3.web-custom-format.map", "web x/y", "web a/y", "public.tiff", "public.file-url", "", "Zed", "public.svg-image"]);
        var types = ClipboardFormats.Mac.AvailableTypes(["web b/x", "web a/y"], ["text/plain"], findText: true, formats, bookmark: true);
        Assert.Equal(
            ["web a/y", "web b/x", "text/plain", "electron application/findtext", "electron application/osclipboard;format=\"Zed\"",
                "electron application/osclipboard;format=\"org.w3.web-custom-format.map\"", "electron application/osclipboard;format=\"public.url\"", "web x/y",
                "electron application/bookmark"],
            types);
        Assert.Equal(["text/plain"], ClipboardFormats.Mac.AvailableTypes([], ["text/plain"], findText: false, new SortedSet<string> { "public.html" }, bookmark: false));
    }

    [Fact]
    public void BuildDropFiles_is_a_wide_dropfiles_block()
    {
        var block = ClipboardFormats.BuildDropFiles([@"C:\a", "b"])!;
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(block));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(16)));
        Assert.Equal("C:\\a\0b\0\0", Encoding.Unicode.GetString(block, 20, block.Length - 20));
        Assert.Null(ClipboardFormats.BuildDropFiles([]));
    }

    [Fact]
    public void RtfToUtf8_keeps_ascii_up_to_the_nul()
        => Assert.Equal(new byte[] { (byte)'{', (byte)'}' }, ClipboardFormats.RtfToUtf8([(byte)'{', (byte)'}', 0, 0xE9]));

    [Fact]
    public void RtfToUtf8_normalizes_utf8_to_nfc()
        => Assert.Equal(new byte[] { 0xC3, 0xA9 }, ClipboardFormats.RtfToUtf8(Encoding.UTF8.GetBytes("é")));

    [Fact]
    public void RtfToUtf8_reads_other_bytes_as_windows_1252()
        => Assert.Equal(Encoding.UTF8.GetBytes("é€"), ClipboardFormats.RtfToUtf8([0xE9, 0x80]));

    [Fact]
    public void TrimAfterNull_and_Utf16Text_stop_at_the_first_nul()
    {
        Assert.Equal(new byte[] { 1, 2 }, ClipboardFormats.TrimAfterNull([1, 2, 0, 3]));
        Assert.Equal(new byte[] { 1, 2 }, ClipboardFormats.TrimAfterNull([1, 2]));
        Assert.Equal("ab", ClipboardFormats.Utf16Text([.. Encoding.Unicode.GetBytes("ab\0c"), 7]));
    }
}
