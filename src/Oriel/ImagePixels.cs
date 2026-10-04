using System.Globalization;
using System.Text;

namespace Oriel;

/// <summary>A 32-bit BGRA bitmap with premultiplied alpha, top row first; <see cref="Bgra"/> holds Width*Height*4 bytes.</summary>
internal readonly record struct ImageBitmap(int Width, int Height, byte[] Bgra);

/// <summary>Pixel and URL helpers behind <c>NativeImage</c>.</summary>
internal static class ImagePixels
{
    private static readonly string[] ScaleSuffixes = ["1", "1.25", "1.33", "1.4", "1.5", "1.8", "2", "2.5", "3", "4", "5"];

    private static readonly ImageBitmap Empty = new(0, 0, []);

    /// <summary>The part of <paramref name="src"/> inside the rectangle; the rectangle is clipped to the image, and an empty overlap gives an empty bitmap.</summary>
    internal static ImageBitmap Crop(ImageBitmap src, int x, int y, int width, int height)
    {
        long x0 = Math.Max(0L, x), y0 = Math.Max(0L, y);
        long x1 = Math.Min((long)x + width, src.Width), y1 = Math.Min((long)y + height, src.Height);
        if (x1 <= x0 || y1 <= y0) return Empty;

        int w = (int)(x1 - x0), h = (int)(y1 - y0);
        var bgra = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(src.Bgra, (int)(((y0 + row) * src.Width + x0) * 4), bgra, row * w * 4, w * 4);
        return new ImageBitmap(w, h, bgra);
    }

