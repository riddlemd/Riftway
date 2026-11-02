using System.Buffers.Binary;
using System.Globalization;

namespace Riftway;

/// <summary>Chromium's arithmetic for the <see cref="Display"/> fields Windows feeds it: colour space, depths,
/// internal panels and native origin (ui/display/win/screen_win.cc, ui/gfx/color_space.cc, ui/gfx/icc_profile.cc).</summary>
internal static class DisplayMapping
{
    /// <summary><c>Display::kDefaultBitsPerPixel</c>.</summary>
    internal const int DefaultBitsPerPixel = 24;

    /// <summary><c>Display::kDefaultBitsPerComponent</c>.</summary>
    internal const int DefaultBitsPerComponent = 8;

    /// <summary><c>gfx::ColorSpace::CreateSRGB().ToString()</c>.</summary>
    internal const string Srgb = "{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}";

    // With HDR on, the WCG output space is scRGB linear, which is unsuitable for blending, so
    // GetRasterAndCompositeColorSpace falls back to P3 with the sRGB transfer.
    internal const string HdrRaster = "{primaries:P3, transfer:SRGB, matrix:RGB, range:FULL}";

    /// <summary>Colour and per-component depth: Chromium's HDR10 values when the display supports HDR, else the defaults.</summary>
    internal static (int ColorDepth, int DepthPerComponent) Depths(bool hdr) => hdr ? (30, 10) : (DefaultBitsPerPixel, DefaultBitsPerComponent);

    /// <summary>Chromium's <c>IsInternalOutputTechnology</c>: <c>INTERNAL</c>, <c>DISPLAYPORT_EMBEDDED</c> or <c>UDI_EMBEDDED</c>.</summary>
    internal static bool IsInternalOutputTechnology(uint technology) => technology is 0x80000000 or 11 or 13;

    /// <summary>The origin <c>ScreenWin</c> creates the display with: <c>ScaleToEnclosingRect(rcMonitor, 1 / scale)</c>,
    /// which <c>SetScaleAndBounds</c> keeps as the native origin before the layout moves the bounds.</summary>
    internal static Point NativeOrigin(int left, int top, float scale)
    {
        var inverse = 1.0f / scale;
        return new Point((int)MathF.Floor(left * inverse), (int)MathF.Floor(top * inverse));
    }

    /// <summary>The SDR colour space string for a monitor's ICC profile, as <c>ColorProfileReader</c> and
    /// <c>ICCProfile::GetPrimariesOnlyColorSpace</c> give it: the profile's primaries with the sRGB transfer, or sRGB when
    /// there is no usable profile.</summary>
    internal static string SdrColorSpace(byte[]? icc) => IccToXyzD50(icc) is { } matrix ? WithSrgbTransfer(matrix) : Srgb;

    // ColorSpace(CUSTOM, SRGB, RGB, FULL, &toXYZD50).ToString(): SetCustomPrimaries names known primaries within 0.001.
    private static string WithSrgbTransfer(float[,] toXyzD50)
    {
        foreach (var (name, primaries) in s_namedPrimaries)
            if (PrimariesToXyzD50(primaries) is { } known && WithinTolerance(toXyzD50, known, 0.001f))
                return $"{{primaries:{name}, transfer:SRGB, matrix:RGB, range:FULL}}";
        // CUSTOM prints its primaries without the "{primaries:" prefix.
        return $"{PrimariesToString(D65Primaries(toXyzD50))}, transfer:SRGB, matrix:RGB, range:FULL}}";
    }

