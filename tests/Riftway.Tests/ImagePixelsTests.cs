namespace Riftway.Tests;

public sealed class ImagePixelsTests
{
    // One byte per pixel replicated across B, G, R and A keeps hand-computed expectations readable.
    private static ImageBitmap Gray(int width, int height, params byte[] values)
        => new(width, height, values.SelectMany(v => new[] { v, v, v, v }).ToArray());

    private static byte[] Levels(ImageBitmap bitmap)
        => Enumerable.Range(0, bitmap.Width * bitmap.Height).Select(i => bitmap.Bgra[i * 4]).ToArray();

    [Fact]
    public void Crop_InsideTheImage_ReturnsThoseRows()
    {
        var src = Gray(3, 3, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var crop = ImagePixels.Crop(src, 1, 1, 2, 2);
        Assert.Equal((2, 2), (crop.Width, crop.Height));
        Assert.Equal(new byte[] { 5, 6, 8, 9 }, Levels(crop));
    }

    [Fact]
    public void Crop_PastTheRightAndBottomEdge_ClipsTheRectangle()
    {
        var crop = ImagePixels.Crop(Gray(3, 3, 1, 2, 3, 4, 5, 6, 7, 8, 9), 2, 2, 5, 5);
        Assert.Equal((1, 1), (crop.Width, crop.Height));
        Assert.Equal(new byte[] { 9 }, Levels(crop));
    }

    [Fact]
    public void Crop_NegativeOrigin_ClipsTheRectangle()
    {
        var crop = ImagePixels.Crop(Gray(3, 3, 1, 2, 3, 4, 5, 6, 7, 8, 9), -1, -2, 3, 4);
        Assert.Equal((2, 2), (crop.Width, crop.Height));
        Assert.Equal(new byte[] { 1, 2, 4, 5 }, Levels(crop));
    }

    [Fact]
    public void Crop_KeepsAllFourChannels()
    {
        var src = new ImageBitmap(2, 1, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, ImagePixels.Crop(src, 1, 0, 1, 1).Bgra);
    }

    [Theory]
    [InlineData(3, 0, 1, 1)]
    [InlineData(0, 3, 1, 1)]
    [InlineData(-3, 0, 3, 1)]
    [InlineData(0, -2, 1, 2)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, -1, 2)]
    [InlineData(1, 1, 2, -1)]
    public void Crop_EmptyIntersection_ReturnsEmptyBitmap(int x, int y, int width, int height)
    {
        var crop = ImagePixels.Crop(Gray(3, 2, 1, 2, 3, 4, 5, 6), x, y, width, height);
        Assert.Equal((0, 0, 0), (crop.Width, crop.Height, crop.Bgra.Length));
    }

    // Expectations come from an independent Python port of skia's ResizeFilter (float32 weights, 14-bit fixed point), not from this code.
    private static readonly byte[] Step = [64, 64, 64, 64, 192, 192, 192, 192];

    [Fact]
    public void Resize_BestEnlargeStepEdge_RingsAroundTheEdge()
    {
        var r = ImagePixels.Resize(Gray(8, 1, Step), 16, 1, ResizeQuality.Best);
        Assert.Equal(new byte[] { 64, 64, 64, 64, 67, 56, 50, 90, 165, 205, 199, 188, 191, 192, 192, 192 }, Levels(r));
    }

    [Fact]
    public void Resize_GoodEnlargeStepEdge_StaysWithinTheEdgeLevels()
    {
        var r = ImagePixels.Resize(Gray(8, 1, Step), 16, 1, ResizeQuality.Good);
        Assert.Equal(new byte[] { 64, 64, 64, 64, 64, 64, 64, 73, 182, 192, 192, 192, 192, 192, 192, 192 }, Levels(r));
    }

    [Fact]
    public void Resize_BetterUsesTheSameFilterAsGood()
    {
        var good = ImagePixels.Resize(Gray(8, 1, Step), 16, 1, ResizeQuality.Good);
        Assert.Equal(good.Bgra, ImagePixels.Resize(Gray(8, 1, Step), 16, 1, ResizeQuality.Better).Bgra);
    }

