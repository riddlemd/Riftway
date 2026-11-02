using System.Buffers.Binary;

namespace Riftway;

/// <summary>Reads Windows <c>.ico</c> files: an ICONDIR, then entries that are PNGs or BMP DIBs with a trailing AND mask.</summary>
internal static class IcoDecoder
{
    private const int HeaderSize = 6, EntrySize = 16, DibHeaderSize = 40;

    private readonly record struct Entry(int Width, int BitCount, int Size, int Offset);

    /// <summary>The image <c>LoadImage(IMAGE_ICON, size, size)</c> yields, which is what Electron's <c>createFromPath</c> keeps:
    /// the entry whose width is closest to <paramref name="size"/> (the deeper one on a tie), stretched to
    /// <paramref name="size"/> square when it differs. Null when no entry decodes. <paramref name="decodePng"/> handles PNG entries.</summary>
    internal static ImageBitmap? Decode(byte[] ico, int size, Func<byte[], ImageBitmap?> decodePng)
    {
        foreach (var entry in ReadEntries(ico).OrderBy(e => Math.Abs(e.Width - size)).ThenByDescending(e => e.BitCount))
        {
            var data = ico.AsSpan(entry.Offset, entry.Size);
            var bitmap = data is [0x89, 0x50, 0x4E, 0x47, ..] ? decodePng(data.ToArray()) : DecodeDib(data);
            if (bitmap is not { Width: > 0 } decoded) continue;
            return decoded.Width == size && decoded.Height == size ? decoded : ImagePixels.Resize(decoded, size, size);
        }
        return null;
    }

    private static List<Entry> ReadEntries(byte[] ico)
    {
        var entries = new List<Entry>();
        // ICONDIR: reserved 0, type 1 (icon), count.
        if (ico.Length < HeaderSize || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1)
            return entries;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        for (var i = 0; i < count && HeaderSize + (i + 1) * EntrySize <= ico.Length; i++)
        {
            var e = ico.AsSpan(HeaderSize + i * EntrySize, EntrySize);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), offset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (size <= 0 || offset + size > ico.Length) continue;
            // A zero width byte means 256. The height byte is ignored: the entry is picked by width, as LoadImage does.
            entries.Add(new Entry(e[0] == 0 ? 256 : e[0], BinaryPrimitives.ReadUInt16LittleEndian(e[6..]), (int)size, (int)offset));
        }
        return entries;
    }

    // BITMAPINFOHEADER whose height covers the XOR bitmap and the 1bpp AND mask; rows are bottom-up and padded to 4 bytes.
    private static ImageBitmap? DecodeDib(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < DibHeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(dib) < DibHeaderSize) return null;
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]) / 2;
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || compression != 0 || bits is not (1 or 4 or 8 or 24 or 32)) return null;

        var paletteCount = bits <= 8 ? (int)(BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]) is var used and > 0 ? used : 1u << bits) : 0;
        var xorOffset = headerSize + paletteCount * 4;
        var xorStride = (width * bits + 31) / 32 * 4;
        var maskOffset = xorOffset + xorStride * height;
        var maskStride = (width + 31) / 32 * 4;
        if (maskOffset > dib.Length) return null;
        var hasMask = maskOffset + maskStride * height <= dib.Length;

        var bgra = new byte[width * height * 4];
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var row = xorOffset + (height - 1 - y) * xorStride;
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                switch (bits)
                {
                    case 32:
                        dib.Slice(row + x * 4, 4).CopyTo(bgra.AsSpan(o));
                        anyAlpha |= bgra[o + 3] != 0;
                        break;
                    case 24:
                        dib.Slice(row + x * 3, 3).CopyTo(bgra.AsSpan(o));
                        bgra[o + 3] = 255;
                        break;
                    default:
                        var perByte = 8 / bits;
                        var index = (dib[row + x / perByte] >> (8 - bits * (x % perByte + 1))) & ((1 << bits) - 1);
                        if (index >= paletteCount) return null;
                        dib.Slice(headerSize + index * 4, 3).CopyTo(bgra.AsSpan(o));
                        bgra[o + 3] = 255;
                        break;
                }
            }
        }

        // A 32bpp icon with no alpha at all is a plain 24bpp one: transparency then comes from the AND mask.
        var useMask = hasMask && !(bits == 32 && anyAlpha);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var o = (y * width + x) * 4;
            if (bits == 32 && anyAlpha) Premultiply(bgra, o);
            else if (bits == 32) bgra[o + 3] = 255;
            if (useMask && (dib[maskOffset + (height - 1 - y) * maskStride + x / 8] >> (7 - x % 8) & 1) != 0)
                bgra.AsSpan(o, 4).Clear();
        }
        return new ImageBitmap(width, height, bgra);
    }

    private static void Premultiply(byte[] bgra, int o)
    {
        var a = bgra[o + 3];
        for (var c = 0; c < 3; c++) bgra[o + c] = (byte)((bgra[o + c] * a + 127) / 255);
    }
}
