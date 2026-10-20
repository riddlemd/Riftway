using System.Globalization;

namespace Oriel.Tests;

// SkiaHslShift against Chromium's own code, compiled from its sources (HslShiftGolden).
public sealed class SkiaHslShiftTests
{
    [Fact]
    public void EveryCaseMatchesChromiumBitForBit()
    {
        var mismatches = new List<string>();
        foreach (var line in HslShiftGolden.Cases.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split(' ');
            double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
            byte B(int i) => byte.Parse(f[i], CultureInfo.InvariantCulture);
            var input = new ImageBitmap(1, 1, [B(6), B(5), B(4), B(3)]);
            var output = SkiaHslShift.Shift(input, D(0), D(1), D(2)).Bgra;
            byte[] expected = [B(10), B(9), B(8), B(7)];
            if (!output.SequenceEqual(expected)) mismatches.Add($"{line} -> got {output[3]} {output[2]} {output[1]} {output[0]}");
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)) + $"\n({mismatches.Count} mismatches)");
    }

    [Theory]
    [InlineData(0.3, -1, 0.3)]
    [InlineData(-1, -1, -1)]
    [InlineData(-2, -1, -1)]
    [InlineData(1.5, 0.5, 0.5)]
    [InlineData(1, 0.5, 1)]
    public void SafeShiftKeepsARangeOrTheDefault(double value, double fallback, double kept)
        => Assert.Equal(kept, NamedImageRules.SafeShift(value, fallback));
}

public sealed class NativeImageHslShiftTests
{
    [Fact]
    public void ElectronsWhiteAndBlackShiftsAndOutOfRangeValuesThatChangeNothing()
    {
        var image = NativeImage.CreateFromBitmap([40, 120, 200, 255], new NativeImageBitmapOptions { Width = 1, Height = 1 });
        Assert.Equal([255, 255, 255, 255], image.HslShifted([-1, 0, 1]).ToBitmap());
        Assert.Equal([0, 0, 0, 255], image.HslShifted([-1, 1, 0]).ToBitmap());
        // Out of range: hue -1, saturation and lightness 0.5, a copy.
        Assert.Equal([40, 120, 200, 255], image.HslShifted([7, 9, -3]).ToBitmap());
    }
}
