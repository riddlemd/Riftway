using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Oriel.Tests;

public class ClipboardFormatsTests
{
    private static int Offset(string cfHtml, string key)
    {
        var start = cfHtml.IndexOf(key + ":", StringComparison.Ordinal) + key.Length + 1;
        Assert.Equal(10, cfHtml.IndexOf("\r\n", start, StringComparison.Ordinal) - start);
        return int.Parse(cfHtml.AsSpan(start, 10));
    }

    // CF_HTML

    [Fact]
    public void BuildCfHtml_header_offsets_are_byte_offsets_of_the_utf8_form()
    {
        var cf = ClipboardFormats.BuildCfHtml("<b>hi</b>");
        var bytes = Encoding.UTF8.GetBytes(cf);

        Assert.StartsWith("Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:", cf);
        Assert.Equal(105,Offset(cf, "StartHTML"));
        Assert.Equal("<html><body><!--StartFragment--><b>hi</b><!--EndFragment--></body></html>",
            Encoding.UTF8.GetString(bytes, Offset(cf, "StartHTML"), Offset(cf, "EndHTML") - Offset(cf, "StartHTML")));
        Assert.Equal("<b>hi</b>", Encoding.UTF8.GetString(bytes, Offset(cf, "StartFragment"), Offset(cf, "EndFragment") - Offset(cf, "StartFragment")));
        Assert.Equal(bytes.Length, Offset(cf, "EndHTML"));
    }

    [Fact]
    public void BuildCfHtml_counts_bytes_not_chars_for_non_ascii()
    {
        const string Fragment = "<p>héllo € \U0001F600</p>";
        var cf = ClipboardFormats.BuildCfHtml(Fragment);
        var bytes = Encoding.UTF8.GetBytes(cf);

        var start = Offset(cf, "StartFragment");
        var end = Offset(cf, "EndFragment");
        Assert.Equal(Encoding.UTF8.GetByteCount(Fragment), end - start);
        Assert.NotEqual(Fragment.Length, end - start);
        Assert.Equal(Fragment, Encoding.UTF8.GetString(bytes, start, end - start));
        Assert.Equal(bytes.Length, Offset(cf, "EndHTML"));
    }

    [Fact]
    public void FragmentFromCfHtml_round_trips_ascii_and_non_ascii()
    {
        Assert.Equal("<b>hi</b>", ClipboardFormats.FragmentFromCfHtml(ClipboardFormats.BuildCfHtml("<b>hi</b>")));
        Assert.Equal("", ClipboardFormats.FragmentFromCfHtml(ClipboardFormats.BuildCfHtml("")));
        const string Fragment = "<p>héllo € \U0001F600</p>";
        Assert.Equal(Fragment, ClipboardFormats.FragmentFromCfHtml(ClipboardFormats.BuildCfHtml(Fragment)));
    }

    [Fact]
    public void FragmentFromCfHtml_uses_the_offsets_over_the_markers()
    {
        // Offsets pick "BBB" while the markers surround "AAA".
        const string Html = "<!--StartFragment-->AAA<!--EndFragment-->BBB";
        var header = "Version:0.9\r\nStartFragment:{0:D10}\r\nEndFragment:{1:D10}\r\n";
        var prefixLength = string.Format(header, 0, 0).Length;
        var text = string.Format(header, prefixLength + Html.Length - 3, prefixLength + Html.Length) + Html;

        Assert.Equal("BBB", ClipboardFormats.FragmentFromCfHtml(text));
    }

    [Fact]
    public void FragmentFromCfHtml_accepts_an_empty_offset_range_over_the_markers()
    {
        const string Html = "<!--StartFragment-->AAA<!--EndFragment-->";
        var header = "Version:0.9\r\nStartFragment:{0:D10}\r\nEndFragment:{0:D10}\r\n";
        var length = string.Format(header, 0).Length;

        Assert.Equal("", ClipboardFormats.FragmentFromCfHtml(string.Format(header, length) + Html));
    }

    [Fact]
    public void FragmentFromCfHtml_accepts_an_offset_range_ending_at_the_last_byte()
    {
        var header = "Version:0.9\r\nStartFragment:{0:D10}\r\nEndFragment:{1:D10}\r\n";
        var length = string.Format(header, 0, 0).Length;

        Assert.Equal("tail", ClipboardFormats.FragmentFromCfHtml(string.Format(header, length, length + 4) + "tail"));
    }