    /// <summary>The <c>toXYZD50</c> matrix skcms reads from an RGB matrix/TRC profile, if Chromium would accept the
    /// profile: parseable, with a white point within 0.04 of D50 (crbug.com/847024).</summary>
    internal static float[,]? IccToXyzD50(byte[]? icc)
    {
        if (icc is null || icc.Length < 132) return null;
        var size = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(0));
        if (size < 132 || size > icc.Length || Tag4(icc, 36) != "acsp" || Tag4(icc, 16) != "RGB ") return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(128));
        if (132 + 12L * count > size) return null;
        var tags = new Dictionary<string, (int Offset, int Size)>();
        for (var i = 0; i < count; i++)
        {
            var entry = 132 + 12 * i;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(entry + 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(entry + 8));
            if ((long)offset + length > size) return null;
            tags.TryAdd(Tag4(icc, entry), ((int)offset, (int)length));
        }
        // skcms only accepts a matrix profile as a source with its curves too.
        if (!tags.ContainsKey("rTRC") || !tags.ContainsKey("gTRC") || !tags.ContainsKey("bTRC")) return null;
        var m = new float[3, 3];
        string[] columns = ["rXYZ", "gXYZ", "bXYZ"];
        for (var c = 0; c < 3; c++)
        {
            if (!tags.TryGetValue(columns[c], out var tag) || tag.Size < 20 || Tag4(icc, tag.Offset) != "XYZ ") return null;
            for (var r = 0; r < 3; r++)
                m[r, c] = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan(tag.Offset + 8 + 4 * r)) * (1.0f / 65536.0f);
        }
        var wX = m[0, 0] + m[0, 1] + m[0, 2];
        var wY = m[1, 0] + m[1, 1] + m[1, 2];
        var wZ = m[2, 0] + m[2, 1] + m[2, 2];
        if (MathF.Abs(wX - 0.96420f) > 0.04f || MathF.Abs(wY - 1.00000f) > 0.04f || MathF.Abs(wZ - 0.82491f) > 0.04f) return null;
        return m;
    }

    private static string Tag4(byte[] data, int offset) => offset + 4 <= data.Length ? System.Text.Encoding.ASCII.GetString(data, offset, 4) : "";

    // ColorSpace::SetCustomPrimaries' kIDsToCheck, in its order, with the SkNamedPrimaries values they map to.
    private static readonly (string Name, float[] Primaries)[] s_namedPrimaries =
    [
        ("BT709", [0.64f, 0.33f, 0.3f, 0.6f, 0.15f, 0.06f, 0.3127f, 0.329f]),
        ("BT470M", [0.67f, 0.33f, 0.21f, 0.71f, 0.14f, 0.08f, 0.31f, 0.316f]),
        ("BT470BG", [0.64f, 0.33f, 0.29f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f]),
        ("SMPTE170M", [0.630f, 0.340f, 0.310f, 0.595f, 0.155f, 0.070f, 0.3127f, 0.3290f]),
        ("SMPTE240M", [0.630f, 0.340f, 0.310f, 0.595f, 0.155f, 0.070f, 0.3127f, 0.3290f]),
        ("FILM", [0.681f, 0.319f, 0.243f, 0.692f, 0.145f, 0.049f, 0.310f, 0.316f]),
        ("BT2020", [0.708f, 0.292f, 0.170f, 0.797f, 0.131f, 0.046f, 0.3127f, 0.3290f]),
        ("SMPTEST428_1", [1f, 0f, 0f, 1f, 0f, 0f, 1f / 3f, 1f / 3f]),
        ("SMPTEST431_2", [0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f, 0.314f, 0.351f]),
        ("P3", [0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f, 0.3127f, 0.3290f]),
        ("XYZ_D50", [1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.34567f, 0.35850f]),
        ("ADOBE_RGB", [0.64f, 0.33f, 0.21f, 0.71f, 0.15f, 0.06f, 0.3127f, 0.3290f]),
        ("APPLE_GENERIC_RGB", [0.63002f, 0.34000f, 0.29505f, 0.60498f, 0.15501f, 0.07701f, 0.3127f, 0.3290f]),
        ("WIDE_GAMUT_COLOR_SPIN", [0.01f, 0.98f, 0.01f, 0.01f, 0.98f, 0.01f, 0.3127f, 0.3290f]),
        ("EBU_3213_E", [0.630f, 0.340f, 0.295f, 0.605f, 0.155f, 0.077f, 0.3127f, 0.3290f]),
    ];

    private static bool WithinTolerance(float[,] a, float[,] b, float tolerance)
    {
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                if (MathF.Abs(a[r, c] - b[r, c]) > tolerance) return false;
        return true;
    }

    // skcms_PrimariesToXYZD50.
    internal static float[,]? PrimariesToXyzD50(float[] p)
    {
        foreach (var v in p)
            if (v is < 0 or > 1) return null;
        float[,] primaries =
        {
            { p[0], p[2], p[4] },
            { p[1], p[3], p[5] },
            { 1 - p[0] - p[1], 1 - p[2] - p[3], 1 - p[4] - p[5] },
        };
        if (Invert(primaries) is not { } inverse) return null;
        float[] white = [p[6] / p[7], 1, (1 - p[6] - p[7]) / p[7]];
        var xyz = Multiply(inverse, white);
        float[,] scale = { { xyz[0], 0, 0 }, { 0, xyz[1], 0 }, { 0, 0, xyz[2] } };
        return Concat(AdaptToXyzD50(p[6], p[7]), Concat(primaries, scale));
    }

    // skcms_AdaptToXYZD50: Bradford.
    private static float[,] AdaptToXyzD50(float wx, float wy)
    {
        float[] white = [wx / wy, 1, (1 - wx - wy) / wy];
        float[] d50 = [0.96422f, 1.0f, 0.82521f];
        float[,] xyzToLms = { { 0.8951f, 0.2664f, -0.1614f }, { -0.7502f, 1.7135f, 0.0367f }, { 0.0389f, -0.0685f, 1.0296f } };
        float[,] lmsToXyz = { { 0.9869929f, -0.1470543f, 0.1599627f }, { 0.4323053f, 0.5183603f, 0.0492912f }, { -0.0085287f, 0.0400428f, 0.9684867f } };
        var src = Multiply(xyzToLms, white);
        var dst = Multiply(xyzToLms, d50);
        float[,] cone = { { dst[0] / src[0], 0, 0 }, { 0, dst[1] / src[1], 0 }, { 0, 0, dst[2] / src[2] } };
        return Concat(lmsToXyz, Concat(cone, xyzToLms));
    }

    // skia::GetD65PrimariesFromToXYZD50Matrix: the xy primaries after adapting back to D65.
    internal static float[] D65Primaries(float[,] toXyzD50)
    {
        const float D65X = 0.3127f, D65Y = 0.3290f;
        var adapt = Invert(AdaptToXyzD50(D65X, D65Y))!;
        var m = Concat(adapt, toXyzD50);
        var sumR = m[0, 0] + m[1, 0] + m[2, 0];
        var sumG = m[0, 1] + m[1, 1] + m[2, 1];
        var sumB = m[0, 2] + m[1, 2] + m[2, 2];
        return [m[0, 0] / sumR, m[1, 0] / sumR, m[0, 1] / sumG, m[1, 1] / sumG, m[0, 2] / sumB, m[1, 2] / sumB, D65X, D65Y];
    }

    // skia::SkColorSpacePrimariesToString; the named forms need bit-exact primaries.
    internal static string PrimariesToString(float[] p)
    {
        static string F(float v) => ((double)v).ToString("F4", CultureInfo.InvariantCulture);
        var name = p.AsSpan().SequenceEqual(s_namedPrimaries[0].Primaries) ? "name:'srgb', "
            : p.AsSpan().SequenceEqual(s_namedPrimaries[9].Primaries) ? "name:'p3', "
            : p.AsSpan().SequenceEqual(s_namedPrimaries[6].Primaries) ? "name:'rec2020', "
            : "";
        return $"{{{name}r:[{F(p[0])}, {F(p[1])}], g:[{F(p[2])}, {F(p[3])}], b:[{F(p[4])}, {F(p[5])}], w:[{F(p[6])}, {F(p[7])}]}}";
    }

    private static float[] Multiply(float[,] m, float[] v)
    {
        var result = new float[3];
        for (var r = 0; r < 3; r++) result[r] = m[r, 0] * v[0] + m[r, 1] * v[1] + m[r, 2] * v[2];
        return result;
    }

    private static float[,] Concat(float[,] a, float[,] b)
    {
        var m = new float[3, 3];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                m[r, c] = a[r, 0] * b[0, c] + a[r, 1] * b[1, c] + a[r, 2] * b[2, c];
        return m;
    }

    // skcms_Matrix3x3_invert: in double, as skcms computes it.
    private static float[,]? Invert(float[,] src)
    {
        double a00 = src[0, 0], a01 = src[1, 0], a02 = src[2, 0];
        double a10 = src[0, 1], a11 = src[1, 1], a12 = src[2, 1];
        double a20 = src[0, 2], a21 = src[1, 2], a22 = src[2, 2];
        double b0 = a00 * a11 - a01 * a10, b1 = a00 * a12 - a02 * a10, b2 = a01 * a12 - a02 * a11;
        double b3 = a20, b4 = a21, b5 = a22;
        var determinant = b0 * b5 - b1 * b4 + b2 * b3;
        if (determinant == 0) return null;
        var invdet = 1.0 / determinant;
        if (invdet > float.MaxValue || invdet < -float.MaxValue || !float.IsFinite((float)invdet)) return null;
        b0 *= invdet; b1 *= invdet; b2 *= invdet; b3 *= invdet; b4 *= invdet; b5 *= invdet;
        var dst = new float[3, 3];
        dst[0, 0] = (float)(a11 * b5 - a12 * b4);
        dst[1, 0] = (float)(a02 * b4 - a01 * b5);
        dst[2, 0] = (float)b2;
        dst[0, 1] = (float)(a12 * b3 - a10 * b5);
        dst[1, 1] = (float)(a00 * b5 - a02 * b3);
        dst[2, 1] = (float)-b1;
        dst[0, 2] = (float)(a10 * b4 - a11 * b3);
        dst[1, 2] = (float)(a01 * b3 - a00 * b4);
        dst[2, 2] = (float)b0;
        foreach (var v in dst)
            if (!float.IsFinite(v)) return null;
        return dst;
    }
}
