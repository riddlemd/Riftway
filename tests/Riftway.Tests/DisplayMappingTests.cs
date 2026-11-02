using System.Buffers.Binary;

namespace Riftway.Tests;

public sealed class DisplayMappingTests
{
    // An RGB matrix/TRC profile: header, tag table, then each tag's data; columns are the D50 rXYZ, gXYZ, bXYZ.
    private static byte[] Icc(double[,] columns, bool withCurves = true, string signature = "acsp")
    {
        var tags = new List<(string Sig, byte[] Data)>();
        string[] names = ["rXYZ", "gXYZ", "bXYZ"];
        for (var c = 0; c < 3; c++)
        {
            var xyz = new byte[20];
            "XYZ "u8.CopyTo(xyz);
            for (var r = 0; r < 3; r++) BinaryPrimitives.WriteInt32BigEndian(xyz.AsSpan(8 + 4 * r), (int)Math.Round(columns[r, c] * 65536));
            tags.Add((names[c], xyz));
        }
        if (withCurves)
        {
            var curve = new byte[12];
            "curv"u8.CopyTo(curve);
            foreach (var name in new[] { "rTRC", "gTRC", "bTRC" }) tags.Add((name, curve));
        }
        var size = 132 + 12 * tags.Count + tags.Sum(t => t.Data.Length);
        var icc = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(icc, (uint)size);
        System.Text.Encoding.ASCII.GetBytes("RGB ").CopyTo(icc, 16);
        System.Text.Encoding.ASCII.GetBytes("XYZ ").CopyTo(icc, 20);
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(icc, 36);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(128), (uint)tags.Count);
        var offset = 132 + 12 * tags.Count;
        for (var i = 0; i < tags.Count; i++)
        {
            System.Text.Encoding.ASCII.GetBytes(tags[i].Sig).CopyTo(icc, 132 + 12 * i);
            BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(132 + 12 * i + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(132 + 12 * i + 8), (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(icc, offset);
            offset += tags[i].Data.Length;
        }
        return icc;
    }

    private static double[,] Columns(float[,] m) => new double[,] { { m[0, 0], m[0, 1], m[0, 2] }, { m[1, 0], m[1, 1], m[1, 2] }, { m[2, 0], m[2, 1], m[2, 2] } };

    // The sRGB profile's D50 colorants, as sRGB ICC files carry them.
    private static readonly double[,] s_srgb = { { 0.436066, 0.385147, 0.143066 }, { 0.222488, 0.716873, 0.060608 }, { 0.013916, 0.097076, 0.714096 } };

    [Fact]
    public void SdrColorSpace_NoProfile_IsSrgb()
    {
        Assert.Equal("{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}", DisplayMapping.SdrColorSpace(null));
    }

    [Fact]
    public void SdrColorSpace_SrgbProfile_NamesBt709()
    {
        Assert.Equal("{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}", DisplayMapping.SdrColorSpace(Icc(s_srgb)));
    }

    [Fact]
    public void SdrColorSpace_DisplayP3Profile_NamesP3()
    {
        double[,] p3 = { { 0.515102, 0.291965, 0.157153 }, { 0.241182, 0.692236, 0.066582 }, { -0.001050, 0.041882, 0.784378 } };
        Assert.Equal("{primaries:P3, transfer:SRGB, matrix:RGB, range:FULL}", DisplayMapping.SdrColorSpace(Icc(p3)));
    }

    [Fact]
    public void SdrColorSpace_CustomPrimaries_PrintsTheirD65Chromaticities()
    {
        // Colorants built from chosen D65 primaries come back as those primaries, printed to four places.
        var matrix = DisplayMapping.PrimariesToXyzD50([0.64f, 0.33f, 0.28f, 0.65f, 0.15f, 0.06f, 0.3127f, 0.3290f])!;
        Assert.Equal("{r:[0.6400, 0.3300], g:[0.2800, 0.6500], b:[0.1500, 0.0600], w:[0.3127, 0.3290]}, transfer:SRGB, matrix:RGB, range:FULL}",
            DisplayMapping.SdrColorSpace(Icc(Columns(matrix))));
    }

    [Fact]
    public void SdrColorSpace_DellXps9315Panel_MatchesADoublePrecisionBradfordAdaptation()
    {
        // This laptop's profile (Final_ICM_Dell_XPS_9315_FHD_plus_LGD06CE.icm); expected xy worked out separately in double.
        double[,] panel = { { 0.514816284179688, 0.323165893554688, 0.126312255859375 }, { 0.2698974609375, 0.668045043945312, 0.0620574951171875 }, { 0.0185546875, 0.110992431640625, 0.695556640625 } };
        Assert.Equal("{r:[0.6317, 0.3353], g:[0.2716, 0.6038], b:[0.1404, 0.0634], w:[0.3127, 0.3290]}, transfer:SRGB, matrix:RGB, range:FULL}",
            DisplayMapping.SdrColorSpace(Icc(panel)));
    }

    [Fact]
    public void SdrColorSpace_WhitePointFarFromD50_IsRejectedAsSrgb()
    {
        var shifted = (double[,])s_srgb.Clone();
        shifted[2, 2] += 0.05;
        Assert.Equal(DisplayMapping.Srgb, DisplayMapping.SdrColorSpace(Icc(shifted)));
        shifted[2, 2] -= 0.02;
        Assert.NotEqual(DisplayMapping.Srgb, DisplayMapping.SdrColorSpace(Icc(shifted)));
    }

    [Fact]
    public void SdrColorSpace_UnusableProfiles_FallBackToSrgb()
    {
        var p3ish = new double[,] { { 0.515102, 0.291965, 0.157153 }, { 0.241182, 0.692236, 0.066582 }, { -0.001050, 0.041882, 0.784378 } };
        Assert.Equal(DisplayMapping.Srgb, DisplayMapping.SdrColorSpace(Icc(p3ish, withCurves: false)));
        Assert.Equal(DisplayMapping.Srgb, DisplayMapping.SdrColorSpace(Icc(p3ish, signature: "xxxx")));
        Assert.Equal(DisplayMapping.Srgb, DisplayMapping.SdrColorSpace(Icc(p3ish)[..100]));
        var claimsMore = Icc(p3ish);
        BinaryPrimitives.WriteUInt32BigEndian(claimsMore, (uint)claimsMore.Length + 1);
        Assert.Equal(DisplayMapping.Srgb, DisplayMapping.SdrColorSpace(claimsMore));
    }

    [Fact]
    public void PrimariesToString_NamesBitExactSrgb()
    {
        Assert.Equal("{name:'srgb', r:[0.6400, 0.3300], g:[0.3000, 0.6000], b:[0.1500, 0.0600], w:[0.3127, 0.3290]}",
            DisplayMapping.PrimariesToString([0.64f, 0.33f, 0.3f, 0.6f, 0.15f, 0.06f, 0.3127f, 0.329f]));
    }

    [Theory]
    [InlineData(false, 24, 8)]
    [InlineData(true, 30, 10)]
    public void Depths_AreChromiumsDefaultOrHdr10(bool hdr, int depth, int perComponent)
    {
        Assert.Equal((depth, perComponent), DisplayMapping.Depths(hdr));
    }

    [Theory]
    [InlineData(0x80000000u, true)]  // INTERNAL
    [InlineData(11u, true)]          // DISPLAYPORT_EMBEDDED
    [InlineData(13u, true)]          // UDI_EMBEDDED
    [InlineData(10u, false)]         // DISPLAYPORT_EXTERNAL
    [InlineData(12u, false)]         // UDI_EXTERNAL
    [InlineData(5u, false)]          // HDMI
    [InlineData(0xFFFFFFFFu, false)] // OTHER, also what a monitor without a path gets
    public void IsInternalOutputTechnology_AsChromium(uint technology, bool expected)
    {
        Assert.Equal(expected, DisplayMapping.IsInternalOutputTechnology(technology));
    }

    [Theory]
    [InlineData(0, 0, 1.0f, 0, 0)]
    [InlineData(1920, 0, 1.5f, 1280, 0)]
    [InlineData(-1366, -10, 1.25f, -1093, -8)]
    [InlineData(2560, 1441, 1.25f, 2048, 1152)]
    public void NativeOrigin_IsThePixelOriginFlooredToDips(int left, int top, float scale, int x, int y)
    {
        Assert.Equal(new Point(x, y), DisplayMapping.NativeOrigin(left, top, scale));
    }
}