    [Fact]
    public void Resize_DefaultQualityIsBest()
        => Assert.Equal(ImagePixels.Resize(Gray(8, 1, Step), 16, 1, ResizeQuality.Best).Bgra, ImagePixels.Resize(Gray(8, 1, Step), 16, 1).Bgra);

    [Fact]
    public void Resize_ShrinkStepEdge_MatchesTheFilter()
    {
        Assert.Equal(new byte[] { 61, 70, 185, 194 }, Levels(ImagePixels.Resize(Gray(8, 1, Step), 4, 1, ResizeQuality.Best)));
        Assert.Equal(new byte[] { 64, 68, 187, 192 }, Levels(ImagePixels.Resize(Gray(8, 1, Step), 4, 1, ResizeQuality.Good)));
    }

    [Fact]
    public void Resize_FractionalScale_MatchesTheFilter()
    {
        Assert.Equal(new byte[] { 66, 53, 128, 202, 189 }, Levels(ImagePixels.Resize(Gray(8, 1, Step), 5, 1, ResizeQuality.Best)));
        Assert.Equal(new byte[] { 64, 64, 128, 192, 192 }, Levels(ImagePixels.Resize(Gray(8, 1, Step), 5, 1, ResizeQuality.Good)));
    }

    [Fact]
    public void Resize_Vertically_UsesTheSameFilter()
    {
        var r = ImagePixels.Resize(Gray(1, 8, Step), 1, 16, ResizeQuality.Best);
        Assert.Equal(new byte[] { 64, 64, 64, 64, 67, 56, 50, 90, 165, 205, 199, 188, 191, 192, 192, 192 }, Levels(r));
    }

    [Fact]
    public void Resize_TwoDimensions_ConvolvesHorizontallyThenVertically()
    {
        var src = Gray(4, 4, 0, 50, 100, 150, 200, 250, 10, 60, 110, 160, 210, 30, 80, 130, 180, 230);
        Assert.Equal(new byte[] { 74, 80, 110, 191, 170, 33, 77, 171, 194 }, Levels(ImagePixels.Resize(src, 3, 3, ResizeQuality.Best)));
        Assert.Equal(new byte[] { 37, 83, 128, 162, 157, 54, 91, 159, 196 }, Levels(ImagePixels.Resize(src, 3, 3, ResizeQuality.Good)));
    }

    [Fact]
    public void Resize_ThreeToOne_WeightsTheSourcePixelUnderTheDestinationCentre()
    {
        Assert.Equal(new byte[] { 97 }, Levels(ImagePixels.Resize(Gray(3, 1, 0, 255, 0), 1, 1, ResizeQuality.Best)));
        Assert.Equal(new byte[] { 112 }, Levels(ImagePixels.Resize(Gray(3, 1, 0, 255, 0), 1, 1, ResizeQuality.Good)));
    }

    [Fact]
    public void Resize_FlatImage_KeepsItsLevelAtEveryQuality()
    {
        foreach (var quality in new[] { ResizeQuality.Good, ResizeQuality.Best })
        {
            Assert.All(Levels(ImagePixels.Resize(Gray(5, 1, 100, 100, 100, 100, 100), 3, 1, quality)), v => Assert.Equal(100, v));
            Assert.All(Levels(ImagePixels.Resize(Gray(3, 1, 100, 100, 100), 7, 1, quality)), v => Assert.Equal(100, v));
        }
    }

    [Fact]
    public void Resize_RingingOnAlpha_CutsPremultipliedColourBackToAlpha()
    {
        ImageBitmap Px(params (byte c, byte a)[] p) => new(p.Length, 1, p.SelectMany(x => new[] { x.c, x.c, x.c, x.a }).ToArray());
        var r = ImagePixels.Resize(Px((0, 0), (0, 60), (60, 60), (60, 60), (0, 0), (0, 60)), 4, 1, ResizeQuality.Best);
        // Unclamped the third pixel would be (44, 44, 44, 34).
        Assert.Equal(new byte[] { 0, 0, 0, 19, 44, 44, 44, 69, 34, 34, 34, 34, 0, 0, 0, 34 }, r.Bgra);
    }

