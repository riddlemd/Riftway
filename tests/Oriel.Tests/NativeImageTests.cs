namespace Oriel.Tests;

// The paths here never reach CEF: raw bitmaps are not PNG or JPEG, so decoding stops at the magic bytes.
public sealed class NativeImageTests
{
    private static byte[] Solid(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
        return pixels;
    }

    private static NativeImage Bitmap(int width, int height, double scale = 1, byte b = 1)
        => NativeImage.CreateFromBitmap(Solid(width, height, b, 2, 3), new NativeImageBitmapOptions { Width = width, Height = height, ScaleFactor = scale });

    [Fact]
    public void CreateEmpty_IsEmpty_WithNoSizeOrPixels()
    {
        var image = NativeImage.CreateEmpty();
        Assert.True(image.IsEmpty());
        Assert.Equal((0, 0), image.GetSize());
        Assert.Equal(0, image.GetAspectRatio());
        Assert.Empty(image.ToBitmap());
        Assert.Empty(image.ToPng());
        Assert.Empty(image.GetScaleFactors());
    }

    [Theory]
    [InlineData(2, 2, 15)]
    [InlineData(0, 2, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(2, 2, 17)]
    public void CreateFromBitmap_WrongSize_Throws(int width, int height, int length)
        => Assert.Throws<ArgumentException>(() => NativeImage.CreateFromBitmap(new byte[length], new NativeImageBitmapOptions { Width = width, Height = height }));

    [Fact]
    public void CreateFromBitmap_KeepsAPrivateCopy()
    {
        var pixels = Solid(2, 2, 9, 9, 9);
        var image = NativeImage.CreateFromBitmap(pixels, new NativeImageBitmapOptions { Width = 2, Height = 2 });
        Assert.False(image.IsEmpty());
        pixels[0] = 0;
        var out1 = image.ToBitmap();
        out1[1] = 0;
        Assert.Equal(Solid(2, 2, 9, 9, 9), image.ToBitmap());
    }

    [Fact]
    public void GetSize_IsInDips_FromTheClosestRepresentation()
    {
        var image = Bitmap(10, 6);
        image.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, Width = 20, Height = 12, Buffer = Solid(20, 12, 5, 5, 5) });
        Assert.Equal([1.0, 2.0], image.GetScaleFactors());
        Assert.Equal((10, 6), image.GetSize());
        Assert.Equal((10, 6), image.GetSize(2));
        Assert.Equal(20 * 12 * 4, image.ToBitmap(2).Length);
        Assert.Equal(10 * 6 * 4, image.ToBitmap(1.2).Length);
        Assert.Equal(10.0 / 6, image.GetAspectRatio(), 6);
    }

    [Fact]
    public void Closest_OnATie_PrefersTheLargerScale()
    {
        var image = Bitmap(4, 4, 1);
        image.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, Width = 8, Height = 8, Buffer = Solid(8, 8, 1, 1, 1) });
        Assert.Equal(8 * 8 * 4, image.ToBitmap(1.5).Length);
    }

    [Fact]
    public void AddRepresentation_ReplacesTheSameScale()
    {
        var image = Bitmap(2, 2, 1, b: 1);
        image.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 1, Width = 2, Height = 2, Buffer = Solid(2, 2, 7, 2, 3) });
        Assert.Equal([1.0], image.GetScaleFactors());
        Assert.Equal(7, image.ToBitmap()[0]);
    }

    [Fact]
    public void AddRepresentation_Unreadable_Throws()
    {
        var image = NativeImage.CreateEmpty();
        Assert.Throws<ArgumentException>(() => image.AddRepresentation(new AddRepresentationOptions { Width = 2, Height = 2, Buffer = new byte[15] }));
        Assert.Throws<ArgumentException>(() => image.AddRepresentation(new AddRepresentationOptions { DataUrl = "data:text/plain,hi" }));
        Assert.Throws<ArgumentException>(() => image.AddRepresentation(new AddRepresentationOptions()));
        Assert.True(image.IsEmpty());
    }

    [Fact]
    public void CreateFromBuffer_RawBitmap_NeedsTheExactSize()
    {
        var image = NativeImage.CreateFromBuffer(Solid(4, 2, 1, 2, 3), new NativeImageBitmapOptions { Width = 4, Height = 2, ScaleFactor = 2 });
        Assert.Equal([2.0], image.GetScaleFactors());
        Assert.Equal((2, 1), image.GetSize(2));
        Assert.True(NativeImage.CreateFromBuffer(Solid(4, 2, 1, 2, 3), new NativeImageBitmapOptions { Width = 4, Height = 3 }).IsEmpty());
        Assert.True(NativeImage.CreateFromBuffer(Solid(4, 2, 1, 2, 3)).IsEmpty());
    }

    [Fact]
    public void Crop_CutsEveryRepresentationAtItsScale()
    {
        var image = Bitmap(10, 10);
        image.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, Width = 20, Height = 20, Buffer = Solid(20, 20, 4, 4, 4) });
        var cropped = image.Crop(new Rectangle(2, 3, 4, 5));
        Assert.Equal([1.0, 2.0], cropped.GetScaleFactors());
        Assert.Equal((4, 5), cropped.GetSize());
        Assert.Equal(8 * 10 * 4, cropped.ToBitmap(2).Length);
    }

    [Fact]
    public void Crop_OutsideTheImage_IsEmpty()
        => Assert.True(Bitmap(4, 4).Crop(new Rectangle(10, 10, 2, 2)).IsEmpty());

    [Fact]
    public void Resize_KeepsTheAspectRatio_AndEveryScale()
    {
        var image = Bitmap(10, 4);
        image.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, Width = 20, Height = 8, Buffer = Solid(20, 8, 4, 4, 4) });
        var resized = image.Resize(new ResizeOptions { Width = 5 });
        Assert.Equal((5, 2), resized.GetSize());
        Assert.Equal(10 * 4 * 4, resized.ToBitmap(2).Length);
        Assert.Equal((3, 7), image.Resize(new ResizeOptions { Width = 3, Height = 7 }).GetSize());
    }

    [Fact]
    public void TemplateImage_IsStored()
    {
        var image = NativeImage.CreateEmpty();
        Assert.False(image.IsTemplateImage());
        image.SetTemplateImage(true);
        Assert.True(image.IsTemplateImage());
        Assert.True(image.IsMacTemplateImage);
    }

    [Fact]
    public void Unpremultiply_DividesByAlpha_RoundingAndClamping()
    {
        byte[] premultiplied = [64, 32, 16, 128, 5, 5, 5, 0, 1, 2, 3, 255, 200, 0, 0, 100];
        var straight = ImagePixels.Unpremultiply(new ImageBitmap(4, 1, premultiplied));
        Assert.Equal([128, 64, 32, 128, 0, 0, 0, 0, 1, 2, 3, 255, 255, 0, 0, 100], straight.Bgra);
        Assert.Equal((4, 1), (straight.Width, straight.Height));
    }

    [Fact]
    public void ThumbarPlan_FillsSevenSlots_HidingTheRest()
    {
        var icon = NativeImage.CreateEmpty();
        var a = new ThumbarButton { Icon = icon, Flags = ThumbarButtonFlags.DismissOnClick | ThumbarButtonFlags.NoBackground };
        var b = new ThumbarButton { Icon = icon };
        var plan = Thumbar.Plan([a, b])!;
        Assert.Null(plan.InvalidAt);
        Assert.Equal(7, plan.Slots.Length);
        Assert.Equal(new ThumbarSlot(40001, a, 6u, 8u), plan.Slots[0]);
        Assert.Equal(new ThumbarSlot(40002, b, 0u, 8u), plan.Slots[1]);
        for (var i = 2; i < 7; i++) Assert.Equal(new ThumbarSlot((uint)(40001 + i), null, 8u, 8u), plan.Slots[i]);
    }

    [Fact]
    public void ThumbarPlan_SendsIconAndTooltip_OnlyWhenTheButtonHasThem()
    {
        var empty = NativeImage.CreateEmpty();
        var icon = Bitmap(2, 2);
        var plan = Thumbar.Plan([
            new ThumbarButton { Icon = icon, Tooltip = "tip" },
            new ThumbarButton { Icon = icon },
            new ThumbarButton { Icon = empty, Tooltip = "tip" },
            new ThumbarButton { Icon = empty, Tooltip = "" },
        ])!;
        Assert.Equal([0xEu, 0xAu, 0xCu, 0x8u], plan.Slots[..4].Select(s => s.Mask));
    }

    [Theory]
    [InlineData(ThumbarButtonFlags.Enabled, 0u)]
    [InlineData(ThumbarButtonFlags.Disabled, 1u)]
    [InlineData(ThumbarButtonFlags.DismissOnClick, 2u)]
    [InlineData(ThumbarButtonFlags.NoBackground, 4u)]
    [InlineData(ThumbarButtonFlags.Hidden, 8u)]
    [InlineData(ThumbarButtonFlags.NonInteractive, 16u)]
    public void ThumbarPlan_FlagsAreThbfValues(ThumbarButtonFlags flags, uint thbf)
        => Assert.Equal(thbf, Thumbar.Plan([new ThumbarButton { Icon = NativeImage.CreateEmpty(), Flags = flags }])!.Slots[0].Flags);

    [Fact]
    public void ThumbarPlan_UnknownFlag_MarksTheFirstBadButton()
    {
        var icon = NativeImage.CreateEmpty();
        var plan = Thumbar.Plan([
            new ThumbarButton { Icon = icon, Flags = ThumbarButtonFlags.NonInteractive },
            new ThumbarButton { Icon = icon, Flags = (ThumbarButtonFlags)32 },
            new ThumbarButton { Icon = icon, Flags = (ThumbarButtonFlags)64 },
        ])!;
        Assert.Equal(1, plan.InvalidAt);
    }

    [Fact]
    public void ThumbarPlan_MoreThanSeven_IsRefused()
    {
        var icon = NativeImage.CreateEmpty();
        Assert.NotNull(Thumbar.Plan([.. Enumerable.Range(0, 7).Select(_ => new ThumbarButton { Icon = icon })]));
        Assert.Null(Thumbar.Plan([.. Enumerable.Range(0, 8).Select(_ => new ThumbarButton { Icon = icon })]));
    }

    [Fact]
    public void ThumbarFlags_AreWin32ThbfFlags()
    {
        Assert.Equal(0, (int)ThumbarButtonFlags.Enabled);
        Assert.Equal(1, (int)ThumbarButtonFlags.Disabled);
        Assert.Equal(2, (int)ThumbarButtonFlags.DismissOnClick);
        Assert.Equal(4, (int)ThumbarButtonFlags.NoBackground);
        Assert.Equal(8, (int)ThumbarButtonFlags.Hidden);
        Assert.Equal(16, (int)ThumbarButtonFlags.NonInteractive);
    }
}
