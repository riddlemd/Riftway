using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Draws the taskbar overlay badge Electron's <c>Browser::UpdateBadgeContents</c> draws on Windows: white text
/// on a dark circle, 16 x 16, with the largest font size from 24 down to 7 that fits the text inside a 3 px margin.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsBadge
{
    /// <summary>The badge's width and height in pixels; Electron's <c>kOverlayIconSize</c>.</summary>
    public const int Size = 16;

    // The Windows 10 Badge API's background, which Electron copies.
    private const byte BackgroundR = 0x26, BackgroundG = 0x25, BackgroundB = 0x2D;
    private const int Radius = Size / 2;
    private const int MaxBounds = Size - 2 * 3;
    private const int MaxTextSize = 24;
    private const int MinTextSize = 7;

    // Scratch canvas for measuring and rasterizing; the text starts at (Pad, Pad) so overhangs stay inside.
    private const int ScratchWidth = 160;
    private const int ScratchHeight = 64;
    private const int Pad = 16;

    /// <summary>The badge for <paramref name="content"/> as straight-alpha BGRA, top row first, <see cref="Size"/>
    /// pixels square.</summary>
    public static byte[] Draw(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (mask, ink, _) = Fit(content);

        // Centres the ink box on the circle's centre, as Electron does with the measured bounds.
        var dx = (int)MathF.Round(Radius - ink.Width / 2f) - ink.Left;
        var dy = (int)MathF.Round(Radius - ink.Height / 2f) - ink.Top;
        var bgra = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var circle = CircleCoverage(x, y);
                int sx = x - dx, sy = y - dy;
                var text = sx is >= 0 and < ScratchWidth && sy is >= 0 and < ScratchHeight ? mask[sy * ScratchWidth + sx] / 255f : 0f;
                // White text over the circle, then the taskbar host's circular clip multiplies everything by it again.
                var alpha = (text + circle * (1 - text)) * circle;
                if (alpha <= 0) continue;
                var white = text * circle;
                var i = (y * Size + x) * 4;
                bgra[i] = Straight(white * 255 + circle * (1 - text) * circle * BackgroundB, alpha);
                bgra[i + 1] = Straight(white * 255 + circle * (1 - text) * circle * BackgroundG, alpha);
                bgra[i + 2] = Straight(white * 255 + circle * (1 - text) * circle * BackgroundR, alpha);
                bgra[i + 3] = (byte)MathF.Round(alpha * 255);
            }
        }
        return bgra;
    }

    /// <summary>The font size <see cref="Draw"/> picks for <paramref name="content"/>.</summary>
    public static int FontSize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Fit(content).Size;
    }

    private static (byte[] Mask, (int Left, int Top, int Width, int Height) Ink, int Size) Fit(string content)
    {
        var face = MessageFontFace();
        // Electron's do/while: try 24, 23, ... and stop at the first size that fits, or after 7 whatever the fit.
        var size = MaxTextSize;
        byte[] mask;
        (int Left, int Top, int Width, int Height) ink;
        do
        {
            mask = Rasterize(content, face, size--);
            ink = InkBounds(mask);
        }
        while (size >= MinTextSize && (ink.Width > MaxBounds || ink.Height > MaxBounds));
        return (mask, ink, size + 1);
    }

    private static byte Straight(float premultiplied, float alpha) => (byte)Math.Clamp(MathF.Round(premultiplied / alpha), 0, 255);

    // Skia's anti-aliased circle: coverage falls off over one pixel across the edge.
    private static float CircleCoverage(int x, int y)
    {
        var distance = MathF.Sqrt((x + 0.5f - Radius) * (x + 0.5f - Radius) + (y + 0.5f - Radius) * (y + 0.5f - Radius));
        return Math.Clamp(Radius + 0.5f - distance, 0f, 1f);
    }

    // Skia's default typeface on Windows is the system message font.
    private static string MessageFontFace()
    {
        var metrics = stackalloc byte[NonClientMetricsSize];
        *(uint*)metrics = NonClientMetricsSize;
        if (SystemParametersInfoW(SpiGetNonClientMetrics, NonClientMetricsSize, metrics, 0) == 0) return "Segoe UI";
        var face = new string((char*)(metrics + MessageFontFaceOffset));
        return face.Length == 0 ? "Segoe UI" : face;
    }

    // Grayscale-antialiased white-on-black text: each pixel's green channel is the glyph coverage.
    private static byte[] Rasterize(string content, string face, int size)
    {
        var mask = new byte[ScratchWidth * ScratchHeight];
        var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = ScratchWidth, Height = -ScratchHeight, Planes = 1, BitCount = 32 };
        void* bits;
        var dc = CreateCompatibleDC(0);
        if (dc == 0) return mask;
        var bitmap = CreateDIBSection(dc, &header, 0, &bits, 0, 0);
        nint font;
        fixed (char* name = face)
            font = CreateFontW(-size, 0, 0, 0, 400, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, name);
        try
        {
            if (bitmap == 0 || font == 0) return mask;
            var oldBitmap = SelectObject(dc, bitmap);
            var oldFont = SelectObject(dc, font);
            SetBkMode(dc, 1 /* TRANSPARENT */);
            SetTextColor(dc, 0x00FFFFFF);
            fixed (char* text = content) TextOutW(dc, Pad, Pad, text, content.Length);
            GdiFlush();
            var pixels = (byte*)bits;
            for (var i = 0; i < mask.Length; i++) mask[i] = pixels[i * 4 + 1];
            SelectObject(dc, oldFont);
            SelectObject(dc, oldBitmap);
            return mask;
        }
        finally
        {
            if (font != 0) DeleteObject(font);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    private static (int Left, int Top, int Width, int Height) InkBounds(byte[] mask)
    {
        int left = ScratchWidth, top = ScratchHeight, right = -1, bottom = -1;
        for (var y = 0; y < ScratchHeight; y++)
        {
            for (var x = 0; x < ScratchWidth; x++)
            {
                if (mask[y * ScratchWidth + x] == 0) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        return right < 0 ? (Pad, Pad, 0, 0) : (left, top, right - left + 1, bottom - top + 1);
    }

    private const uint SpiGetNonClientMetrics = 0x29;
    // NONCLIENTMETRICSW with iPaddedBorderWidth; lfMessageFont starts at 408, its lfFaceName 28 bytes in.
    private const int NonClientMetricsSize = 504;
    private const int MessageFontFaceOffset = 408 + 28;

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

    [DllImport("user32.dll")] private static extern int SystemParametersInfoW(uint action, uint param, void* data, uint winIni);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, BitmapInfoHeader* header, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, char* face);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int TextOutW(nint dc, int x, int y, char* text, int length);
    [DllImport("gdi32.dll")] private static extern int GdiFlush();
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
}
