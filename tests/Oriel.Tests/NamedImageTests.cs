using Oriel.Cef.Platform;

namespace Oriel.Tests;

// Electron v44's NativeImage::CreateFromNamedImage and CreateMenuSymbol.
public sealed class NamedImageRulesTests
{
    [Theory]
    [InlineData("NSImageNameActionTemplate", "NSActionTemplate")]
    [InlineData("NSActionTemplate", "NSActionTemplate")]
    [InlineData("square.and.pencil", "square.and.pencil")]
    [InlineData("ImageNameImageName", "ImageName")]
    public void TheFirstImageNameIsDropped(string name, string normalized) => Assert.Equal(normalized, NamedImageRules.Normalize(name));

    [Theory]
    [InlineData("NSActionTemplate", false)]
    [InlineData("NXFollowLinkFreestandingTemplate", false)]
    [InlineData("folder", true)]
    [InlineData("ns.lowercase", true)]
    public void OnlyNamesWithoutNsOrNxAreSymbols(string name, bool symbol) => Assert.Equal(symbol, NamedImageRules.IsSymbol(name));

    [Theory]
    [InlineData(SymbolWeight.UltraLight, -0.8)]
    [InlineData(SymbolWeight.Thin, -0.6)]
    [InlineData(SymbolWeight.Light, -0.4)]
    [InlineData(SymbolWeight.Regular, 0.0)]
    [InlineData(SymbolWeight.Medium, 0.23)]
    [InlineData(SymbolWeight.Semibold, 0.3)]
    [InlineData(SymbolWeight.Bold, 0.4)]
    [InlineData(SymbolWeight.Heavy, 0.56)]
    [InlineData(SymbolWeight.Black, 0.62)]
    public void WeightsAreAppKits(SymbolWeight weight, double value) => Assert.Equal(value, NamedImageRules.FontWeight(weight));

    [Theory]
    [InlineData(SymbolScale.Small, 1L)]
    [InlineData(SymbolScale.Medium, 2L)]
    [InlineData(SymbolScale.Large, 3L)]
    public void ScalesAreAppKits(SymbolScale scale, long value) => Assert.Equal(value, NamedImageRules.ImageSymbolScale(scale));

    [Theory]
    [InlineData(30, 20, 15, 10)]
    [InlineData(20, 20, 15, 15)]
    [InlineData(15, 16, 14, 15)]
    [InlineData(10, 20, 7, 15)]
    [InlineData(14, 15, 14, 15)]
    public void AMenuSymbolIs15WideUnlessThatMakesIt16Tall(int width, int height, int newWidth, int newHeight)
        => Assert.Equal((newWidth, newHeight), NamedImageRules.MenuSymbolSize(width, height));

    [Fact]
    public void OffMacBothAreEmpty()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.True(NativeImage.CreateFromNamedImage("NSActionTemplate").IsEmpty());
        Assert.True(NativeImage.CreateMenuSymbol("folder").IsEmpty());
        Assert.Throws<PlatformNotSupportedException>(() => NativeImage.CreateEmpty().GetNativeHandle());
    }
}

public sealed class MacNamedImagesTests
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    [Fact]
    public void ANamedImageAndASymbolComeBackAsPng_AndUnknownNamesAsNothing()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(PngSignature, MacNamedImages.Named("NSActionTemplate")![..4]);
        Assert.Equal(PngSignature, MacNamedImages.Symbol("folder", 30, 0, 2)![..4]);
        Assert.Null(MacNamedImages.Named("NSNoSuchImageOriel"));
        Assert.Null(MacNamedImages.Symbol("no.such.symbol.oriel", 30, 0, 2));
    }

    [Fact]
    public void AnEmptyImageHasAnEmptyHandle()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Empty(NativeImage.CreateEmpty().GetNativeHandle());
    }
}
