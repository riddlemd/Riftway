using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Oriel;

/// <summary>Electron's <c>Display.colorSpace</c> from a display's ICC profile: a port of the path Chromium takes on macOS,
/// <c>skia::ColorProfile::Make</c> → <c>SkColorSpace::Make</c> → <c>gfx::ColorSpace(SkColorSpace)</c> → <c>ToString()</c>.</summary>
/// <remarks>All arithmetic is single precision, as in skcms and Skia, so the matching tolerances decide the same way.</remarks>
internal static class DisplayColorSpace
{
    /// <summary>What Chromium reports when it cannot use a profile: <c>DisplayColorSpaces</c> falls back to sRGB.</summary>
    internal const string SRgb = "{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}";

    /// <summary>An skcms_TransferFunction.</summary>
    internal readonly record struct TransferFn(float G, float A, float B, float C, float D, float E, float F);

    private static readonly TransferFn s_srgbFn = new(2.4f, (float)(1 / 1.055), (float)(0.055 / 1.055), (float)(1 / 12.92), 0.04045f, 0, 0);
    private static readonly TransferFn s_linearFn = new(1, 1, 0, 0, 0, 0, 0);
    private static readonly TransferFn s_2dot2Fn = new(2.2f, 1, 0, 0, 0, 0, 0);

    // SkNamedGamut::kSRGB, the 16.16 values skcms_sRGB_profile() also carries.
    private static readonly float[,] s_srgbGamut =
    {
        { 0x6FA2 / 65536f, 0x6299 / 65536f, 0x24A0 / 65536f },
        { 0x38F5 / 65536f, 0xB785 / 65536f, 0x0F84 / 65536f },
        { 0x0390 / 65536f, 0x18DA / 65536f, 0xB6CF / 65536f },
    };

    // gfx::ColorSpace::SetCustomPrimaries' search order, with the SkColorSpacePrimaries each id stands for (rx, ry, gx, gy, bx, by, wx, wy).
    private static readonly (string Name, float[] Primaries)[] s_primaryIds =
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

    // gfx::ColorSpace::SetCustomTransferFunction's search order for SDR.
    private static readonly (string Name, TransferFn Fn)[] s_transferIds =
    [
        ("SRGB", s_srgbFn),
        ("LINEAR", s_linearFn),
        ("GAMMA18", new(1.801f, 1, 0, 0, 0, 0, 0)),
        ("GAMMA22", s_2dot2Fn),
        ("GAMMA24", new(2.4f, 1, 0, 0, 0, 0, 0)),
        ("GAMMA28", new(2.8f, 1, 0, 0, 0, 0, 0)),
        ("SMPTE240M", new(2.222222222222f, 0.899626676224f, 0.100373323776f, 0.25f, 0.091286342118f, 0, 0)),
        ("BT709_APPLE", new(1.961f, 1, 0, 0, 0, 0, 0)),
        ("SMPTEST428_1", new(2.6f, 1.034080527699f, 0, 0, 0, 0, 0)),
    ];

    // skcms_252_random_bytes: the samples skcms_ApproximatelyEqualProfiles pushes through both profiles.
    private static readonly byte[] s_samples =
    [
        8, 179, 128, 204, 253, 38, 134, 184, 68, 102, 32, 138, 99, 39, 169, 215,
        119, 26, 3, 223, 95, 239, 52, 132, 114, 74, 81, 234, 97, 116, 244, 205, 30,
        154, 173, 12, 51, 159, 122, 153, 61, 226, 236, 178, 229, 55, 181, 220, 191,
        194, 160, 126, 168, 82, 131, 18, 180, 245, 163, 22, 246, 69, 235, 252, 57,
        108, 14, 6, 152, 240, 255, 171, 242, 20, 227, 177, 238, 96, 85, 16, 211,
        70, 200, 149, 155, 146, 127, 145, 100, 151, 109, 19, 165, 208, 195, 164,
        137, 254, 182, 248, 64, 201, 45, 209, 5, 147, 207, 210, 113, 162, 83, 225,
        9, 31, 15, 231, 115, 37, 58, 53, 24, 49, 197, 56, 120, 172, 48, 21, 214,
        129, 111, 11, 50, 187, 196, 34, 60, 103, 71, 144, 47, 203, 77, 80, 232,
        140, 222, 250, 206, 166, 247, 139, 249, 221, 72, 106, 27, 199, 117, 54,
        219, 135, 118, 40, 79, 41, 251, 46, 93, 212, 92, 233, 148, 28, 121, 63,
        123, 158, 105, 59, 29, 42, 143, 23, 0, 107, 176, 87, 104, 183, 156, 193,
        189, 90, 188, 65, 190, 17, 198, 7, 186, 161, 1, 124, 78, 125, 170, 133,
        174, 218, 67, 157, 75, 101, 89, 217, 62, 33, 141, 228, 25, 35, 91, 230, 4,
        2, 13, 73, 86, 167, 237, 84, 243, 44, 185, 66, 130, 110, 150, 142, 216, 88,
        112, 36, 224, 136, 202, 76, 94, 98, 175, 213,
    ];

