namespace Oriel;

/// <summary>Chromium's <c>SkBitmapOperations::CreateHSLShiftedBitmap</c> (ui/gfx/skbitmap_operations.cc) and
/// <c>color_utils::HSLShift</c> on Oriel's premultiplied BGRA pixels: the same line processor per shift, the same
/// fixed-point arithmetic, and <c>float</c> where Chromium computes in <c>float</c>.</summary>
internal static class SkiaHslShift
{
    // HSLShift::epsilon: how close to 0.5 counts as no saturation or lightness change.
    private const double Epsilon = 0.0005;

    /// <summary>The shifted copy of <paramref name="src"/>: <paramref name="h"/> the absolute hue in 0..1 (negative for
    /// none), <paramref name="s"/> and <paramref name="l"/> 0..1 with 0.5 for no change (negative for none).</summary>
    internal static ImageBitmap Shift(ImageBitmap src, double h, double s, double l)
    {
        var hShift = h >= 0 && h <= 1;
        var sOp = s >= 0 && s <= 0.5 - Epsilon ? 1 : s >= 0.5 + Epsilon ? 2 : 0;
        var lOp = l >= 0 && l <= 0.5 - Epsilon ? 1 : l >= 0.5 + Epsilon ? 2 : 0;
        var input = src.Bgra;
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i += 4)
        {
            var (b, g, r, a) = (input[i], input[i + 1], input[i + 2], input[i + 3]);
            (byte R, byte G, byte B) shifted = (hShift || sOp == 2, sOp, lOp) switch
            {
                (true, _, _) => Default(a, r, g, b, h, s, l),
                (false, 0, 0) => (r, g, b),
                (false, 0, 1) => SnopLdec(r, g, b, l),
                (false, 0, 2) => SnopLinc(a, r, g, b, l),
                (false, 1, 0) => SdecLnop(r, g, b, s),
                (false, 1, 1) => SdecLdec(r, g, b, s, l),
                _ => SdecLinc(a, r, g, b, s, l),
            };
            (output[i], output[i + 1], output[i + 2], output[i + 3]) = (shifted.B, shifted.G, shifted.R, a);
        }
        return src with { Bgra = output };
    }

    // LineProcHnopSnopLdec.
    private static (byte, byte, byte) SnopLdec(byte r, byte g, byte b, double l)
    {
        const uint den = 65536;
        var ldec = (uint)(l * 2 * den);
        return ((byte)(r * ldec / den), (byte)(g * ldec / den), (byte)(b * ldec / den));
    }

    // LineProcHnopSnopLinc; unsigned, so a component above alpha wraps as it does in Chromium.
    private static (byte, byte, byte) SnopLinc(byte a, byte r, byte g, byte b, double l)
    {
        const uint den = 65536;
        var linc = (uint)((l - 0.5) * 2 * den);
        unchecked
        {
            return ((byte)(r + (a - (uint)r) * linc / den), (byte)(g + (a - (uint)g) * linc / den), (byte)(b + (a - (uint)b) * linc / den));
        }
    }

    // The saturation processors keep L, (max + min) / 2, and move each component from it.
    private static (int Max, int Min) Extremes(int r, int g, int b)
        => r > g ? (Math.Max(r, b), Math.Min(g, b)) : (Math.Max(g, b), Math.Min(r, b));

    // LineProcHnopSdecLnop.
    private static (byte, byte, byte) SdecLnop(int r, int g, int b, double s)
    {
        const int denom = 65536;
        var sNumer = (int)(s * 2 * denom);
        var (max, min) = Extremes(r, g, b);
        var denomL = (max + min) * (denom / 2);
        var sNumerL = (max + min) * sNumer / 2;
        return ((byte)((denomL + r * sNumer - sNumerL) / denom), (byte)((denomL + g * sNumer - sNumerL) / denom), (byte)((denomL + b * sNumer - sNumerL) / denom));
    }

    // LineProcHnopSdecLdec.
    private static (byte, byte, byte) SdecLdec(int r, int g, int b, double s, double l)
    {
        const int denom = 1024;
        var lNumer = (int)(l * 2 * denom);
        var sNumer = (int)(s * 2 * denom);
        var (max, min) = Extremes(r, g, b);
        var denomL = (max + min) * (denom / 2);
        var sNumerL = (max + min) * sNumer / 2;
        byte One(int c) => (byte)((denomL + c * sNumer - sNumerL) * lNumer / (denom * denom));
        return (One(r), One(g), One(b));
    }

    // LineProcHnopSdecLinc.
    private static (byte, byte, byte) SdecLinc(int a, int r, int g, int b, double s, double l)
    {
        const int denom = 1024;
        var lNumer = (int)((l - 0.5) * 2 * denom);
        var sNumer = (int)(s * 2 * denom);
        var (max, min) = Extremes(r, g, b);
        var denomL = (max + min) * (denom / 2);
        var sNumerL = (max + min) * sNumer / 2;
        byte One(int c)
        {
            var v = denomL + c * sNumer - sNumerL;
            return (byte)((v * denom + (a * denom - v) * lNumer) / (denom * denom));
        }
        return (One(r), One(g), One(b));
    }

    // LineProcDefault: unpremultiply, color_utils::HSLShift, premultiply.
    private static (byte, byte, byte) Default(byte a, byte r, byte g, byte b, double h, double s, double l)
    {
        var scale = LinuxTrayRules.UnpremultiplyScale(a);
        var (ur, ug, ub) = (LinuxTrayRules.ApplyScale(scale, r), LinuxTrayRules.ApplyScale(scale, g), LinuxTrayRules.ApplyScale(scale, b));
        var (sr, sg, sb) = HslShift(ur, ug, ub, h, s, l);
        return a == 255 ? (sr, sg, sb) : (MulDiv255Round(sr, a), MulDiv255Round(sg, a), MulDiv255Round(sb, a));
    }

    // SkMulDiv255Round.
    private static byte MulDiv255Round(int value, int alpha)
    {
        var product = value * alpha + 128;
        return (byte)((product + (product >> 8)) >> 8);
    }

    /// <summary><c>color_utils::HSLShift</c> on an unpremultiplied colour.</summary>
    internal static (byte R, byte G, byte B) HslShift(byte r, byte g, byte b, double h, double s, double l)
    {
        if (h >= 0 || s >= 0)
        {
            var (hh, hs, hl) = ToHsl(r, g, b);
            if (h >= 0) hh = h;
            if (s >= 0)
            {
                if (s <= 0.5f) hs *= s * 2.0f;
                else hs += (1.0f - hs) * ((s - 0.5f) * 2.0f);
            }
            (r, g, b) = FromHsl(hh, hs, hl);
        }
        if (l < 0) return (r, g, b);
        // Lightness in the style of image editors, not HSL's: L also moves saturation.
        float fr = r, fg = g, fb = b;
        if (l <= 0.5f)
        {
            fr = (float)(fr * (l * 2.0f));
            fg = (float)(fg * (l * 2.0f));
            fb = (float)(fb * (l * 2.0f));
        }
        else
        {
            fr = (float)(fr + (255.0f - fr) * ((l - 0.5f) * 2.0f));
            fg = (float)(fg + (255.0f - fg) * ((l - 0.5f) * 2.0f));
            fb = (float)(fb + (255.0f - fb) * ((l - 0.5f) * 2.0f));
        }
        return (ClampRoundByte(fr), ClampRoundByte(fg), ClampRoundByte(fb));
    }

    /// <summary><c>color_utils::SkColorToHSL</c>: computed in <c>float</c>, kept in <c>color_utils::HSL</c>'s doubles.</summary>
    internal static (double H, double S, double L) ToHsl(byte red, byte green, byte blue)
    {
        var r = red / 255.0f;
        var g = green / 255.0f;
        var b = blue / 255.0f;
        var vmin = Math.Min(r, Math.Min(g, b));
        var vmax = Math.Max(r, Math.Max(g, b));
        var delta = vmax - vmin;
        double l = (vmin + vmax) / 2;
        if (red == green && red == blue) return (0, 0, l);
        var dr = (((vmax - r) / 6.0f) + (delta / 2.0f)) / delta;
        var dg = (((vmax - g) / 6.0f) + (delta / 2.0f)) / delta;
        var db = (((vmax - b) / 6.0f) + (delta / 2.0f)) / delta;
        double h;
        if (r >= g && r >= b) h = db - dg;
        else if (g >= r && g >= b) h = (1.0f / 3.0f) + dr - db;
        else h = (2.0f / 3.0f) + dg - dr;
        if (h < 0.0f) ++h;
        else if (h > 1.0f) --h;
        double s = delta / ((l < 0.5f) ? (vmax + vmin) : (2 - vmax - vmin));
        return (h, s, l);
    }

    /// <summary><c>color_utils::HSLToSkColor</c>, in <c>float</c>.</summary>
    internal static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
    {
        var hue = (float)h;
        var saturation = (float)s;
        var lightness = (float)l;
        if (saturation == 0)
        {
            var light = ClampRoundByte(lightness * 255);
            return (light, light, light);
        }
        var temp2 = lightness < 0.5f ? lightness * (1.0f + saturation) : lightness + saturation - (lightness * saturation);
        var temp1 = 2.0f * lightness - temp2;
        return ((byte)CalcHue(temp1, temp2, hue + 1.0f / 3.0f), (byte)CalcHue(temp1, temp2, hue), (byte)CalcHue(temp1, temp2, hue - 1.0f / 3.0f));
    }

    // color_utils' calcHue.
    private static int CalcHue(float temp1, float temp2, float hue)
    {
        if (hue < 0.0f) ++hue;
        else if (hue > 1.0f) --hue;
        var result = temp1;
        if (hue * 6.0f < 1.0f) result = temp1 + (temp2 - temp1) * hue * 6.0f;
        else if (hue * 2.0f < 1.0f) result = temp2;
        else if (hue * 3.0f < 2.0f) result = temp1 + (temp2 - temp1) * (2.0f / 3.0f - hue) * 6.0f;
        return (int)Math.Round(result * 255, MidpointRounding.AwayFromZero);
    }

    // base::ClampRound to a byte: round half away from zero, then clamp.
    private static byte ClampRoundByte(float value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