    [Fact]
    public void Resize_ColourChannels_AreFilteredIndependently()
    {
        var src = new ImageBitmap(2, 1, [10, 20, 30, 255, 10, 20, 30, 255]);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255 }, ImagePixels.Resize(src, 3, 1).Bgra);
    }

    [Fact]
    public void Resize_SameSize_ReturnsACopy()
    {
        var src = Gray(2, 1, 7, 9);
        var r = ImagePixels.Resize(src, 2, 1);
        Assert.Equal(src.Bgra, r.Bgra);
        Assert.NotSame(src.Bgra, r.Bgra);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void Resize_NonPositiveTarget_ReturnsEmptyBitmap(int width, int height)
    {
        var r = ImagePixels.Resize(Gray(2, 1, 1, 2), width, height);
        Assert.Equal((0, 0, 0), (r.Width, r.Height, r.Bgra.Length));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    public void Resize_EmptySource_ReturnsEmptyBitmap(int srcWidth, int srcHeight)
    {
        var r = ImagePixels.Resize(new ImageBitmap(srcWidth, srcHeight, []), 2, 2);
        Assert.Equal((0, 0, 0), (r.Width, r.Height, r.Bgra.Length));
    }

    [Theory]
    [InlineData(100, 50, 20, 10, 20, 10)]
    [InlineData(100, 50, 20, null, 20, 10)]
    [InlineData(100, 50, null, 10, 20, 10)]
    [InlineData(100, 50, null, null, 100, 50)]
    [InlineData(3, 2, 1, null, 1, 1)]
    [InlineData(3, 2, 2, null, 2, 1)]
    [InlineData(2, 3, null, 5, 3, 5)]
    [InlineData(100, 1, 10, null, 10, 1)]
    [InlineData(200, 100, 1, null, 1, 1)]
    [InlineData(100, 50, 7, 9, 7, 9)]
    [InlineData(4, 5, 2, null, 2, 3)]
    [InlineData(0, 0, 5, null, 5, 0)]
    [InlineData(0, 4, 5, null, 5, 0)]
    [InlineData(4, 0, null, 5, 0, 5)]
    public void ResizeTarget_FollowsElectronsRule(int sw, int sh, int? w, int? h, int ew, int eh)
        => Assert.Equal((ew, eh), ImagePixels.ResizeTarget(sw, sh, w, h));

    [Fact]
    public void TryParseDataUrl_Base64_DecodesPayloadAndMime()
    {
        Assert.True(ImagePixels.TryParseDataUrl("data:image/png;base64,AQID", out var mime, out var data));
        Assert.Equal("image/png", mime);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
    }

    [Fact]
    public void TryParseDataUrl_IgnoresWhitespaceInBase64AndCaseOfSchemeMimeAndFlag()
    {
        Assert.True(ImagePixels.TryParseDataUrl("DATA:Image/PNG;charset=utf-8;BASE64, AQ\r\nID ", out var mime, out var data));
        Assert.Equal("image/png", mime);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
    }

    [Fact]
    public void TryParseDataUrl_NoMime_DefaultsToTextPlain()
    {
        Assert.True(ImagePixels.TryParseDataUrl("data:,hi", out var mime, out _));
        Assert.Equal("text/plain", mime);
        Assert.True(ImagePixels.TryParseDataUrl("data:;base64,AQ==", out mime, out var data));
        Assert.Equal("text/plain", mime);
        Assert.Equal(new byte[] { 1 }, data);
        Assert.True(ImagePixels.TryParseDataUrl("data:;charset=utf-8,x", out mime, out _));
        Assert.Equal("text/plain", mime);
    }

    [Fact]
    public void TryParseDataUrl_PlainPayload_IsPercentDecodedAsBytes()
    {
        Assert.True(ImagePixels.TryParseDataUrl("data:image/svg+xml,%3Csvg%2F%3E%20a%zz%4z%z4%4%41", out var mime, out var data));
        Assert.Equal("image/svg+xml", mime);
        Assert.Equal("<svg/> a%zz%4z%z4%4A"u8.ToArray(), data);
    }

    [Fact]
    public void TryParseDataUrl_PlainPayload_KeepsNonAsciiAsUtf8()
        => Assert.True(ImagePixels.TryParseDataUrl("data:text/plain,é", out _, out var data) && data.SequenceEqual(new byte[] { 0xC3, 0xA9 }));

    [Fact]
    public void TryParseDataUrl_PayloadAfterFirstComma_KeepsLaterCommas()
    {
        Assert.True(ImagePixels.TryParseDataUrl("data:text/plain,a,b", out _, out var data));
        Assert.Equal("a,b"u8.ToArray(), data);
    }

    [Theory]
    [InlineData("image/png;base64,AQID")]
    [InlineData("http://example.com/a.png")]
    [InlineData("")]
    [InlineData("data:image/png;base64")]
    [InlineData("data:image/png;base64,!!!!")]
    [InlineData("data:image/png;base64,AQI")]
    public void TryParseDataUrl_Invalid_ReturnsFalse(string url)
    {
        Assert.False(ImagePixels.TryParseDataUrl(url, out _, out var data));
        Assert.Empty(data);
    }

    [Fact]
    public void ToDataUrl_EncodesBase64WithMime()
        => Assert.Equal("data:image/png;base64,AQID", ImagePixels.ToDataUrl("image/png", new byte[] { 1, 2, 3 }));

    [Fact]
    public void ToDataUrl_RoundTripsThroughTryParseDataUrl()
    {
        Assert.True(ImagePixels.TryParseDataUrl(ImagePixels.ToDataUrl("image/jpeg", new byte[] { 255, 0, 128 }), out var mime, out var data));
        Assert.Equal("image/jpeg", mime);
        Assert.Equal(new byte[] { 255, 0, 128 }, data);
    }

    [Fact]
    public void ScaleVariants_PlainName_ListsScaleOneThenEverySuffix()
    {
        var expected = new (string, float)[]
        {
            ("icons/app.png", 1f), ("icons/app@1.25x.png", 1.25f), ("icons/app@1.33x.png", 1.33f),
            ("icons/app@1.4x.png", 1.4f), ("icons/app@1.5x.png", 1.5f), ("icons/app@1.8x.png", 1.8f),
            ("icons/app@2x.png", 2f), ("icons/app@2.5x.png", 2.5f), ("icons/app@3x.png", 3f),
            ("icons/app@4x.png", 4f), ("icons/app@5x.png", 5f),
        };
        Assert.Equal(expected, ImagePixels.ScaleVariants("icons/app.png"));
    }

    [Theory]
    [InlineData("icons/app@2x.png", 2f)]
    [InlineData("app@1x.png", 1f)]
    [InlineData("a/app@1.25x.jpg", 1.25f)]
    [InlineData("app@5x.png", 5f)]
    public void ScaleVariants_NameWithScaleSuffix_ReturnsOnlyThatPath(string path, float scale)
        => Assert.Equal([(path, scale)], ImagePixels.ScaleVariants(path));

    [Fact]
    public void ScaleVariants_UnknownScaleSuffix_IsTreatedAsPlainName()
    {
        var variants = ImagePixels.ScaleVariants("app@7x.png");
        Assert.Equal(11, variants.Count);
        Assert.Equal(("app@7x.png", 1f), variants[0]);
        Assert.Equal("app@7x@2x.png", variants[6].Path);
    }

    [Fact]
    public void ScaleVariants_DirectoryDotDoesNotCountAsExtension()
    {
        var variants = ImagePixels.ScaleVariants("my.dir/app");
        Assert.Equal("my.dir/app@2x", variants[6].Path);
    }
}