    /// <summary>A TRC: parametric when <see cref="Table"/> is empty, else 16-bit big-endian entries.</summary>
    private sealed record Curve(TransferFn Parametric, byte[] Table)
    {
        public int Entries => Table.Length / 2;
    }

    private sealed class Profile
    {
        public Curve[]? Trc;
        public float[,]? ToXyzD50;
    }

    /// <summary>Electron's string, and a key that changes whenever Chromium's <c>gfx::ColorSpace</c> would compare unequal.</summary>
    internal static (string Text, string Key) FromIccProfile(ReadOnlySpan<byte> icc)
    {
        // A profile skcms rejects leaves gfx::ColorSpace invalid, and DisplayColorSpaces keeps its sRGB default.
        if (!TryParse(icc, out var profile)) return (SRgb, SRgb);
        var (fn, matrix) = ComputeSkColorSpace(profile);
        return Describe(fn, matrix);
    }

    // skia::ColorProfile::ComputeSkColorSpace for a profile without CICP.
    private static (TransferFn, float[,]) ComputeSkColorSpace(Profile profile)
    {
        if (Make(profile) is { } made) return made;
        if (profile.ToXyzD50 is not null)
        {
            var withSrgb = new Profile { ToXyzD50 = profile.ToXyzD50, Trc = [new(s_srgbFn, []), new(s_srgbFn, []), new(s_srgbFn, [])] };
            if (Make(withSrgb) is { } adjusted) return adjusted;
        }
        return (s_srgbFn, s_srgbGamut);
    }

    // SkColorSpace::Make(const skcms_ICCProfile&) without CICP.
    private static (TransferFn, float[,])? Make(Profile profile)
    {
        if (ApproximatelyEqualToSrgb(profile)) return (s_srgbFn, s_srgbGamut);
        if (profile.ToXyzD50 is not { } matrix || Invert(matrix) is null) return null;
        if (profile.Trc is not { } trc) return null;
        TransferFn fn;
        if (trc.All(c => c.Entries == 0) && trc[1].Parametric == trc[0].Parametric && trc[2].Parametric == trc[0].Parametric)
            fn = trc[0].Parametric;
        else
        {
            // Tables or mismatched curves: Skia snaps them to sRGB when they approximate it, and otherwise ColorProfile
            // retries with sRGB curves; either way the result carries the sRGB transfer function.
            fn = s_srgbFn;
        }
        return MakeRgb(fn, matrix);
    }

    // SkColorSpace::MakeRGB, with its snapping to named transfer functions.
    private static (TransferFn, float[,])? MakeRgb(TransferFn fn, float[,] matrix)
    {
        if (!IsSrgbish(fn)) return null;
        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.001f;
        static bool AllNear(TransferFn x, TransferFn y)
            => Near(x.A, y.A) && Near(x.B, y.B) && Near(x.C, y.C) && Near(x.D, y.D) && Near(x.E, y.E) && Near(x.F, y.F) && Near(x.G, y.G);
        static bool GamutNearSrgb(float[,] m)
        {
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    if (!(MathF.Abs(m[r, c] - s_srgbGamut[r, c]) < 0.01f)) return false;
            return true;
        }
        if (AllNear(s_srgbFn, fn)) return GamutNearSrgb(matrix) ? (s_srgbFn, s_srgbGamut) : (s_srgbFn, matrix);
        if (Near(1, fn.A) && Near(0, fn.B) && Near(0, fn.E) && Near(2.2f, fn.G) && fn.D <= 0) return (s_2dot2Fn, matrix);
        var linearExp = Near(1, fn.A) && Near(0, fn.B) && Near(0, fn.E) && Near(1, fn.G) && fn.D <= 0;
        var linearFn = Near(1, fn.C) && Near(0, fn.F) && fn.D >= 1;
        if (linearExp || linearFn) return (s_linearFn, matrix);
        return (fn, matrix);
    }

