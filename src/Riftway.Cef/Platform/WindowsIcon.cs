using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Reads <c>.ico</c> files as Electron does on Windows: <c>LoadImage</c>, then Chromium's
/// <c>IconUtil::CreateSkBitmapFromHICON</c>.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsIcon
{
    /// <summary>The icon <c>LoadImage(IMAGE_ICON, size, size, LR_LOADFROMFILE)</c> makes of <paramref name="path"/>, as
    /// premultiplied BGRA; null if Windows cannot load it.</summary>
    public static (int Width, int Height, byte[] Bgra)? Load(string path, int size)
    {
        nint icon;
        fixed (char* name = path) icon = LoadImageW(0, name, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        if (icon == 0) return null;
        try { return Pixels(icon, size, size); }
        finally { _ = DestroyIcon(icon); }
    }

    /// <summary>Chromium's <c>IconUtil::CreateSkBitmapFromHICON(HICON)</c>: the icon at its mask's size (half the mask's
    /// height for a monochrome icon), as premultiplied BGRA; null if Windows cannot describe or draw it.</summary>
    public static (int Width, int Height, byte[] Bgra)? FromIcon(nint icon)
    {
        IconInfo info;
        if (icon == 0 || GetIconInfo(icon, &info) == 0) return null;
        try
        {
            BitmapInfo mask;
            if (GetObjectW(info.Mask, sizeof(BitmapInfo), &mask) == 0) return null;
            var height = mask.Height / (info.Color != 0 ? 1 : 2);
            return mask.Width > 0 && height > 0 ? Pixels(icon, mask.Width, height) : null;
        }
        finally
        {
            if (info.Mask != 0) _ = DeleteObject(info.Mask);
            if (info.Color != 0) _ = DeleteObject(info.Color);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX, HotspotY;
        public nint Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    // IconUtil::CreateSkBitmapFromHICON: the AND mask drawn first gives opacity, used only when the image has no alpha.
    private static (int, int, byte[])? Pixels(nint icon, int width, int height)
    {
        var header = stackalloc uint[10];
        header[0] = 40;
        header[1] = (uint)width;
        header[2] = (uint)-height;
        header[3] = 1 | (32u << 16);
        var screen = GetDC(0);
        void* bits;
        var dib = CreateDIBSection(screen, header, 0, &bits, 0, 0);
        var dc = CreateCompatibleDC(screen);
        _ = ReleaseDC(0, screen);
        if (dib == 0 || dc == 0)
        {
            if (dib != 0) _ = DeleteObject(dib);
            if (dc != 0) _ = DeleteDC(dc);
            return null;
        }
        var old = SelectObject(dc, dib);
        try
        {
            var count = width * height;
            var pixels = new Span<uint>(bits, count);
            pixels.Clear();
            if (DrawIconEx(dc, 0, 0, icon, width, height, 0, 0, 0x0001 /* DI_MASK */) == 0) return null;
            var opaque = new bool[count];
            for (var i = 0; i < count; i++) opaque[i] = pixels[i] == 0;
            pixels.Clear();
            if (DrawIconEx(dc, 0, 0, icon, width, height, 0, 0, 0x0003 /* DI_NORMAL */) == 0) return null;
            var hasAlpha = false;
            foreach (var p in pixels) hasAlpha |= (p & 0xFF000000) != 0;
            if (!hasAlpha)
                for (var i = 0; i < count; i++) pixels[i] = opaque[i] ? pixels[i] | 0xFF000000 : pixels[i] & 0x00FFFFFF;
            return (width, height, MemoryMarshal.AsBytes(pixels).ToArray());
        }
        finally
        {
            _ = SelectObject(dc, old);
            _ = DeleteDC(dc);
            _ = DeleteObject(dib);
        }
    }

    [DllImport("user32.dll")] private static extern nint LoadImageW(nint instance, char* name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetIconInfo(nint icon, IconInfo* info);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, void* buffer);
    [DllImport("user32.dll")] private static extern int DrawIconEx(nint dc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, void* info, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
}