    [Fact]
    public void FragmentFromCfHtml_tolerates_missing_StartHTML_and_EndHTML()
    {
        var header = "Version:1.0\r\nStartFragment:{0:D10}\r\nEndFragment:{1:D10}\r\n";
        var length = string.Format(header, 0, 0).Length;
        Assert.Equal("xyz", ClipboardFormats.FragmentFromCfHtml(string.Format(header, length, length + 3) + "xyz"));
    }

    [Fact]
    public void FragmentFromCfHtml_handles_bare_line_feeds_and_padding_spaces()
    {
        const string Header = "Version:0.9\nStartFragment: 0000000062 \nEndFragment:0000000065\n";
        Assert.Equal(62, Header.Length);
        Assert.Equal("abc", ClipboardFormats.FragmentFromCfHtml(Header + "abc"));
    }

    [Theory]
    [InlineData("StartFragment:0000000999\r\nEndFragment:0000001000\r\n")] // past the end
    [InlineData("StartFragment:0000000050\r\nEndFragment:0000000040\r\n")] // reversed
    [InlineData("StartFragment:-000000001\r\nEndFragment:0000000040\r\n")] // negative
    [InlineData("StartFragment:abc\r\nEndFragment:def\r\n")] // not numbers
    [InlineData("StartFragment:0000000040\r\n")] // end missing
    [InlineData("EndFragment:0000000040\r\n")] // start missing
    public void FragmentFromCfHtml_falls_back_to_the_markers_when_offsets_are_unusable(string offsets)
    {
        var text = "Version:0.9\r\n" + offsets + "<html><!--StartFragment-->frag<!--EndFragment--></html>";
        Assert.Equal("frag", ClipboardFormats.FragmentFromCfHtml(text));
    }

    [Fact]
    public void FragmentFromCfHtml_falls_back_to_the_markers_when_there_are_no_offsets()
    {
        Assert.Equal("mé", ClipboardFormats.FragmentFromCfHtml("Version:0.9\r\n<!--StartFragment-->mé<!--EndFragment-->"));
    }

    [Fact]
    public void FragmentFromCfHtml_ignores_markers_in_the_wrong_order()
    {
        const string Text = "Version:0.9\r\n<!--EndFragment-->a<!--StartFragment-->";
        Assert.Null(ClipboardFormats.FragmentFromCfHtml(Text));
    }

    [Fact]
    public void FragmentFromCfHtml_ignores_a_start_marker_without_an_end_marker()
    {
        Assert.Null(ClipboardFormats.FragmentFromCfHtml("Version:0.9\r\n<html><!--StartFragment-->abc</html>"));
    }

    [Fact]
    public void FragmentFromCfHtml_takes_the_html_range_when_there_is_no_fragment()
    {
        var header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\n";
        var prefixLength = string.Format(header, 0, 0).Length;
        const string Html = "<html>é</html>";
        var text = string.Format(header, prefixLength, prefixLength + Encoding.UTF8.GetByteCount(Html)) + Html;

        Assert.Equal(Html, ClipboardFormats.FragmentFromCfHtml(text));
    }

