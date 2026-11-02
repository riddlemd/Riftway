using System.Globalization;
using System.Text.RegularExpressions;

namespace Riftway;

/// <summary>Chromium's <c>content::ParseCssColorString</c>, which Electron uses for <c>titleBarOverlay</c> colours:
/// <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c> (alpha last), <c>rgb()</c>/<c>rgba()</c> with integer
/// channels, <c>hsl()</c>/<c>hsla()</c>, and Skia's named colours. Not the full CSS grammar.</summary>
internal static partial class CssColor
{
    /// <summary>Parses <paramref name="color"/> into ARGB, as Chromium does; false where Chromium's parser fails.</summary>
    internal static bool TryParse(string? color, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrEmpty(color)) return false;
        if (color[0] == '#') return TryParseHex(color, out argb);
        if (color.StartsWith("hsl", StringComparison.Ordinal)) return TryParseHsl(color, out argb);
        if (color.StartsWith("rgb", StringComparison.Ordinal)) return TryParseRgb(color, out argb);
        // SkParse::FindNamedColor compares C strings: the name ends at the first NUL.
        var nul = color.IndexOf('\0');
        if (Named.TryGetValue(nul < 0 ? color : color[..nul], out var rgb))
        {
            argb = 0xFF000000 | rgb;
            return true;
        }
        return false;
    }

    /// <summary>Electron's <c>ParseCSSColor</c>: as <see cref="TryParse"/>, except that <c>#ARGB</c> and
    /// <c>#AARRGGBB</c> put the alpha first, Electron's historical hex order.</summary>
    internal static bool TryParseElectron(string? color, out uint argb)
    {
        // IsHexFormatWithAlpha: five or nine characters, all hex digits after the '#'.
        if (color is { Length: 5 or 9 } && color[0] == '#' && !color.AsSpan(1).ContainsAnyExcept(HexDigits))
        {
            var alpha = color.Length == 5 ? 1 : 2;
            color = "#" + color[(1 + alpha)..] + color.Substring(1, alpha);
        }
        return TryParse(color, out argb);
    }

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789ABCDEFabcdef");

    private static bool TryParseHex(string color, out uint argb)
    {
        argb = 0;
        var digits = color.Length switch
        {
            4 or 5 => string.Concat(color[1..].Select(c => new string(c, 2))),
            7 or 9 => color[1..],
            _ => null,
        };
        if (digits?.Length == 6) digits += "FF";
        if (digits is null || !digits.All(char.IsAsciiHexDigit)) return false;
        var rgba = uint.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        argb = (rgba << 24) | (rgba >> 8);
        return true;
    }

    private static bool TryParseRgb(string color, out uint argb)
    {
        argb = 0;
        var match = Rgb().Match(color);
        if (!match.Success) match = Rgba().Match(color);
        if (!match.Success) return false;
        var alpha = 1.0;
        if (!TryChannel(match.Groups[1].Value, out var r) || !TryChannel(match.Groups[2].Value, out var g) || !TryChannel(match.Groups[3].Value, out var b)
            || (match.Groups[4].Success && !TryDouble(match.Groups[4].Value, out alpha)) || alpha is < 0 or > 1)
            return false;
        argb = ((uint)(alpha * 255) << 24) | (r << 16) | (g << 8) | b;
        return true;
    }

    private static bool TryParseHsl(string color, out uint argb)
    {
        argb = 0;
        var match = Hsl().Match(color);
        if (!match.Success) match = Hsla().Match(color);
        if (!match.Success) return false;
        var alpha = 1.0;
        if (!TryDouble(match.Groups[1].Value, out var hue) || !TryDouble(match.Groups[2].Value, out var saturation) || !TryDouble(match.Groups[3].Value, out var lightness)
            || (match.Groups[4].Success && !TryDouble(match.Groups[4].Value, out alpha)))
            return false;
        // static_cast<int> of an out-of-range double gives INT_MIN on x64, where .NET saturates.
        var whole = hue is >= int.MinValue and < 2147483648.0 ? (int)hue : int.MinValue;
        var h = ((whole % 360 + 360) % 360) / 360.0;
        var s = Math.Clamp(saturation, 0.0, 100.0) / 100.0;
        var l = Math.Clamp(lightness, 0.0, 100.0) / 100.0;
        argb = HslToArgb((float)h, (float)s, (float)l, (byte)(Math.Clamp(alpha, 0.0, 1.0) * 255));
        return true;
    }

    // color_utils::HSLToSkColor, in float as Chromium computes it.
    private static uint HslToArgb(float hue, float saturation, float lightness, byte alpha)
    {
        if (saturation == 0)
        {
            var light = (uint)ClampRound(lightness * 255f, 0, 255);
            return ((uint)alpha << 24) | (light << 16) | (light << 8) | light;
        }
        var temp2 = lightness < 0.5f ? lightness * (1.0f + saturation) : lightness + saturation - lightness * saturation;
        var temp1 = 2.0f * lightness - temp2;
        return ((uint)alpha << 24) | (Channel(temp1, temp2, hue + 1.0f / 3.0f) << 16) | (Channel(temp1, temp2, hue) << 8) | Channel(temp1, temp2, hue - 1.0f / 3.0f);
    }

    // color_utils' calcHue.
    private static uint Channel(float temp1, float temp2, float hue)
    {
        if (hue < 0.0f) ++hue;
        else if (hue > 1.0f) --hue;
        var result = temp1;
        if (hue * 6.0f < 1.0f) result = temp1 + (temp2 - temp1) * hue * 6.0f;
        else if (hue * 2.0f < 1.0f) result = temp2;
        else if (hue * 3.0f < 2.0f) result = temp1 + (temp2 - temp1) * (2.0f / 3.0f - hue) * 6.0f;
        return (uint)ClampRound(result * 255f, 0, 255);
    }

    private static float ClampRound(float value, float min, float max) => Math.Clamp(MathF.Round(value, MidpointRounding.AwayFromZero), min, max);

    // RE2 parses an int capture with strtol after s/000+/00/: any value above 255 fails the range check.
    private static bool TryChannel(string digits, out uint value)
    {
        var trimmed = digits.TrimStart('0');
        value = 0;
        if (trimmed.Length > 3) return false;
        value = trimmed.Length == 0 ? 0 : uint.Parse(trimmed, CultureInfo.InvariantCulture);
        return value <= 255;
    }

    // RE2's strtod capture: the whole capture must be one number, at most 200 characters once s/000+/00/ applies.
    private static bool TryDouble(string text, out double value)
    {
        value = 0;
        var sign = text.StartsWith('-') ? 1 : 0;
        var body = text[sign..];
        while (body.Length >= 3 && body[0] == '0' && body[1] == '0' && body[2] == '0') body = body[1..];
        if (sign + body.Length > 200) return false;
        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    // RE2's \s is [\t\n\f\r ] and \d is ASCII; .NET's are Unicode.
    [GeneratedRegex(@"^rgb\(([0-9]+),[\t\n\f\r ]*([0-9]+),[\t\n\f\r ]*([0-9]+)\)\z")]
    private static partial Regex Rgb();

    [GeneratedRegex(@"^rgba\(([0-9]+),[\t\n\f\r ]*([0-9]+),[\t\n\f\r ]*([0-9]+),[\t\n\f\r ]*([0-9.]+)\)\z")]
    private static partial Regex Rgba();

    [GeneratedRegex(@"^hsl\((-?[0-9.]+),[\t\n\f\r ]*([0-9.]+)%,[\t\n\f\r ]*([0-9.]+)%\)\z")]
    private static partial Regex Hsl();

    [GeneratedRegex(@"^hsla\((-?[0-9.]+),[\t\n\f\r ]*([0-9.]+)%,[\t\n\f\r ]*([0-9.]+)%,[\t\n\f\r ]*([0-9.]+)\)\z")]
    private static partial Regex Hsla();

    // Skia's gColorNames/gColors (SkParseColor.cpp): exact, lowercase, no "transparent".
    private static readonly Dictionary<string, uint> Named = new(StringComparer.Ordinal)
    {
        ["aliceblue"] = 0xF0F8FF, ["antiquewhite"] = 0xFAEBD7, ["aqua"] = 0x00FFFF, ["aquamarine"] = 0x7FFFD4,
        ["azure"] = 0xF0FFFF, ["beige"] = 0xF5F5DC, ["bisque"] = 0xFFE4C4, ["black"] = 0x000000,
        ["blanchedalmond"] = 0xFFEBCD, ["blue"] = 0x0000FF, ["blueviolet"] = 0x8A2BE2, ["brown"] = 0xA52A2A,
        ["burlywood"] = 0xDEB887, ["cadetblue"] = 0x5F9EA0, ["chartreuse"] = 0x7FFF00, ["chocolate"] = 0xD2691E,
        ["coral"] = 0xFF7F50, ["cornflowerblue"] = 0x6495ED, ["cornsilk"] = 0xFFF8DC, ["crimson"] = 0xDC143C,
        ["cyan"] = 0x00FFFF, ["darkblue"] = 0x00008B, ["darkcyan"] = 0x008B8B, ["darkgoldenrod"] = 0xB8860B,
        ["darkgray"] = 0xA9A9A9, ["darkgreen"] = 0x006400, ["darkkhaki"] = 0xBDB76B, ["darkmagenta"] = 0x8B008B,
        ["darkolivegreen"] = 0x556B2F, ["darkorange"] = 0xFF8C00, ["darkorchid"] = 0x9932CC, ["darkred"] = 0x8B0000,
        ["darksalmon"] = 0xE9967A, ["darkseagreen"] = 0x8FBC8F, ["darkslateblue"] = 0x483D8B, ["darkslategray"] = 0x2F4F4F,
        ["darkturquoise"] = 0x00CED1, ["darkviolet"] = 0x9400D3, ["deeppink"] = 0xFF1493, ["deepskyblue"] = 0x00BFFF,
        ["dimgray"] = 0x696969, ["dodgerblue"] = 0x1E90FF, ["firebrick"] = 0xB22222, ["floralwhite"] = 0xFFFAF0,
        ["forestgreen"] = 0x228B22, ["fuchsia"] = 0xFF00FF, ["gainsboro"] = 0xDCDCDC, ["ghostwhite"] = 0xF8F8FF,
        ["gold"] = 0xFFD700, ["goldenrod"] = 0xDAA520, ["gray"] = 0x808080, ["green"] = 0x008000,
        ["greenyellow"] = 0xADFF2F, ["honeydew"] = 0xF0FFF0, ["hotpink"] = 0xFF69B4, ["indianred"] = 0xCD5C5C,
        ["indigo"] = 0x4B0082, ["ivory"] = 0xFFFFF0, ["khaki"] = 0xF0E68C, ["lavender"] = 0xE6E6FA,
        ["lavenderblush"] = 0xFFF0F5, ["lawngreen"] = 0x7CFC00, ["lemonchiffon"] = 0xFFFACD, ["lightblue"] = 0xADD8E6,
        ["lightcoral"] = 0xF08080, ["lightcyan"] = 0xE0FFFF, ["lightgoldenrodyellow"] = 0xFAFAD2, ["lightgreen"] = 0x90EE90,
        ["lightgrey"] = 0xD3D3D3, ["lightpink"] = 0xFFB6C1, ["lightsalmon"] = 0xFFA07A, ["lightseagreen"] = 0x20B2AA,
        ["lightskyblue"] = 0x87CEFA, ["lightslategray"] = 0x778899, ["lightsteelblue"] = 0xB0C4DE, ["lightyellow"] = 0xFFFFE0,
        ["lime"] = 0x00FF00, ["limegreen"] = 0x32CD32, ["linen"] = 0xFAF0E6, ["magenta"] = 0xFF00FF,
        ["maroon"] = 0x800000, ["mediumaquamarine"] = 0x66CDAA, ["mediumblue"] = 0x0000CD, ["mediumorchid"] = 0xBA55D3,
        ["mediumpurple"] = 0x9370DB, ["mediumseagreen"] = 0x3CB371, ["mediumslateblue"] = 0x7B68EE, ["mediumspringgreen"] = 0x00FA9A,
        ["mediumturquoise"] = 0x48D1CC, ["mediumvioletred"] = 0xC71585, ["midnightblue"] = 0x191970, ["mintcream"] = 0xF5FFFA,
        ["mistyrose"] = 0xFFE4E1, ["moccasin"] = 0xFFE4B5, ["navajowhite"] = 0xFFDEAD, ["navy"] = 0x000080,
        ["oldlace"] = 0xFDF5E6, ["olive"] = 0x808000, ["olivedrab"] = 0x6B8E23, ["orange"] = 0xFFA500,
        ["orangered"] = 0xFF4500, ["orchid"] = 0xDA70D6, ["palegoldenrod"] = 0xEEE8AA, ["palegreen"] = 0x98FB98,
        ["paleturquoise"] = 0xAFEEEE, ["palevioletred"] = 0xDB7093, ["papayawhip"] = 0xFFEFD5, ["peachpuff"] = 0xFFDAB9,
        ["peru"] = 0xCD853F, ["pink"] = 0xFFC0CB, ["plum"] = 0xDDA0DD, ["powderblue"] = 0xB0E0E6,
        ["purple"] = 0x800080, ["red"] = 0xFF0000, ["rosybrown"] = 0xBC8F8F, ["royalblue"] = 0x4169E1,
        ["saddlebrown"] = 0x8B4513, ["salmon"] = 0xFA8072, ["sandybrown"] = 0xF4A460, ["seagreen"] = 0x2E8B57,
        ["seashell"] = 0xFFF5EE, ["sienna"] = 0xA0522D, ["silver"] = 0xC0C0C0, ["skyblue"] = 0x87CEEB,
        ["slateblue"] = 0x6A5ACD, ["slategray"] = 0x708090, ["snow"] = 0xFFFAFA, ["springgreen"] = 0x00FF7F,
        ["steelblue"] = 0x4682B4, ["tan"] = 0xD2B48C, ["teal"] = 0x008080, ["thistle"] = 0xD8BFD8,
        ["tomato"] = 0xFF6347, ["turquoise"] = 0x40E0D0, ["violet"] = 0xEE82EE, ["wheat"] = 0xF5DEB3,
        ["white"] = 0xFFFFFF, ["whitesmoke"] = 0xF5F5F5, ["yellow"] = 0xFFFF00, ["yellowgreen"] = 0x9ACD32,
    };
}