    // gfx::ColorSpace(const SkColorSpace&) then ToString().
    internal static (string Text, string Key) Describe(TransferFn fn, float[,] matrix)
    {
        var transfer = s_transferIds.FirstOrDefault(t => WithinTolerance(fn, t.Fn)).Name;
        var primaries = s_primaryIds.FirstOrDefault(p => WithinTolerance(matrix, PrimariesToXyzD50(p.Primaries)!)).Name;

        var text = new StringBuilder();
        if (primaries is not null) text.Append("{primaries:").Append(primaries);
        else text.Append(PrimariesToString(D65PrimariesFromMatrix(matrix)));
        text.Append(", transfer:").Append(transfer ?? TransferToString(fn)).Append(", matrix:RGB, range:FULL}");

        var key = new StringBuilder(text.ToString());
        if (primaries is null) foreach (var v in matrix) key.Append(';').Append(v.ToString("R", CultureInfo.InvariantCulture));
        if (transfer is null) key.Append(';').Append(fn.ToString());
        return (text.ToString(), key.ToString());
    }

    private static bool WithinTolerance(TransferFn x, TransferFn y)
        => MathF.Abs(x.A - y.A) <= 0.001f && MathF.Abs(x.B - y.B) <= 0.001f && MathF.Abs(x.C - y.C) <= 0.001f
        && MathF.Abs(x.D - y.D) <= 0.001f && MathF.Abs(x.E - y.E) <= 0.001f && MathF.Abs(x.F - y.F) <= 0.001f
        && MathF.Abs(x.G - y.G) <= 0.001f;

    private static bool WithinTolerance(float[,] x, float[,] y)
    {
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                if (MathF.Abs(x[r, c] - y[r, c]) > 0.001f) return false;
        return true;
    }

    private static string F4(float value) => ((double)value).ToString("F4", CultureInfo.InvariantCulture);

    // skia::SkcmsTransferFunctionToString.
    private static string TransferToString(TransferFn fn)
        => $"{{{F4(fn.C)}*x + {F4(fn.F)} if abs(x) < {F4(fn.D)} else sign(x)*(({F4(fn.A)}*abs(x) + {F4(fn.B)})**{F4(fn.G)} + {F4(fn.E)})}}";

    // skia::SkColorSpacePrimariesToString.
    private static string PrimariesToString(float[] p)
    {
        var name = p.SequenceEqual(s_primaryIds[0].Primaries) ? "name:'srgb', "
            : p.SequenceEqual(s_primaryIds[9].Primaries) ? "name:'p3', "
            : p.SequenceEqual(s_primaryIds[6].Primaries) ? "name:'rec2020', " : "";
        return $"{{{name}r:[{F4(p[0])}, {F4(p[1])}], g:[{F4(p[2])}, {F4(p[3])}], b:[{F4(p[4])}, {F4(p[5])}], w:[{F4(p[6])}, {F4(p[7])}]}}";
    }

    // skia::GetD65PrimariesFromToXYZD50Matrix.
    private static float[] D65PrimariesFromMatrix(float[,] d50)
    {
        const float x = 0.3127f, y = 0.3290f;
        var toD50 = AdaptToXyzD50(x, y)!;
        var toD65 = Invert(toD50)!;
        var m = Concat(toD65, d50);
        var sumR = m[0, 0] + m[1, 0] + m[2, 0];
        var sumG = m[0, 1] + m[1, 1] + m[2, 1];
        var sumB = m[0, 2] + m[1, 2] + m[2, 2];
        return [m[0, 0] / sumR, m[1, 0] / sumR, m[0, 1] / sumG, m[1, 1] / sumG, m[0, 2] / sumB, m[1, 2] / sumB, x, y];
    }

    // skcms_ApproximatelyEqualProfiles(profile, skcms_sRGB_profile()): both transform the same bytes to XYZD50 within 1.
    private static bool ApproximatelyEqualToSrgb(Profile profile)
    {
        if (profile.Trc is not { } trc || profile.ToXyzD50 is not { } matrix) return false;
        Curve[] srgb = [new(s_srgbFn, []), new(s_srgbFn, []), new(s_srgbFn, [])];
        for (var i = 0; i < s_samples.Length; i += 3)
        {
            var a = ToXyzBytes(trc, matrix, i);
            var b = ToXyzBytes(srgb, s_srgbGamut, i);
            for (var c = 0; c < 3; c++)
                if (Math.Abs(a[c] - b[c]) > 1) return false;
        }
        return true;
    }

