using System.Buffers.Binary;

namespace Oriel.Tests;

public sealed class IcoDecoderTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static ImageBitmap? NoPng(byte[] _) => null;

    // width/height byte 0 means 256, as in a real ICONDIRENTRY.
    internal static byte[] Ico(params (int W, int H, int Bits, byte[] Data)[] entries)
    {
        var bytes = new List<byte> { 0, 0, 1, 0, (byte)entries.Length, 0 };
        var offset = 6 + entries.Length * 16;
        foreach (var (w, h, bits, data) in entries)
        {
            bytes.AddRange([(byte)w, (byte)h, 0, 0, 1, 0, (byte)bits, 0]);
            bytes.AddRange(BitConverter.GetBytes((uint)data.Length));
            bytes.AddRange(BitConverter.GetBytes((uint)offset));
            offset += data.Length;
        }
        foreach (var e in entries) bytes.AddRange(e.Data);
        return [.. bytes];
    }

    // Rows are given top first; the DIB stores them bottom-up, each padded to 4 bytes.
    internal static byte[] Dib(int w, int h, int bits, byte[][] xorRows, byte[][]? maskRows, byte[]? palette = null, int heightField = -1)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(40));
        bytes.AddRange(BitConverter.GetBytes(w));
        bytes.AddRange(BitConverter.GetBytes(heightField >= 0 ? heightField : h * 2));
        bytes.AddRange([1, 0, (byte)bits, 0]);
        bytes.AddRange(new byte[24]);
        if (palette is not null) bytes.AddRange(palette);
        foreach (var rows in new[] { xorRows, maskRows })
        {
            if (rows is null) continue;
            foreach (var row in rows.Reverse())
            {
                bytes.AddRange(row);
                bytes.AddRange(new byte[(4 - row.Length % 4) % 4]);
            }
        }
        return [.. bytes];
    }

    internal static byte[] Px(params byte[] bytes) => bytes;

    [Fact]
    public void Decode_32BppWithAlpha_PremultipliesAndOrdersTopRowFirst()
    {
        var dib = Dib(2, 2, 32,
            [Px(200, 100, 50, 255, 100, 50, 0, 128), Px(10, 20, 30, 0, 255, 255, 255, 51)],
            [Px(0), Px(0)]);
        var r = IcoDecoder.Decode(Ico((2, 2, 32, dib)), 2, NoPng)!.Value;
        Assert.Equal((2, 2), (r.Width, r.Height));
        // (c * a + 127) / 255: 100*128 -> 50, 50*128 -> 25; 255*51 -> 51; the alpha-0 pixel's colour is dropped.
        Assert.Equal(new byte[] { 200, 100, 50, 255, 50, 25, 0, 128, 0, 0, 0, 0, 51, 51, 51, 51 }, r.Bgra);
    }

    [Fact]
    public void Decode_32BppWithAlpha_IgnoresTheAndMask()
    {
        var dib = Dib(1, 1, 32, [Px(8, 8, 8, 255)], [Px(0x80)]);
        Assert.Equal(new byte[] { 8, 8, 8, 255 }, IcoDecoder.Decode(Ico((1, 1, 32, dib)), 1, NoPng)!.Value.Bgra);
    }

    [Fact]
    public void Decode_32BppAllZeroAlpha_TakesTransparencyFromTheMask()
    {
        var dib = Dib(2, 2, 32, [Px(1, 2, 3, 0, 4, 5, 6, 0), Px(7, 8, 9, 0, 10, 11, 12, 0)], [Px(0x40), Px(0x80)]);
        // Mask bit 7 is the left pixel, bit 6 the right one; a set bit is transparent.
        Assert.Equal(new byte[] { 1, 2, 3, 255, 0, 0, 0, 0, 0, 0, 0, 0, 10, 11, 12, 255 }, IcoDecoder.Decode(Ico((2, 2, 32, dib)), 2, NoPng)!.Value.Bgra);
    }

    [Fact]
    public void Decode_8BppWithPalette_LooksUpColoursAndAppliesMask()
    {
        // ClrUsed 0 means a full 1 << 8 palette follows the header.
        var palette = new byte[] { 0, 0, 0, 0, 10, 20, 30, 0, 40, 50, 60, 0 }.Concat(new byte[(256 - 3) * 4]).ToArray();
        var dib = Dib(3, 3, 8, [Px(1, 2, 0), Px(2, 1, 1), Px(0, 0, 0)], [Px(0b0010_0000), Px(0b1000_0000), Px(0)], palette);
        var r = IcoDecoder.Decode(Ico((3, 3, 8, dib)), 3, NoPng)!.Value;
        Assert.Equal(new byte[]
        {
            10, 20, 30, 255, 40, 50, 60, 255, 0, 0, 0, 0,
            0, 0, 0, 0, 10, 20, 30, 255, 10, 20, 30, 255,
            0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255,
        }, r.Bgra);
    }

    [Fact]
    public void Decode_4BppAndClrUsed_ReadsNibblesAndTheDeclaredPalette()
    {
        var dib = Dib(3, 3, 4, [Px(0x12, 0x00), Px(0, 0), Px(0, 0)], [Px(0), Px(0), Px(0)], new byte[] { 0, 0, 0, 0, 9, 8, 7, 0, 1, 2, 3, 0 });
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), 3);
        var r = IcoDecoder.Decode(Ico((3, 3, 4, dib)), 3, NoPng)!.Value;
        Assert.Equal(new byte[] { 9, 8, 7, 255, 1, 2, 3, 255, 0, 0, 0, 255 }, r.Bgra[..12]);
    }

    [Fact]
    public void Decode_1Bpp_ReadsBitsLeftToRight()
    {
        var dib = Dib(3, 3, 1, [Px(0b1010_0000), Px(0), Px(0)], [Px(0), Px(0), Px(0)], new byte[] { 0, 0, 0, 0, 255, 255, 255, 0 });
        var r = IcoDecoder.Decode(Ico((3, 3, 1, dib)), 3, NoPng)!.Value;
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255 }, r.Bgra[..12]);
    }

    [Fact]
    public void Decode_24Bpp_IsOpaqueExceptWhereMasked()
    {
        var dib = Dib(2, 2, 24, [Px(1, 2, 3, 4, 5, 6), Px(7, 8, 9, 10, 11, 12)], [Px(0x80), Px(0)]);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 }, IcoDecoder.Decode(Ico((2, 2, 24, dib)), 2, NoPng)!.Value.Bgra);
    }

    [Fact]
    public void Decode_MissingMask_LeavesPixelsOpaque()
    {
        var dib = Dib(1, 1, 24, [Px(1, 2, 3)], null, heightField: 2);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, IcoDecoder.Decode(Ico((1, 1, 24, dib)), 1, NoPng)!.Value.Bgra);
    }

    [Fact]
    public void Decode_PngEntry_HandsTheEntryBytesToThePngDecoder()
    {
        byte[]? seen = null;
        var expected = new ImageBitmap(1, 1, [9, 9, 9, 9]);
        var r = IcoDecoder.Decode(Ico((1, 1, 32, PngMagic)), 1, b => { seen = b; return expected; });
        Assert.Equal(PngMagic, seen);
        Assert.Equal(expected, r);
    }

    [Fact]
    public void Decode_PicksTheEntryClosestToTheRequestedSize()
    {
        var small = Dib(1, 1, 24, [Px(1, 1, 1)], [Px(0)]);
        var mid = Dib(2, 2, 24, [Px(2, 2, 2, 2, 2, 2), Px(2, 2, 2, 2, 2, 2)], [Px(0), Px(0)]);
        var big = Dib(4, 4, 24, Enumerable.Repeat(Enumerable.Repeat((byte)3, 12).ToArray(), 4).ToArray(), Enumerable.Repeat(Px(0), 4).ToArray());
        var ico = Ico((1, 1, 24, small), (4, 4, 24, big), (2, 2, 24, mid));
        Assert.Equal(2, IcoDecoder.Decode(ico, 2, NoPng)!.Value.Bgra[0]);
        Assert.Equal(3, IcoDecoder.Decode(ico, 5, NoPng)!.Value.Bgra[0]);
    }

    [Fact]
    public void Decode_EqualSizes_PrefersTheDeeperEntry()
    {
        var shallow = Dib(1, 1, 1, [Px(0)], [Px(0)], new byte[] { 7, 7, 7, 0, 0, 0, 0, 0 });
        var deep = Dib(1, 1, 24, [Px(5, 5, 5)], [Px(0)]);
        Assert.Equal(5, IcoDecoder.Decode(Ico((1, 1, 1, shallow), (1, 1, 24, deep)), 1, NoPng)!.Value.Bgra[0]);
        Assert.Equal(5, IcoDecoder.Decode(Ico((1, 1, 24, deep), (1, 1, 1, shallow)), 1, NoPng)!.Value.Bgra[0]);
    }

    [Fact]
    public void Decode_ZeroWidthAndHeightBytes_MeanTwoFiftySix()
    {
        var small = Dib(1, 1, 24, [Px(1, 1, 1)], [Px(0)]);
        var called = false;
        var r = IcoDecoder.Decode(Ico((1, 1, 24, small), (0, 0, 32, PngMagic)), 256, b => { called = true; return new ImageBitmap(256, 256, new byte[256 * 256 * 4]); })!.Value;
        Assert.True(called);
        Assert.Equal((256, 256), (r.Width, r.Height));
    }

    [Fact]
    public void Decode_SmallerThanRequested_IsStretchedToTheRequestedSquare()
    {
        var dib = Dib(2, 2, 24, [Px(80, 80, 80, 80, 80, 80), Px(80, 80, 80, 80, 80, 80)], [Px(0), Px(0)]);
        var r = IcoDecoder.Decode(Ico((2, 2, 24, dib)), 8, NoPng)!.Value;
        Assert.Equal((8, 8, 8 * 8 * 4), (r.Width, r.Height, r.Bgra.Length));
        Assert.Equal(new byte[] { 80, 80, 80, 255 }, r.Bgra[..4]);
    }

    [Fact]
    public void Decode_RightWidthButWrongHeight_IsStillStretchedToASquare()
    {
        var dib = Dib(4, 2, 24, [new byte[12], new byte[12]], [Px(0), Px(0)]);
        var r = IcoDecoder.Decode(Ico((4, 2, 24, dib)), 4, NoPng)!.Value;
        Assert.Equal((4, 4), (r.Width, r.Height));
    }

    [Fact]
    public void Decode_32BppHalfAlpha_RoundsThePremultipliedColourToNearest()
    {
        // 10 * 200 / 255 = 7.84.
        var dib = Dib(1, 1, 32, [Px(10, 10, 10, 200)], [Px(0)]);
        Assert.Equal(new byte[] { 8, 8, 8, 200 }, IcoDecoder.Decode(Ico((1, 1, 32, dib)), 1, NoPng)!.Value.Bgra);
    }

    [Fact]
    public void Decode_ClrUsedOfOne_ReadsASinglePaletteEntry()
    {
        var dib = Dib(1, 1, 8, [Px(0)], [Px(0)], new byte[] { 5, 6, 7, 0 });
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), 1);
        Assert.Equal(new byte[] { 5, 6, 7, 255 }, IcoDecoder.Decode(Ico((1, 1, 8, dib)), 1, NoPng)!.Value.Bgra);
    }

    [Fact]
    public void Decode_UndecodableBestEntry_FallsBackToTheNextClosest()
    {
        var good = Dib(1, 1, 24, [Px(6, 6, 6)], [Px(0)]);
        var r = IcoDecoder.Decode(Ico((2, 2, 32, PngMagic), (1, 1, 24, good)), 2, NoPng);
        Assert.Equal(2, r!.Value.Width);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 0, 1 })]
    [InlineData(new byte[] { 1, 0, 1, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 2, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 0, 24, 0, 8, 0, 0, 0, 22, 0, 0, 0 })]
    public void Decode_NotAUsableIco_ReturnsNull(byte[] ico) => Assert.Null(IcoDecoder.Decode(ico, 256, NoPng));

    [Fact]
    public void Decode_EntryRunningPastTheFile_IsSkipped()
    {
        var good = Dib(1, 1, 24, [Px(6, 6, 6)], [Px(0)]);
        var ico = Ico((1, 1, 24, good), (1, 1, 24, good));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 8), (uint)ico.Length);
        Assert.Equal(6, IcoDecoder.Decode(ico, 1, NoPng)!.Value.Bgra[0]);
    }

    [Fact]
    public void Decode_TruncatedDirectory_StopsAtTheLastCompleteEntry()
    {
        var good = Dib(1, 1, 24, [Px(6, 6, 6)], [Px(0)]);
        var ico = Ico((1, 1, 24, good));
        ico[4] = 3;
        Assert.NotNull(IcoDecoder.Decode(ico, 1, NoPng));
    }

    [Theory]
    [InlineData(0, 1, 24, 0)]
    [InlineData(1, 0, 24, 0)]
    [InlineData(1, 1, 16, 0)]
    [InlineData(1, 1, 24, 1)]
    public void Decode_UnsupportedDib_ReturnsNull(int w, int h, int bits, int compression)
    {
        var dib = Dib(Math.Max(w, 1), Math.Max(h, 1), bits, [Px(1, 2, 3, 4)], [Px(0)], heightField: h * 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), w);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), (uint)compression);
        Assert.Null(IcoDecoder.Decode(Ico((1, 1, bits, dib)), 1, NoPng));
    }

    [Fact]
    public void Decode_PaletteIndexOutOfRange_ReturnsNull()
    {
        var dib = Dib(1, 1, 4, [Px(0x20)], [Px(0)], new byte[] { 0, 0, 0, 0, 1, 1, 1, 0 });
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), 2);
        Assert.Null(IcoDecoder.Decode(Ico((1, 1, 4, dib)), 1, NoPng));
    }

    [Fact]
    public void Decode_DibShorterThanItsHeader_ReturnsNull()
    {
        var dib = new byte[30];
        (dib[0], dib[4], dib[8], dib[14]) = (40, 1, 2, 8);
        Assert.Null(IcoDecoder.Decode(Ico((1, 1, 8, dib)), 1, NoPng));
    }

    [Fact]
    public void Decode_PixelDataCutOff_ReturnsNull()
    {
        var dib = Dib(2, 2, 32, [Px(1, 1, 1, 1, 1, 1, 1, 1), Px(1, 1, 1, 1, 1, 1, 1, 1)], null);
        Assert.Null(IcoDecoder.Decode(Ico((2, 2, 32, dib[..50])), 2, NoPng));
    }
}
