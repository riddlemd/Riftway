using System.Text;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class LinuxClipboardTests
{
    private static readonly ImageBitmap s_pixel = new(1, 1, [1, 2, 3, 255]);
    private const string Os = "electron application/osclipboard;format=";

    private static (List<KeyValuePair<string, byte[]>> Offer, byte[]? PrimaryText) Commit(ClipboardWriter writer, byte[]? png = null) => writer.CommitLinux(_ => png ?? [9])!.Value;

    private static string Names(IEnumerable<KeyValuePair<string, byte[]>> offer) => string.Join(",", offer.Select(p => p.Key));

    [Fact]
    public void CommitLinux_offers_platform_formats_then_objects_in_chromiums_order()
    {
        var w = new ClipboardWriter();
        w.WriteText("t");
        w.WriteFilenames("file:///a");
        w.WriteUrl("https://example.com/");
        w.WriteRtf("r");
        w.WriteHtml("h");
        w.WriteImage(s_pixel);
        w.WriteUnsafeRawData("Raw", [2]);
        w.WriteData("x/y", [1]);
        Assert.Equal("Raw,application/web;type=\"custom/format1\",image/png,text/html,text/rtf,text/x-moz-url,text/plain,TEXT,STRING,text/plain;charset=utf-8,UTF8_STRING,"
            + "text/uri-list,application/web;type=\"custom/formatmap\"", Names(Commit(w).Offer));
    }

    [Fact]
    public void CommitLinux_encodes_each_part_as_clipboard_ozone_does()
    {
        var w = new ClipboardWriter();
        w.WriteText("é");
        w.WriteHtml("<b>");
        w.WriteRtf("{é}");
        w.WriteUrl("HTTPS://Example.com", "Tï");
        w.WriteFilenames("file:///tmp/a%20b\r\nhttps://x/\r\nfile:///tmp/c");
        w.WriteData("x/y", [1]);
        w.WriteImage(s_pixel);
        var offer = Commit(w, [8, 8]).Offer.ToDictionary(p => p.Key, p => p.Value);
        foreach (var name in (string[])["text/plain", "TEXT", "STRING", "text/plain;charset=utf-8", "UTF8_STRING"]) Assert.Equal(Encoding.UTF8.GetBytes("é"), offer[name]);
        Assert.Equal(Encoding.UTF8.GetBytes("<b>"), offer["text/html"]);
        Assert.Equal(Encoding.UTF8.GetBytes("{é}"), offer["text/rtf"]);
        Assert.Equal(Encoding.Unicode.GetBytes("https://example.com/\nTï"), offer["text/x-moz-url"]);
        Assert.Equal("file:///tmp/a%20b\r\nfile:///tmp/c", Encoding.UTF8.GetString(offer["text/uri-list"]));
        Assert.Equal("{\"x/y\":\"application/web;type=\\\"custom/format0\\\"\"}", Encoding.UTF8.GetString(offer["application/web;type=\"custom/formatmap\""]));
        Assert.Equal(new byte[] { 1 }, offer["application/web;type=\"custom/format0\""]);
        Assert.Equal(new byte[] { 8, 8 }, offer["image/png"]);
    }

    [Fact]
    public void CommitLinux_hands_the_text_on_for_primary()
    {
        var w = new ClipboardWriter();
        w.WriteText("sel é");
        Assert.Equal(Encoding.UTF8.GetBytes("sel é"), Commit(w).PrimaryText);
        var html = new ClipboardWriter();
        html.WriteHtml("<i>");
        Assert.Null(Commit(html).PrimaryText);
    }

    [Fact]
    public void CommitLinux_is_null_when_nothing_was_written() => Assert.Null(new ClipboardWriter().CommitLinux(_ => [1]));

    [Fact]
    public void CommitLinux_of_empty_parts_offers_nothing_and_no_primary_text()
    {
        var w = new ClipboardWriter();
        w.WriteText("");
        w.WriteHtml("");
        w.WriteRtf("");
        Assert.Empty(Commit(w).Offer);
        Assert.Null(Commit(w).PrimaryText);
    }

    [Fact]
    public void CommitLinux_offers_a_uri_list_without_file_urls_empty()
    {
        var w = new ClipboardWriter();
        w.WriteFilenames("https://not-a-file/");
        Assert.Equal("text/uri-list", Assert.Single(Commit(w).Offer).Key);
        Assert.Empty(Assert.Single(Commit(w).Offer).Value);
    }

    [Fact]
    public void CommitLinux_skips_an_image_that_does_not_encode()
    {
        var w = new ClipboardWriter();
        w.WriteImage(s_pixel);
        Assert.Empty(w.CommitLinux(_ => [])!.Value.Offer);
    }

    [Fact]
    public void StandardTypes_follow_the_order_of_the_names_with_text_once()
    {
        Assert.Equal(["text/plain", "image/png", "text/html"], ClipboardFormats.Linux.StandardTypes(["STRING", "TEXT", "UTF8_STRING", "image/png", "text/html", "text/plain"]));
        Assert.Equal(["text/uri-list", "image/svg+xml", "text/rtf"], ClipboardFormats.Linux.StandardTypes(["TARGETS", "text/uri-list", "image/svg+xml", "text/rtf", "chromium/x-web-custom-data"]));
    }

    [Theory]
    [InlineData("UTF8_STRING")]
    [InlineData("STRING")]
    [InlineData("TEXT")]
    [InlineData("text/plain")]
    [InlineData("text/plain;charset=utf-8")]
    public void StandardTypes_reads_each_text_name_as_text_plain(string name) => Assert.Equal(["text/plain"], ClipboardFormats.Linux.StandardTypes([name]));

    [Fact]
    public void AvailableTypes_lists_custom_then_standard_then_other_ascii_targets_in_byte_order()
    {
        var types = ClipboardFormats.Linux.AvailableTypes(["web b/b", "web a/a"], ["text/plain", "text/html"],
            ["text/plain", "UTF8_STRING", "text/html", "TARGETS", "web c/c", "café", "", "image/png", "image/svg+xml", "text/rtf", "text/uri-list", "TARGETS"]);
        Assert.Equal(["web a/a", "web b/b", "text/plain", "text/html", Os + "\"TARGETS\"", Os + "\"UTF8_STRING\"", "web c/c"], types);
    }

    [Fact]
    public void AvailableTypes_has_no_bookmark_on_linux()
        => Assert.DoesNotContain(ClipboardFormats.BookmarkMime, ClipboardFormats.Linux.AvailableTypes([], [], ["text/x-moz-url"]));

    [Fact]
    public void Custom_format_names_are_chromiums_aura_names()
    {
        Assert.Equal("application/web;type=\"custom/format7\"", ClipboardFormats.Linux.WebCustomFormatName(7));
        Assert.Equal("application/web;type=\"custom/formatmap\"", ClipboardFormats.Linux.WebCustomFormatMap);
    }

    [Fact]
    public void HasFormat_ignores_non_ascii_names()
    {
        Assert.True(ClipboardFormats.Linux.HasFormat(["a/b"], "a/b"));
        Assert.False(ClipboardFormats.Linux.HasFormat(["a/é"], "a/é"));
        Assert.False(ClipboardFormats.Linux.HasFormat(["a/c"], "a/b"));
    }

    [Fact]
    public void MimeTypesFromTargets_adds_chromiums_names_then_sorts_and_dedupes()
    {
        Assert.Equal(["STRING", "TARGETS", "chromium/x-web-custom-data", "image/png", "text/html", "text/plain", "text/rtf", "text/uri-list"],
            X11SelectionRules.MimeTypesFromTargets(["TARGETS", "STRING", "", "text/html", "image/png", "text/rtf", "text/uri-list", "chromium/x-web-custom-data", "STRING"]));
        Assert.Equal(["image/svg+xml"], X11SelectionRules.MimeTypesFromTargets(["image/svg+xml"]));
        Assert.Empty(X11SelectionRules.MimeTypesFromTargets([]));
    }

    [Theory]
    [InlineData("UTF8_STRING")]
    [InlineData("TEXT")]
    [InlineData("text/plain;charset=utf-8")]
    public void MimeTypesFromTargets_lists_text_plain_for_any_text_target(string target)
        => Assert.Contains("text/plain", X11SelectionRules.MimeTypesFromTargets([target]));

    [Fact]
    public void TargetsReply_puts_protocol_targets_then_uri_url_text_then_the_rest_by_atom()
    {
        // Atoms numbered unlike the reply's order, so only the reply's own grouping can put the text targets first.
        string[] names = ["TIMESTAMP", "TARGETS", "SAVE_TARGETS", "MULTIPLE", "image/png", "text/html", "text/plain;charset=utf-8", "text/plain", "TEXT", "STRING", "UTF8_STRING", "text/x-moz-url", "text/uri-list"];
        uint Atom(string name) => (uint)Array.IndexOf(names, name) + 100;
        // Held in an order unlike the reply's: html before png by atom, text names shuffled.
        uint[] held = [Atom("text/html"), Atom("STRING"), Atom("text/plain"), Atom("image/png"), Atom("text/uri-list"), Atom("UTF8_STRING"), Atom("text/x-moz-url")];
        var reply = X11SelectionRules.TargetsReply(held, Atom).Select(a => names[a - 100]);
        Assert.Equal(["TIMESTAMP", "TARGETS", "SAVE_TARGETS", "MULTIPLE", "text/uri-list", "text/x-moz-url", "UTF8_STRING", "STRING", "text/plain", "image/png", "text/html"], reply);
    }

    [Fact]
    public void TargetsReply_of_nothing_is_the_protocol_targets()
    {
        string[] names = ["TIMESTAMP", "TARGETS", "SAVE_TARGETS", "MULTIPLE"];
        Assert.Equal([1u, 2u, 3u, 4u], X11SelectionRules.TargetsReply([], n => (uint)Array.IndexOf(names, n) + 1));
    }

    [Theory]
    [InlineData(262140L, 262040)]
    [InlineData(16777212L, 0x100000)]
    [InlineData(1048676L, 0x100000)]
    [InlineData(1048675L, 0xFFFFF)]
    public void MaxIncrementalChunk_is_the_request_limit_less_100_capped_at_1_MiB(long maxRequest, int chunk)
        => Assert.Equal(chunk, X11SelectionRules.MaxIncrementalChunk(maxRequest));

    [Fact]
    public void Utf8Order_compares_utf8_bytes()
    {
        Assert.True(X11SelectionRules.Utf8Order.Compare("STRING", "image/png") < 0);
        // U+FFFD sorts before U+1F600 in UTF-8 but after its surrogates in UTF-16.
        Assert.True(X11SelectionRules.Utf8Order.Compare("�", "\U0001F600") < 0);
    }
}
