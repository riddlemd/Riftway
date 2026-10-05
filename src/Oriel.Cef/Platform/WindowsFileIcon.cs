using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>File icons as Chromium's <c>IconLoader</c> (<c>icon_loader_win.cc</c>) and <c>UtilReadIcon</c> read them.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsFileIcon
{
    private const uint ShgfiIcon = 0x100, ShgfiSmallIcon = 0x1, ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint PageReadonly = 0x02, SecImageNoExecute = 0x11000000;
    private const uint FileMapRead = 0x4;
    private const int RtGroupIcon = 14, ImageIcon = 1, LogPixelsX = 88;

    /// <summary>Chromium's <c>GetIconForFileExtension</c>: the shell's icon for the type <paramref name="extension"/>
    /// (which may be empty) without reading any file, the system small icon when <paramref name="small"/> else the large
    /// one, as premultiplied BGRA top row first; null if the shell has none.</summary>
    public static (byte[] Bgra, int Width, int Height)? LoadForExtension(string extension, bool small)
    {
        CoInitializeEx(null, 0 /* COINIT_MULTITHREADED */);
        FileInfo info;
        nint ok;
        fixed (char* q = extension)
            ok = SHGetFileInfoW(q, FileAttributeNormal, &info, (uint)sizeof(FileInfo), ShgfiIcon | (small ? ShgfiSmallIcon : 0) | ShgfiUseFileAttributes);
        if (ok == 0 || info.Icon == 0) return null;
        try { return Pixels(info.Icon); }
        finally { DestroyIcon(info.Icon); }
    }

    /// <summary>Chromium's <c>UtilReadIcon</c>: maps <paramref name="path"/> as an executable image and loads its first
    /// <c>RT_GROUP_ICON</c> at <paramref name="size"/> pixels square. Null when the file cannot be opened, is not a PE image
    /// (an .ico file is not) or has no icon.</summary>
    public static (byte[] Bgra, int Width, int Height)? LoadFromFile(string path, int size)
    {
        nint file;
        fixed (char* p = path) file = CreateFileW(p, GenericRead, FileShareAll, null, OpenExisting, 0, 0);
        if (file == -1) return null;
        try
        {
            var mapping = CreateFileMappingW(file, null, PageReadonly | SecImageNoExecute, 0, 0, null);
            if (mapping == 0) return null;
            try
            {
                var view = MapViewOfFile(mapping, FileMapRead, 0, 0, 0);
                if (view is null) return null;
                try { return FirstIcon((nint)view, size); }
                finally { UnmapViewOfFile(view); }
            }
            finally { CloseHandle(mapping); }
        }
        finally { CloseHandle(file); }
    }

    /// <summary><c>display::win::GetDPIScale()</c> without a forced scale: the system DPI over 96.</summary>
    public static float SystemDpiScale()
    {
        var dc = GetDC(0);
        try { return GetDeviceCaps(dc, LogPixelsX) / 96f; }
        finally { ReleaseDC(0, dc); }
    }

    private static (byte[] Bgra, int Width, int Height)? FirstIcon(nint module, int size)
    {
        nint id = 0;
        // The callback stops at the first name, so EnumResourceNamesW reports failure even when it found one.
        EnumResourceNamesW(module, (char*)RtGroupIcon, &TakeFirstName, (nint)(&id));
        try
        {
            var icon = LoadImageW(module, (char*)id, ImageIcon, size, size, 0 /* LR_DEFAULTCOLOR */);
            if (icon == 0) return null;
            try { return Pixels(icon); }
            finally { DestroyIcon(icon); }
        }
        finally
        {
            if ((nuint)id >> 16 != 0) Marshal.FreeHGlobal(id);
        }
    }

    [UnmanagedCallersOnly]
    private static int TakeFirstName(nint module, char* type, char* name, nint param)
    {
        // A name is either an integer id (IS_INTRESOURCE) or a string that only lives during the callback.
        *(nint*)param = (nuint)name >> 16 == 0 ? (nint)name : Marshal.StringToHGlobalUni(new string(name));
        return 0;
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
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(nint dc, int index);
    [DllImport("kernel32.dll")] private static extern nint CreateFileW(char* name, uint access, uint share, void* security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll")] private static extern nint CreateFileMappingW(nint file, void* security, uint protect, uint sizeHigh, uint sizeLow, char* name);
    [DllImport("kernel32.dll")] private static extern void* MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);
    [DllImport("kernel32.dll")] private static extern int UnmapViewOfFile(void* view);
    [DllImport("kernel32.dll")] private static extern int CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern int EnumResourceNamesW(nint module, char* type, delegate* unmanaged<nint, char*, char*, nint, int> callback, nint param);
    [DllImport("user32.dll")] private static extern nint LoadImageW(nint instance, char* name, uint type, int width, int height, uint flags);
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
