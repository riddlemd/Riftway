using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The shell's icon for a file, as Chromium's <c>IconLoader</c> asks for it.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsFileIcon
{
    private const uint ShgfiIcon = 0x100, ShgfiSmallIcon = 0x1, ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;

    /// <summary>The icon for <paramref name="query"/>, 16 px when <paramref name="small"/> else 32 px, as premultiplied
    /// BGRA top row first; null if the shell has none. With <paramref name="byType"/> the query is an extension and the
    /// file is not read.</summary>
    public static (byte[] Bgra, int Width, int Height)? Load(string query, bool small, bool byType)
    {
        CoInitializeEx(null, 0 /* COINIT_MULTITHREADED */);
        FileInfo info;
        var flags = ShgfiIcon | (small ? ShgfiSmallIcon : 0) | (byType ? ShgfiUseFileAttributes : 0);
        nint ok;
        fixed (char* q = query) ok = SHGetFileInfoW(q, byType ? FileAttributeNormal : 0, &info, (uint)sizeof(FileInfo), flags);
        if (ok == 0 || info.Icon == 0) return null;
        try { return Pixels(info.Icon); }
        finally { DestroyIcon(info.Icon); }
    }

    private static (byte[] Bgra, int Width, int Height)? Pixels(nint icon)
    {
        IconInfo info;
        if (GetIconInfo(icon, &info) == 0) return null;
        try
        {
            if (info.Color == 0) return null;
            BitmapHeader bitmap;
            if (GetObjectW(info.Color, sizeof(BitmapHeader), &bitmap) == 0) return null;
            var (w, h) = (bitmap.Width, bitmap.Height);
            var color = Bits(info.Color, w, h);
            if (color is null) return null;
            if (!IconPixels.HasAlpha(color))
            {
                // Icons without an alpha channel carry transparency in the AND mask.
                var mask = info.Mask != 0 ? Bits(info.Mask, w, h) : null;
                for (var i = 0; i < color.Length; i += 4) color[i + 3] = mask is not null && mask[i] != 0 ? (byte)0 : (byte)255;
            }
            IconPixels.Premultiply(color);
            return (color, w, h);
        }
        finally
        {
            if (info.Color != 0) DeleteObject(info.Color);
            if (info.Mask != 0) DeleteObject(info.Mask);
        }
    }

    private static byte[]? Bits(nint hbitmap, int w, int h)
    {
        var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = w, Height = -h, Planes = 1, BitCount = 32 };
        // Room for the two-entry colour table GetDIBits writes for a monochrome mask.
        var withTable = stackalloc byte[sizeof(BitmapInfoHeader) + 8];
        *(BitmapInfoHeader*)withTable = header;
        var pixels = new byte[w * h * 4];
        var dc = GetDC(0);
        try
        {
            fixed (byte* data = pixels)
                return GetDIBits(dc, hbitmap, 0, (uint)h, data, withTable, 0) == 0 ? null : pixels;
        }
        finally { ReleaseDC(0, dc); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        public fixed char DisplayName[260];
        public fixed char TypeName[80];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("shell32.dll")] private static extern nint SHGetFileInfoW(char* path, uint attributes, FileInfo* info, uint size, uint flags);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(void* reserved, uint flags);
    [DllImport("user32.dll")] private static extern int DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetIconInfo(nint icon, IconInfo* info);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, void* buffer);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, void* info, uint usage);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
}

/// <summary>Pixel arithmetic for icons, kept apart so it is testable on every OS.</summary>
internal static class IconPixels
{
    internal static bool HasAlpha(ReadOnlySpan<byte> bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return true;
        return false;
    }

    internal static void Premultiply(Span<byte> bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 255) continue;
            bgra[i] = (byte)((bgra[i] * a + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 127) / 255);
        }
    }
}
