using System.Buffers.Binary;
using System.Text;

namespace Riftway.Tests;

public sealed class DisplayColorSpaceTests
{
    // rXYZ, gXYZ, bXYZ as 16.16: skcms' sRGB, Apple's Display P3 (as in a MacBook's "Color LCD" profile),
    // and primaries r(0.66, 0.32) g(0.28, 0.62) b(0.14, 0.07) D65 that match no named gamut.
    private static readonly int[][] s_srgb = [[0x6FA2, 0x38F5, 0x0390], [0x6299, 0xB785, 0x18DA], [0x24A0, 0x0F84, 0xB6CF]];
    private static readonly int[][] s_p3 = [[33759, 15807, -69], [19135, 45367, 2745], [10296, 4363, 51385]];
    private static readonly int[][] s_custom = [[32526, 15665, 641], [21974, 45073, 5951], [8691, 4798, 47489]];

    private static readonly double[] s_srgbCurve = [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045];

    [Fact]
    public void SrgbProfile_IsBt709WithTheSrgbCurve()
        => Assert.Equal("{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}", Text(Profile(s_srgb, Para(3, s_srgbCurve))));

    [Fact]
    public void DisplayP3Profile_IsP3()
        => Assert.Equal("{primaries:P3, transfer:SRGB, matrix:RGB, range:FULL}", Text(Profile(s_p3, Para(3, s_srgbCurve))));

    [Fact]
    public void TableCurves_TakeTheSrgbCurve()
    {
        // A MacBook panel's profile: 1024-entry tables shaped like a 1.8 gamma, nowhere near sRGB.
        var table = Enumerable.Range(0, 1024).Select(i => (ushort)Math.Round(Math.Pow(i / 1023.0, 1.8) * 65535)).ToArray();
        Assert.Equal("{primaries:P3, transfer:SRGB, matrix:RGB, range:FULL}", Text(Profile(s_p3, Curv(table))));
    }

    [Theory]
    [InlineData(1.961, "BT709_APPLE")]
    [InlineData(2.2, "GAMMA22")]
    [InlineData(1.8, "GAMMA18")]
    [InlineData(2.4, "GAMMA24")]
    [InlineData(1.0, "LINEAR")]
    public void PureGamma_NamesTheTransfer(double gamma, string transfer)
        => Assert.Equal($"{{primaries:BT709, transfer:{transfer}, matrix:RGB, range:FULL}}", Text(Profile(s_srgb, Para(0, [gamma]))));

    [Fact]
    public void UnnamedGamut_PrintsItsD65Primaries()
        => Assert.Equal(
            "{r:[0.6600, 0.3200], g:[0.2800, 0.6200], b:[0.1400, 0.0700], w:[0.3127, 0.3290]}, transfer:SRGB, matrix:RGB, range:FULL}",
            Text(Profile(s_custom, Para(3, s_srgbCurve))));

    [Fact]
    public void UnnamedCurve_PrintsItsParameters()
        => Assert.Equal(
            "{primaries:P3, transfer:{0.0000*x + 0.0000 if abs(x) < 0.0000 else sign(x)*((1.0000*abs(x) + 0.0000)**2.6000 + 0.0000)}, matrix:RGB, range:FULL}",
            Text(Profile(s_p3, Para(0, [2.6]))));

    [Fact]
    public void CurveWithinAByteOfSrgb_SnapsToSrgb()
        // 2.41 misses every named curve by more than 0.001, but its output stays within a byte of sRGB's.
        => Assert.Equal("{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}",
            Text(Profile(s_srgb, Para(3, [2.41, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045]))));

    [Fact]
    public void CurveAByteOffSrgb_StaysCustom()
        => Assert.StartsWith("{primaries:BT709, transfer:{0.0774*x", Text(Profile(s_srgb, Para(3, [2.6, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045]))));

    [Theory]
    [InlineData("none")]
    [InlineData("signature")]
    [InlineData("illuminant")]
    [InlineData("no-trc")]
    public void UnusableProfile_FallsBackToSrgb(string damage)
    {
        var profile = Profile(s_p3, Para(3, s_srgbCurve), withTrc: damage != "no-trc");
        if (damage == "none") profile = [];
        if (damage == "signature") profile[36] = (byte)'x';
        if (damage == "illuminant") BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(68), 65536);
        Assert.Equal(DisplayColorSpace.SRgb, Text(profile));
    }

    [Fact]
    public void Key_TellsApartSpacesThatPrintTheSame()
    {
        var a = DisplayColorSpace.FromIccProfile(Profile(s_p3, Para(0, [2.6])));
        var b = DisplayColorSpace.FromIccProfile(Profile(s_p3, Para(0, [2.60002])));
        Assert.Equal(a.Text, b.Text);
        Assert.NotEqual(a.Key, b.Key);
    }

    private static string Text(byte[] icc) => DisplayColorSpace.FromIccProfile(icc).Text;

    private static byte[] Para(int kind, double[] values)
    {
        var tag = new byte[12 + 4 * values.Length];
        Encoding.ASCII.GetBytes("para").CopyTo(tag, 0);
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(8), (ushort)kind);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(12 + 4 * i), (int)Math.Round(values[i] * 65536));
        return tag;
    }

    private static byte[] Curv(ushort[] table)
    {
        var tag = new byte[12 + 2 * table.Length];
        Encoding.ASCII.GetBytes("curv").CopyTo(tag, 0);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(8), (uint)table.Length);
        for (var i = 0; i < table.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(12 + 2 * i), table[i]);
        return tag;
    }

    // A v4 display profile: D50 illuminant, rXYZ/gXYZ/bXYZ, and one curve shared by all three TRC tags.
    private static byte[] Profile(int[][] columns, byte[] curve, bool withTrc = true)
    {
        var tags = new List<(string Sig, byte[] Data)>();
        string[] names = ["rXYZ", "gXYZ", "bXYZ"];
        for (var c = 0; c < 3; c++)
        {
            var xyz = new byte[20];
            Encoding.ASCII.GetBytes("XYZ ").CopyTo(xyz, 0);
            for (var i = 0; i < 3; i++) BinaryPrimitives.WriteInt32BigEndian(xyz.AsSpan(8 + 4 * i), columns[c][i]);
            tags.Add((names[c], xyz));
        }
        if (withTrc) foreach (var name in new[] { "rTRC", "gTRC", "bTRC" }) tags.Add((name, curve));
        var size = 132 + 12 * tags.Count + tags.Sum(t => (t.Data.Length + 3) & ~3);
        var icc = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(icc, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(8), 0x04000000);
        Encoding.ASCII.GetBytes("mntrRGB XYZ ").CopyTo(icc, 12);
        Encoding.ASCII.GetBytes("acsp").CopyTo(icc, 36);
        BinaryPrimitives.WriteInt32BigEndian(icc.AsSpan(68), 63190);
        BinaryPrimitives.WriteInt32BigEndian(icc.AsSpan(72), 65536);
        BinaryPrimitives.WriteInt32BigEndian(icc.AsSpan(76), 54061);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(128), (uint)tags.Count);
        var offset = 132 + 12 * tags.Count;
        for (var i = 0; i < tags.Count; i++)
        {
            var entry = icc.AsSpan(132 + 12 * i);
            Encoding.ASCII.GetBytes(tags[i].Sig).CopyTo(icc, 132 + 12 * i);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(icc, offset);
            offset += (tags[i].Data.Length + 3) & ~3;
        }
        return icc;
    }
}