    // One RGB_888 pixel through skcms_Transform's TRC → matrix → clamp → store_888 stages.
    private static int[] ToXyzBytes(Curve[] trc, float[,] matrix, int offset)
    {
        Span<float> rgb = stackalloc float[3];
        for (var c = 0; c < 3; c++) rgb[c] = ApplyCurve(trc[c], s_samples[offset + c] * (1 / 255.0f));
        var result = new int[3];
        for (var r = 0; r < 3; r++)
        {
            var v = matrix[r, 0] * rgb[0] + matrix[r, 1] * rgb[1] + matrix[r, 2] * rgb[2];
            v = MathF.Max(0, MathF.Min(v, 1));
            result[r] = (int)(v * 255 + 0.5f);
        }
        return result;
    }

    private static float ApplyCurve(Curve curve, float x)
    {
        if (curve.Entries > 0)
        {
            var ix = MathF.Max(0, MathF.Min(x, 1)) * (curve.Entries - 1);
            var lo = (int)ix;
            var hi = (int)MinusOneUlp(ix + 1.0f);
            var t = ix - lo;
            var l = BinaryPrimitives.ReadUInt16BigEndian(curve.Table.AsSpan(2 * lo)) * (1 / 65535.0f);
            var h = BinaryPrimitives.ReadUInt16BigEndian(curve.Table.AsSpan(2 * hi)) * (1 / 65535.0f);
            return l + (h - l) * t;
        }
        var fn = curve.Parametric;
        var sign = x < 0 ? -1.0f : 1.0f;
        x *= sign;
        // A pure gamma takes skcms' gamma stage, which skips the linear segment.
        if (fn is { A: 1, B: 0, C: 0, D: 0, E: 0, F: 0 }) return sign * (fn.G == 1 ? x : Pow(x, fn.G));
        return sign * (x < fn.D ? fn.C * x + fn.F : Pow(fn.A * x + fn.B, fn.G) + fn.E);
    }