    [Fact]
    public void FragmentFromCfHtml_rejects_an_html_range_past_the_end()
    {
        Assert.Null(ClipboardFormats.FragmentFromCfHtml("Version:0.9\r\nStartHTML:0000000030\r\nEndHTML:0000009999\r\n<html></html>"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><!--StartFragment-->a<!--EndFragment--></html>")]
    [InlineData("StartFragment:0000000000\r\nEndFragment:0000000000\r\n")]
    [InlineData("Version:0.9\r\n")]
    public void FragmentFromCfHtml_is_null_when_it_is_not_cf_html(string text)
    {
        Assert.Null(ClipboardFormats.FragmentFromCfHtml(text));
    }

    [Fact]
    public void FragmentFromCfHtml_reads_header_keys_only_before_the_body()
    {
        // The body mentions StartFragment: but the real header has none, so the markers decide.
        var text = "Version:0.9\r\n<html>\r\nStartFragment:0000000000\r\nEndFragment:0000000005\r\n<!--StartFragment-->ok<!--EndFragment--></html>";
        Assert.Equal("ok", ClipboardFormats.FragmentFromCfHtml(text));
    }

    // DIB

    private static byte[] Dib(int headerSize, int width, int height, int bits, int compression, byte[] pixels,
        uint red = 0x00FF0000, uint green = 0x0000FF00, uint blue = 0x000000FF, uint alpha = 0xFF000000, uint colorsUsed = 0)
    {
        var maskBytes = compression == 3 && headerSize == 40 ? 12 : 0;
        var dib = new byte[headerSize + maskBytes + pixels.Length];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), (short)bits);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), colorsUsed);
        if (compression == 3)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), red);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), green);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), blue);
            if (headerSize > 40) BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), alpha);
        }
        pixels.CopyTo(dib, headerSize + maskBytes);
        return dib;
    }

    [Fact]
    public void BuildDibV5_writes_the_v5_header_and_bottom_up_straight_alpha_pixels()
    {
        // 2x2, top row first, premultiplied BGRA. Row 0: opaque red, half-transparent (premultiplied) blue.
        // Row 1: fully transparent, opaque (10,20,30).
        byte[] bgra =
        [
            0, 0, 255, 255,   64, 0, 0, 128,
            0, 0, 0, 0,       10, 20, 30, 255,
        ];
        var dib = ClipboardFormats.BuildDibV5(new ImageBitmap(2, 2, bgra));

        Assert.Equal(124 + 16, dib.Length);
        Assert.Equal(124, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(12)));
        Assert.Equal(32, BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16)));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(20)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(24)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(28)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(36)));
        Assert.Equal(0x00FF0000u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(40)));
        Assert.Equal(0x0000FF00u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(44)));
        Assert.Equal(0x000000FFu, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(48)));
        Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(52)));
        Assert.Equal(0x73524742u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(56)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(108)));
        Assert.All(dib[60..108], b => Assert.Equal(0, b));
        Assert.All(dib[112..124], b => Assert.Equal(0, b));
        // Bottom row first; 64 at alpha 128 un-premultiplies to 128.
        Assert.Equal(new byte[] { 0, 0, 0, 0, 10, 20, 30, 255, 0, 0, 255, 255, 128, 0, 0, 128 }, dib[124..]);
    }

    [Fact]
    public void BuildDibV5_caps_an_out_of_range_premultiplied_channel_at_255()
    {
        var dib = ClipboardFormats.BuildDibV5(new ImageBitmap(1, 1, [200, 100, 1, 100]));
        Assert.Equal(new byte[] { 255, 255, 3, 100 }, dib[124..]);
    }

    [Fact]
    public void BuildDibV5_rounds_the_unpremultiplied_channel()
    {
        // 1 * 255 / 2 = 127.5 rounds up; 1 * 255 / 3 = 85 exactly; 2 * 255 / 5 = 102.
        Assert.Equal(128, ClipboardFormats.Unpremultiply(1, 2));
        Assert.Equal(85, ClipboardFormats.Unpremultiply(1, 3));
        Assert.Equal(102, ClipboardFormats.Unpremultiply(2, 5));
        Assert.Equal(0, ClipboardFormats.Unpremultiply(0, 0));
        Assert.Equal(0, ClipboardFormats.Unpremultiply(5, 0));
    }

    [Fact]
    public void Premultiply_rounds_to_nearest()
    {
        Assert.Equal(0, ClipboardFormats.Premultiply(255, 0));
        Assert.Equal(255, ClipboardFormats.Premultiply(255, 255));
        Assert.Equal(128, ClipboardFormats.Premultiply(255, 128)); // 128.49
        Assert.Equal(1, ClipboardFormats.Premultiply(2, 128));     // 1.004
        Assert.Equal(1, ClipboardFormats.Premultiply(3, 85));      // 1.0
        Assert.Equal(0, ClipboardFormats.Premultiply(1, 127));     // 0.498 rounds down
        Assert.Equal(1, ClipboardFormats.Premultiply(1, 128));     // 0.502 rounds up
    }

    [Fact]
    public void ParseDib_round_trips_BuildDibV5_including_semi_transparent_pixels()
    {
        byte[] bgra =
        [
            0, 0, 255, 255,   64, 0, 0, 128,   0, 32, 0, 128,
            0, 0, 0, 0,       10, 20, 30, 255, 0, 0, 0, 64,
        ];
        var parsed = ClipboardFormats.ParseDib(ClipboardFormats.BuildDibV5(new ImageBitmap(3, 2, bgra)));

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Value.Width);
        Assert.Equal(2, parsed.Value.Height);
        Assert.Equal(bgra, parsed.Value.Bgra);
    }

    [Fact]
    public void ParseDib_reads_bottom_up_with_positive_height_and_top_down_with_negative()
    {
        byte[] rowA = [1, 2, 3, 255, 4, 5, 6, 255];
        byte[] rowB = [7, 8, 9, 255, 10, 11, 12, 255];

        var bottomUp = ClipboardFormats.ParseDib(Dib(40, 2, 2, 32, 3, [.. rowB, .. rowA]));
        var topDown = ClipboardFormats.ParseDib(Dib(40, 2, -2, 32, 3, [.. rowA, .. rowB]));

        Assert.Equal([.. rowA, .. rowB], bottomUp!.Value.Bgra);
        Assert.Equal([.. rowA, .. rowB], topDown!.Value.Bgra);
        Assert.Equal(2, bottomUp.Value.Height);
        Assert.Equal(2, topDown.Value.Height);
    }

    [Fact]
    public void ParseDib_reads_24bpp_rows_padded_to_four_bytes_as_opaque()
    {
        // Width 3: 9 pixel bytes padded to 12. Bottom-up, so the first stored row is the image's bottom row.
        byte[] bottom = [1, 2, 3, 4, 5, 6, 7, 8, 9, 0xEE, 0xEE, 0xEE];
        byte[] top = [11, 12, 13, 14, 15, 16, 17, 18, 19, 0xEE, 0xEE, 0xEE];
        var parsed = ClipboardFormats.ParseDib(Dib(40, 3, 2, 24, 0, [.. bottom, .. top]));

        Assert.Equal(
            new byte[] { 11, 12, 13, 255, 14, 15, 16, 255, 17, 18, 19, 255, 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255 },
            parsed!.Value.Bgra);
    }

    [Fact]
    public void ParseDib_reads_24bpp_width_four_without_padding()
    {
        var pixels = new byte[12];
        for (var i = 0; i < 12; i++) pixels[i] = (byte)(i + 1);
        var parsed = ClipboardFormats.ParseDib(Dib(40, 4, 1, 24, 0, pixels));

        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 }, parsed!.Value.Bgra);
    }

    [Fact]
    public void ParseDib_takes_BI_RGB_32bpp_with_all_zero_alpha_as_opaque()
    {
        var parsed = ClipboardFormats.ParseDib(Dib(40, 2, 1, 32, 0, [10, 20, 30, 0, 40, 50, 60, 0]));
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, parsed!.Value.Bgra);
    }

    [Fact]
    public void ParseDib_keeps_BI_RGB_32bpp_alpha_when_any_pixel_has_it_and_premultiplies()
    {
        var parsed = ClipboardFormats.ParseDib(Dib(40, 2, 1, 32, 0, [255, 255, 255, 128, 100, 100, 100, 0]));
        Assert.Equal(new byte[] { 128, 128, 128, 128, 0, 0, 0, 0 }, parsed!.Value.Bgra);
    }

    [Fact]
    public void ParseDib_reads_BI_BITFIELDS_in_an_info_header_with_masks_after_the_header()
    {
        var parsed = ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 3, [10, 20, 30, 255]));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, parsed!.Value.Bgra);
    }

    [Fact]
    public void ParseDib_takes_info_bitfields_with_all_zero_alpha_as_opaque()
    {
        var parsed = ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 3, [10, 20, 30, 0]));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, parsed!.Value.Bgra);
    }

    [Theory]
    [InlineData(108)]
    [InlineData(124)]
    public void ParseDib_reads_v4_and_v5_headers_with_the_alpha_mask_trusted(int headerSize)
    {
        var parsed = ClipboardFormats.ParseDib(Dib(headerSize, 2, 1, 32, 3, [200, 100, 50, 128, 1, 2, 3, 0]));
        // Fully transparent stays transparent: the alpha mask is declared, so zero alpha is real.
        Assert.Equal(new byte[] { 100, 50, 25, 128, 0, 0, 0, 0 }, parsed!.Value.Bgra);
    }

    [Theory]
    [InlineData(108)]
    [InlineData(124)]
    public void ParseDib_reads_v4_and_v5_bitfields_without_an_alpha_mask_as_opaque(int headerSize)
    {
        var parsed = ClipboardFormats.ParseDib(Dib(headerSize, 1, 1, 32, 3, [10, 20, 30, 77], alpha: 0));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, parsed!.Value.Bgra);
    }

    [Theory]
    [InlineData(108)]
    [InlineData(124)]
    public void ParseDib_reads_v4_and_v5_headers_with_BI_RGB_24bpp(int headerSize)
    {
        var parsed = ClipboardFormats.ParseDib(Dib(headerSize, 1, 1, 24, 0, [1, 2, 3, 0]));
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, parsed!.Value.Bgra);
    }

    [Fact]
    public void ParseDib_accepts_a_buffer_with_trailing_bytes()
    {
        var parsed = ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 0, [1, 2, 3, 255, 9, 9, 9, 9]));
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, parsed!.Value.Bgra);
    }

    [Fact]
    public void ParseDib_rejects_unsupported_or_malformed_blocks()
    {
        byte[] px = [1, 2, 3, 255];

        Assert.Null(ClipboardFormats.ParseDib([]));
        Assert.Null(ClipboardFormats.ParseDib(new byte[39]));
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 0, px)[..43])); // pixels short
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 0, px)[..40])); // no pixels
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 2, 1, 32, 0, px))); // one pixel short of two
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 2, 32, 0, px))); // two rows, one stored
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 1, px))); // BI_RLE8
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 4, px))); // BI_JPEG
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 24, 3, [1, 2, 3, 0]))); // bitfields 24bpp
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 8, 0, px))); // paletted 8bpp
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 16, 0, px))); // 16bpp
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 0, px, colorsUsed: 2))); // colour table
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 0, 1, 32, 0, []))); // zero width
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, -1, 1, 32, 0, px))); // negative width
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 0, 32, 0, []))); // zero height
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, int.MinValue, 32, 0, px))); // unrepresentable height
    }

    [Fact]
    public void ParseDib_rejects_unknown_header_sizes_and_headers_cut_short()
    {
        byte[] px = [1, 2, 3, 255];
        var dib = Dib(40, 1, 1, 32, 0, px);
        BinaryPrimitives.WriteInt32LittleEndian(dib, 12); // BITMAPCOREHEADER
        Assert.Null(ClipboardFormats.ParseDib(dib));
        BinaryPrimitives.WriteInt32LittleEndian(dib, 56);
        Assert.Null(ClipboardFormats.ParseDib(dib));
        BinaryPrimitives.WriteInt32LittleEndian(dib, 124); // claims V5 but is 44 bytes
        Assert.Null(ClipboardFormats.ParseDib(dib));
        Assert.Null(ClipboardFormats.ParseDib(Dib(124, 1, 1, 32, 0, [])[..123]));
    }

    [Fact]
    public void ParseDib_rejects_non_standard_bitfield_masks()
    {
        byte[] px = [1, 2, 3, 255];
        Assert.Null(ClipboardFormats.ParseDib(Dib(124, 1, 1, 32, 3, px, red: 0x000000FF)));
        Assert.Null(ClipboardFormats.ParseDib(Dib(124, 1, 1, 32, 3, px, green: 0x00FF0000)));
        Assert.Null(ClipboardFormats.ParseDib(Dib(124, 1, 1, 32, 3, px, blue: 0x0000FF00)));
        Assert.Null(ClipboardFormats.ParseDib(Dib(124, 1, 1, 32, 3, px, alpha: 0x000000FF)));
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 3, px, red: 0x000000FF)));
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 3, px, green: 0)));
        Assert.Null(ClipboardFormats.ParseDib(Dib(40, 1, 1, 32, 3, px, blue: 0)));
    }

    [Fact]
    public void ParseDib_rejects_an_info_bitfields_block_cut_inside_the_masks()
    {
        var dib = Dib(40, 1, 1, 32, 3, []);
        Assert.Equal(52, dib.Length);
        Assert.Null(ClipboardFormats.ParseDib(dib[..51]));
        Assert.Null(ClipboardFormats.ParseDib(dib[..45]));
    }

    [Fact]
    public void ParseDib_ignores_declared_planes_and_sizeimage()
    {
        // Real writers leave biSizeImage 0 for BI_RGB; the stride rule decides the size.
        var dib = Dib(40, 1, 1, 32, 0, [1, 2, 3, 255]);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), 0);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, ClipboardFormats.ParseDib(dib)!.Value.Bgra);
    }

    // MIME mapping

    [Theory]
    [InlineData("CF_UNICODETEXT", "text/plain")]
    [InlineData("CF_TEXT", "text/plain")]
    [InlineData("HTML Format", "text/html")]
    [InlineData("Rich Text Format", "text/rtf")]
    [InlineData("PNG", "image/png")]
    [InlineData("CF_DIB", "image/png")]
    [InlineData("CF_DIBV5", "image/png")]
    [InlineData("UniformResourceLocatorW", null)]
    [InlineData("MyFormat", null)]
    [InlineData("", null)]
    public void MimeOf_name(string name, string? mime)
    {
        Assert.Equal(mime, ClipboardFormats.MimeOf(name));
    }

    [Theory]
    [InlineData(1u, null, "text/plain")]
    [InlineData(13u, null, "text/plain")]
    [InlineData(8u, null, "image/png")]
    [InlineData(17u, null, "image/png")]
    [InlineData(2u, null, null)]
    [InlineData(49400u, "HTML Format", "text/html")]
    [InlineData(49401u, "Rich Text Format", "text/rtf")]
    [InlineData(49402u, "PNG", "image/png")]
    [InlineData(49403u, "UniformResourceLocatorW", null)]
    [InlineData(49404u, null, null)]
    public void MimeOf_id(uint id, string? name, string? mime)
    {
        Assert.Equal(mime, ClipboardFormats.MimeOf(id, name));
    }

    [Fact]
    public void MimeOf_id_does_not_map_the_registered_name_of_a_predefined_id_differently()
    {
        Assert.Equal("text/plain", ClipboardFormats.MimeOf(13, "MyFormat"));
    }

    [Fact]
    public void WindowsNamesOf_lists_the_formats_best_first()
    {
        Assert.Equal(["CF_UNICODETEXT", "CF_TEXT"], ClipboardFormats.WindowsNamesOf("text/plain"));
        Assert.Equal(["HTML Format"], ClipboardFormats.WindowsNamesOf("text/html"));
        Assert.Equal(["Rich Text Format"], ClipboardFormats.WindowsNamesOf("text/rtf"));
        Assert.Equal(["PNG", "CF_DIBV5", "CF_DIB"], ClipboardFormats.WindowsNamesOf("image/png"));
        Assert.Empty(ClipboardFormats.WindowsNamesOf("image/jpeg"));
        Assert.Empty(ClipboardFormats.WindowsNamesOf("MyFormat"));
    }

    [Fact]
    public void PredefinedId_maps_cf_names_only()
    {
        Assert.Equal(1u, ClipboardFormats.PredefinedId("CF_TEXT"));
        Assert.Equal(8u, ClipboardFormats.PredefinedId("CF_DIB"));
        Assert.Equal(13u, ClipboardFormats.PredefinedId("CF_UNICODETEXT"));
        Assert.Equal(17u, ClipboardFormats.PredefinedId("CF_DIBV5"));
        Assert.Null(ClipboardFormats.PredefinedId("PNG"));
        Assert.Null(ClipboardFormats.PredefinedId("HTML Format"));
    }

    [Fact]
    public void AvailableMimes_lists_each_type_once_in_a_stable_order()
    {
        (uint, string?)[] formats =
        [
            (17, null), (8, null), (49402, "PNG"), (49403, "UniformResourceLocatorW"),
            (49401, "Rich Text Format"), (13, null), (1, null), (49400, "HTML Format"), (49500, "Custom"), (2, null),
        ];

        Assert.Equal(["text/plain", "text/html", "text/rtf", "image/png"], ClipboardFormats.AvailableMimes(formats));
        Assert.Equal(["text/html"], ClipboardFormats.AvailableMimes([(49400, "HTML Format"), (49400, "HTML Format")]));
        Assert.Equal(["text/plain", "image/png"], ClipboardFormats.AvailableMimes([(8, null), (1, null)]));
        Assert.Empty(ClipboardFormats.AvailableMimes([(49403, "UniformResourceLocatorW"), (49500, null)]));
        Assert.Empty(ClipboardFormats.AvailableMimes([]));
    }
}
