namespace Oriel.Tests;

public sealed class WebContentsMethodsTests
{
    [Fact]
    public void ImageMarkup_EscapesAsBlinksURLToImageMarkup()
    {
        Assert.Equal("<img src=\"http://x/a?b=1&amp;c=&quot;&lt;&gt;&#39;\" alt=\"x &amp; &quot;y&quot;\"/>", WebContents.ImageMarkup("http://x/a?b=1&c=\"<>'", "x & \"y\""));
    }

    [Fact]
    public void ImageMarkup_LeavesOutAnEmptyAlt()
    {
        Assert.Equal("<img src=\"http://x/a.png\"/>", WebContents.ImageMarkup("http://x/a.png", ""));
    }
    [Fact]
    public void DeviceMetricsOverride_MapsElectronsParameters()
    {
        var overrides = WebContents.DeviceMetricsOverride(new DeviceEmulationParameters
        {
            ScreenPosition = EmulatedScreenPosition.Mobile,
            ScreenSize = (400, 800),
            ViewPosition = new Point(10, 20),
            DeviceScaleFactor = 2,
            ViewSize = (300, 200),
            Scale = 0.5,
        });
        Assert.Equal("""{"width":300,"height":200,"deviceScaleFactor":2,"mobile":true,"scale":0.5,"screenWidth":400,"screenHeight":800,"positionX":10,"positionY":20}""",
            overrides.ToJsonString());
    }

    [Fact]
    public void DeviceMetricsOverride_Defaults_OverrideNothing()
    {
        var overrides = WebContents.DeviceMetricsOverride(new DeviceEmulationParameters());
        Assert.Equal("""{"width":0,"height":0,"deviceScaleFactor":0,"mobile":false,"scale":1,"screenWidth":0,"screenHeight":0}""",
            overrides.ToJsonString());
    }

    [Fact]
    public void WorldOf_ZeroIsTheMainWorld_999ThePreloadWorld_OthersTheirOwn()
    {
        Assert.Equal((true, (string?)null), WebContents.WorldOf(0));
        Assert.Equal((false, (string?)"oriel"), WebContents.WorldOf(999));
        Assert.Equal((false, (string?)"oriel-world-5"), WebContents.WorldOf(5));
    }

    [Fact]
    public void WithSourceUrl_NamesTheScriptOnlyWhenGiven()
    {
        Assert.Equal("1", WebContents.WithSourceUrl(new WebSource("1")));
        Assert.Equal("1", WebContents.WithSourceUrl(new WebSource("1", "")));
        Assert.Equal("1\n//# sourceURL=a.js", WebContents.WithSourceUrl(new WebSource("1", "a.js")));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void CapturePixels_EmptyRect_IsTheWholeImage(int width, int height)
        => Assert.Null(WebContents.CapturePixels(new Rectangle(1, 1, width, height), 2, 100, 100));

    [Fact]
    public void CapturePixels_NoRect_IsTheWholeImage() => Assert.Null(WebContents.CapturePixels(null, 2, 100, 100));

    [Fact]
    public void CapturePixels_ScalesOutwardAndClipsToTheImage()
    {
        Assert.Equal(new Rectangle(16, 16, 17, 17), WebContents.CapturePixels(new Rectangle(11, 11, 11, 11), 1.5, 100, 100));
        Assert.Equal(new Rectangle(80, 0, 20, 10), WebContents.CapturePixels(new Rectangle(40, -5, 20, 10), 2, 100, 100));
    }
}