    private static float MinusOneUlp(float x) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) - 1);

    // skcms' approx_pow: approx_exp2(approx_log2(x) * y).
    private static float Pow(float x, float y)
    {
        if (x == 0 || x == 1) return x;
        var bits = BitConverter.SingleToInt32Bits(x);
        var e = bits * (1.0f / (1 << 23));
        var m = BitConverter.Int32BitsToSingle((bits & 0x007fffff) | 0x3f000000);
        var log2 = e - 124.225514990f - 1.498030302f * m - 1.725879990f / (0.3520887068f + m);
        var p = log2 * y;
        var fract = p - MathF.Floor(p);
        var fbits = (1.0f * (1 << 23)) * (p + 121.274057500f - 1.490129070f * fract + 27.728023300f / (4.84252568f - fract));
        fbits = MathF.Min(MathF.Max(fbits, 0), 0x7f800000);
        return BitConverter.Int32BitsToSingle((int)fbits);
    }

    // classify() == skcms_TFType_sRGBish.
    private static bool IsSrgbish(TransferFn fn)
        => fn.G >= 0 && float.IsFinite(fn.A + fn.B + fn.C + fn.D + fn.E + fn.F + fn.G)
        && fn.A >= 0 && fn.C >= 0 && fn.D >= 0 && fn.A * fn.D + fn.B >= 0;

    // skcms_ParseWithA2BPriority, for the parts SkColorSpace::Make reads.
    private static bool TryParse(ReadOnlySpan<byte> buf, out Profile profile)
    {
        profile = new Profile();
        if (buf.Length < 132) return false;
        var size = BinaryPrimitives.ReadUInt32BigEndian(buf);
        var version = BinaryPrimitives.ReadUInt32BigEndian(buf[8..]);
        var dataColorSpace = BinaryPrimitives.ReadUInt32BigEndian(buf[16..]);
        var pcs = BinaryPrimitives.ReadUInt32BigEndian(buf[20..]);
        var signature = BinaryPrimitives.ReadUInt32BigEndian(buf[36..]);
        var illuminant = new[] { Fixed(buf[68..]), Fixed(buf[72..]), Fixed(buf[76..]) };
        var tagCount = BinaryPrimitives.ReadUInt32BigEndian(buf[128..]);
        if (signature != Sig("acsp") || size > buf.Length || size < 132 + (ulong)tagCount * 12 || version >> 24 > 4) return false;
        if (MathF.Abs(illuminant[0] - 0.9642f) > 0.0100f || MathF.Abs(illuminant[1] - 1.0000f) > 0.0100f
            || MathF.Abs(illuminant[2] - 0.8249f) > 0.0100f) return false;
        buf = buf[..(int)size];
        var tags = new Dictionary<uint, (int Offset, int Size)>();
        for (var i = 0; i < (int)tagCount; i++)
        {
            var entry = buf[(132 + 12 * i)..];
            var tagSignature = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (length < 4 || (ulong)offset + length > size) return false;
            tags.TryAdd(tagSignature, ((int)offset, (int)length));
        }
        if (pcs != Sig("XYZ ") && pcs != Sig("Lab ")) return false;
        ReadOnlySpan<byte> Tag((int Offset, int Size) t, ReadOnlySpan<byte> all) => all.Slice(t.Offset, t.Size);

        if (dataColorSpace == Sig("GRAY") && tags.TryGetValue(Sig("kTRC"), out var kTrc))
        {
            if (ReadCurve(Tag(kTrc, buf)) is not { } gray) return false;
            profile.Trc = [gray, gray, gray];
            if (pcs == Sig("XYZ ")) profile.ToXyzD50 = new float[,] { { illuminant[0], 0, 0 }, { 0, illuminant[1], 0 }, { 0, 0, illuminant[2] } };
        }
        else
        {
            if (tags.TryGetValue(Sig("rTRC"), out var r) && tags.TryGetValue(Sig("gTRC"), out var g) && tags.TryGetValue(Sig("bTRC"), out var b))
            {
                if (ReadCurve(Tag(r, buf)) is not { } rc || ReadCurve(Tag(g, buf)) is not { } gc || ReadCurve(Tag(b, buf)) is not { } bc) return false;
                profile.Trc = [rc, gc, bc];
            }
            if (tags.TryGetValue(Sig("rXYZ"), out var rx) && tags.TryGetValue(Sig("gXYZ"), out var gx) && tags.TryGetValue(Sig("bXYZ"), out var bx))
            {
                var m = new float[3, 3];
                if (!ReadXyz(Tag(rx, buf), m, 0) || !ReadXyz(Tag(gx, buf), m, 1) || !ReadXyz(Tag(bx, buf), m, 2)) return false;
                profile.ToXyzD50 = m;
            }
        }
        // usable_as_src. The A2B tables themselves never reach SkColorSpace::Make, so only their presence matters here.
        var hasA2B = tags.ContainsKey(Sig("A2B0")) || tags.ContainsKey(Sig("A2B1")) || tags.ContainsKey(Sig("A2B2"));
        return hasA2B || (profile.Trc is not null && profile.ToXyzD50 is not null);
    }

    private static uint Sig(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    private static float Fixed(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadInt32BigEndian(p) * (1.0f / 65536.0f);

    private static bool ReadXyz(ReadOnlySpan<byte> tag, float[,] m, int column)
    {
        if (tag.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(tag) != Sig("XYZ ")) return false;
        m[0, column] = Fixed(tag[8..]);
        m[1, column] = Fixed(tag[12..]);
        m[2, column] = Fixed(tag[16..]);
        return true;
    }

    private static Curve? ReadCurve(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 4) return null;
        var type = BinaryPrimitives.ReadUInt32BigEndian(tag);
        if (type == Sig("para"))
        {
            if (tag.Length < 12) return null;
            var kind = BinaryPrimitives.ReadUInt16BigEndian(tag[8..]);
            if (kind > 4) return null;
            int[] bytes = [4, 12, 16, 20, 28];
            if (tag.Length < 12 + bytes[kind]) return null;
            var v = tag[12..];
            float g = Fixed(v), a = 1, b = 0, c = 0, d = 0, e = 0, f = 0;
            switch (kind)
            {
                case 1:
                    a = Fixed(v[4..]); b = Fixed(v[8..]);
                    if (a == 0) return null;
                    d = -b / a;
                    break;
                case 2:
                    a = Fixed(v[4..]); b = Fixed(v[8..]); e = Fixed(v[12..]);
                    if (a == 0) return null;
                    d = -b / a;
                    f = e;
                    break;
                case 3:
                    a = Fixed(v[4..]); b = Fixed(v[8..]); c = Fixed(v[12..]); d = Fixed(v[16..]);
                    break;
                case 4:
                    a = Fixed(v[4..]); b = Fixed(v[8..]); c = Fixed(v[12..]); d = Fixed(v[16..]); e = Fixed(v[20..]); f = Fixed(v[24..]);
                    break;
            }
            var fn = new TransferFn(g, a, b, c, d, e, f);
            return IsSrgbish(fn) ? new Curve(fn, []) : null;
        }
        if (type == Sig("curv"))
        {
            if (tag.Length < 12) return null;
            var count = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
            if ((ulong)tag.Length < 12 + (ulong)count * 2) return null;
            if (count == 0) return new Curve(s_linearFn, []);
            if (count == 1) return new Curve(new(BinaryPrimitives.ReadUInt16BigEndian(tag[12..]) * (1.0f / 256.0f), 1, 0, 0, 0, 0, 0), []);
            if (count > 1 << 24) return null;
            return new Curve(default, tag.Slice(12, (int)count * 2).ToArray());
        }
        return null;
    }

    // skcms_PrimariesToXYZD50.
    internal static float[,]? PrimariesToXyzD50(float[] p)
    {
        if (p.Any(v => v is < 0 or > 1)) return null;
        var primaries = new float[,] { { p[0], p[2], p[4] }, { p[1], p[3], p[5] }, { 1 - p[0] - p[1], 1 - p[2] - p[3], 1 - p[4] - p[5] } };
        if (Invert(primaries) is not { } inverse) return null;
        float[] white = [p[6] / p[7], 1, (1 - p[6] - p[7]) / p[7]];
        var xyz = Multiply(inverse, white);
        var toXyz = Concat(primaries, new float[,] { { xyz[0], 0, 0 }, { 0, xyz[1], 0 }, { 0, 0, xyz[2] } });
        return AdaptToXyzD50(p[6], p[7]) is { } adapt ? Concat(adapt, toXyz) : null;
    }

    // skcms_AdaptToXYZD50: Bradford.
    private static float[,]? AdaptToXyzD50(float wx, float wy)
    {
        if (wx is < 0 or > 1 || wy is < 0 or > 1) return null;
        float[] white = [wx / wy, 1, (1 - wx - wy) / wy];
        float[] d50 = [0.96422f, 1.0f, 0.82521f];
        var xyzToLms = new float[,] { { 0.8951f, 0.2664f, -0.1614f }, { -0.7502f, 1.7135f, 0.0367f }, { 0.0389f, -0.0685f, 1.0296f } };
        var lmsToXyz = new float[,] { { 0.9869929f, -0.1470543f, 0.1599627f }, { 0.4323053f, 0.5183603f, 0.0492912f }, { -0.0085287f, 0.0400428f, 0.9684867f } };
        var src = Multiply(xyzToLms, white);
        var dst = Multiply(xyzToLms, d50);
        var m = new float[,] { { dst[0] / src[0], 0, 0 }, { 0, dst[1] / src[1], 0 }, { 0, 0, dst[2] / src[2] } };
        return Concat(lmsToXyz, Concat(m, xyzToLms));
    }

    private static float[] Multiply(float[,] m, float[] v)
    {
        var r = new float[3];
        for (var row = 0; row < 3; row++) r[row] = m[row, 0] * v[0] + m[row, 1] * v[1] + m[row, 2] * v[2];
        return r;
    }

    private static float[,] Concat(float[,] a, float[,] b)
    {
        var m = new float[3, 3];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                m[r, c] = a[r, 0] * b[0, c] + a[r, 1] * b[1, c] + a[r, 2] * b[2, c];
        return m;
    }

    // skcms_Matrix3x3_invert: in double, then back to float.
    private static float[,]? Invert(float[,] src)
    {
        double a00 = src[0, 0], a01 = src[1, 0], a02 = src[2, 0];
        double a10 = src[0, 1], a11 = src[1, 1], a12 = src[2, 1];
        double a20 = src[0, 2], a21 = src[1, 2], a22 = src[2, 2];
        double b0 = a00 * a11 - a01 * a10, b1 = a00 * a12 - a02 * a10, b2 = a01 * a12 - a02 * a11, b3 = a20, b4 = a21, b5 = a22;
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