    /// <summary>Scales to the given size: box-averages an axis that shrinks, interpolates bilinearly when it grows, on premultiplied values.</summary>
    internal static ImageBitmap Resize(ImageBitmap src, int width, int height)
    {
        if (width <= 0 || height <= 0 || src.Width <= 0 || src.Height <= 0) return Empty;
        if (width == src.Width && height == src.Height) return src with { Bgra = (byte[])src.Bgra.Clone() };

        var horizontal = ResizeAxis(ToFloats(src.Bgra), src.Height, src.Width, width, 4, src.Width * 4, 4, width * 4);
        var vertical = ResizeAxis(horizontal, width, src.Height, height, width * 4, 4, width * 4, 4);
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i++) bgra[i] = (byte)Math.Clamp((int)(vertical[i] + 0.5f), 0, 255);
        return new ImageBitmap(width, height, bgra);
    }

    private static float[] ToFloats(byte[] bytes)
    {
        var f = new float[bytes.Length];
        for (var i = 0; i < f.Length; i++) f[i] = bytes[i];
        return f;
    }

    // Resamples one axis of a 4-channel float image. Strides are in floats: between samples along the axis and between
    // lines across it, for the input and the output. The vertical pass treats columns as lines.
    private static float[] ResizeAxis(float[] input, int lines, int srcN, int dstN, int inSample, int inLine, int outSample, int outLine)
    {
        var output = new float[(lines - 1) * outLine + (dstN - 1) * outSample + 4];
        var scale = (double)srcN / dstN;
        for (var d = 0; d < dstN; d++)
        {
            var taps = Taps(d, srcN, dstN, scale);
            for (var line = 0; line < lines; line++)
            for (var c = 0; c < 4; c++)
            {
                double sum = 0;
                foreach (var (index, weight) in taps)
                    sum += input[line * inLine + index * inSample + c] * weight;
                output[line * outLine + d * outSample + c] = (float)sum;
            }
        }
        return output;
    }

    private static List<(int Index, double Weight)> Taps(int d, int srcN, int dstN, double scale)
    {
        var taps = new List<(int, double)>();
        if (dstN < srcN)
        {
            double start = d * scale, end = start + scale;
            for (var s = (int)start; s < srcN && s < end; s++)
            {
                var overlap = Math.Min(end, s + 1) - Math.Max(start, s);
                if (overlap > 0) taps.Add((s, overlap / scale));
            }
        }
        else
        {
            var pos = Math.Clamp((d + 0.5) * scale - 0.5, 0, srcN - 1);
            var lo = (int)pos;
            var frac = pos - lo;
            taps.Add((lo, 1 - frac));
            if (frac > 0) taps.Add((lo + 1, frac));
        }
        return taps;
    }

    /// <summary>The same pixels with straight alpha, as Win32 icon DIBs take them; fully transparent pixels become zero.</summary>
    internal static ImageBitmap Unpremultiply(ImageBitmap src)
    {
        var bgra = new byte[src.Bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = src.Bgra[i + 3];
            bgra[i + 3] = a;
            if (a == 0) continue;
            for (var c = 0; c < 3; c++) bgra[i + c] = (byte)Math.Min(255, (src.Bgra[i + c] * 255 + a / 2) / a);
        }
        return src with { Bgra = bgra };
    }

    /// <summary>Electron's <c>nativeImage.resize</c> size rule: both given use them, one given scales the other by aspect ratio, neither keeps the source size.</summary>
    internal static (int Width, int Height) ResizeTarget(int srcWidth, int srcHeight, int? width, int? height)
    {
        if (width is { } w && height is { } h) return (w, h);
        if (width is { } onlyW) return (onlyW, Other(srcHeight, srcWidth, onlyW));
        if (height is { } onlyH) return (Other(srcWidth, srcHeight, onlyH), onlyH);
        return (srcWidth, srcHeight);

        static int Other(int srcOther, int srcGiven, int given)
        {
            if (srcGiven <= 0 || srcOther <= 0) return 0;
            var v = (int)Math.Round((double)srcOther * given / srcGiven, MidpointRounding.AwayFromZero);
            return Math.Max(1, v);
        }
    }

    /// <summary>Parses <c>data:[&lt;mime&gt;][;charset=..][;base64],&lt;payload&gt;</c>; false when it is not a data URL or the payload is malformed.</summary>
    internal static bool TryParseDataUrl(string url, out string mimeType, out byte[] data)
    {
        mimeType = "";
        data = [];
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        var comma = url.IndexOf(',');
        if (comma < 0) return false;

        var parts = url[5..comma].Split(';');
        var mime = parts[0].Trim();
        var isBase64 = parts.Skip(1).Any(p => p.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase));
        var mimeGiven = mime.Length > 0 && !mime.Contains('=') && !mime.Equals("base64", StringComparison.OrdinalIgnoreCase);
        var payload = PercentDecode(url[(comma + 1)..]);

        if (isBase64)
        {
            var text = Encoding.Latin1.GetString(payload);
            var buffer = new byte[text.Length];
            if (!Convert.TryFromBase64String(text, buffer, out var written)) return false;
            payload = buffer[..written];
        }

        mimeType = mimeGiven ? mime.ToLowerInvariant() : "text/plain";
        data = payload;
        return true;
    }

    private static byte[] PercentDecode(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length
                && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                // Multi-byte characters stay UTF-8 so a non-ASCII payload round-trips.
                var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(text.AsSpan(i, len).ToString()));
                i += len - 1;
            }
        }
        return [.. bytes];
    }

    /// <summary>Builds <c>data:&lt;mime&gt;;base64,&lt;base64&gt;</c>.</summary>
    internal static string ToDataUrl(string mimeType, ReadOnlySpan<byte> data)
        => $"data:{mimeType};base64,{Convert.ToBase64String(data)}";

    /// <summary>Electron's <c>createFromPath</c> HiDPI lookup: the path at scale 1, then its <c>@Nx</c> siblings; a path already carrying a scale suffix yields only itself.</summary>
    internal static IReadOnlyList<(string Path, float ScaleFactor)> ScaleVariants(string path)
    {
        var ext = Path.GetExtension(path);
        var stem = path[..^ext.Length];
        foreach (var s in ScaleSuffixes)
            if (stem.EndsWith($"@{s}x", StringComparison.Ordinal))
                return [(path, float.Parse(s, CultureInfo.InvariantCulture))];

        var list = new List<(string, float)> { (path, 1f) };
        foreach (var s in ScaleSuffixes.Skip(1))
            list.Add(($"{stem}@{s}x{ext}", float.Parse(s, CultureInfo.InvariantCulture)));
        return list;
    }
}
