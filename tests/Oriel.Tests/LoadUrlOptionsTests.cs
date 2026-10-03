using Oriel.Cef.Interop;

namespace Oriel.Tests;

public sealed class LoadUrlOptionsTests
{
    [Fact]
    public void ParseExtraHeaders_SplitsLinesAndTrims()
    {
        var headers = PendingLoad.ParseExtraHeaders("X-One: 1\r\nX-Two:  two \n\nContent-Type: a=b:c\n");

        Assert.Equal(
            [new("X-One", "1"), new("X-Two", "two"), new("Content-Type", "a=b:c")],
            headers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no colon")]
    [InlineData(": no name")]
    [InlineData("  : blank name")]
    public void ParseExtraHeaders_SkipsLinesWithoutAName(string? extraHeaders)
        => Assert.Empty(PendingLoad.ParseExtraHeaders(extraHeaders));

    [Fact]
    public void Create_NothingToApply_IsNull()
    {
        Assert.Null(PendingLoad.Create("app://x/", null));
        Assert.Null(PendingLoad.Create("app://x/", new LoadUrlOptions()));
        Assert.Null(PendingLoad.Create("app://x/", new LoadUrlOptions { UserAgent = "UA", PostData = [] }));
    }

    [Fact]
    public void Create_BaseUrlForDataUrl_NotSupported()
        => Assert.Throws<NotSupportedException>(() =>
            PendingLoad.Create("data:text/html,x", new LoadUrlOptions { BaseUrlForDataUrl = "app://x/" }));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 10)]
    public void Create_PartialUploadFile_NotSupported(long offset, long length)
        => Assert.Throws<NotSupportedException>(() => PendingLoad.Create("app://x/", new LoadUrlOptions
        {
            PostData = [new UploadFile("/tmp/f") { Offset = offset, Length = length }],
        }));

    [Fact]
    public void Create_NullPostDataEntry_Throws()
        => Assert.Throws<ArgumentException>(() => PendingLoad.Create("app://x/", new LoadUrlOptions { PostData = [null!] }));

    [Fact]
    public void Create_WholeUploadFile_IsAccepted()
        => Assert.NotNull(PendingLoad.Create("app://x/", new LoadUrlOptions { PostData = [new UploadFile("/tmp/f")] }));

    [Fact]
    public void Create_RefererHeader_BecomesTheReferrer()
    {
        var load = PendingLoad.Create("app://x/", new LoadUrlOptions { ExtraHeaders = "referer: https://a.example/\nX-One: 1" })!;

        Assert.Equal(new Referrer("https://a.example/"), load.Referrer);
        Assert.Equal([new("X-One", "1")], load.Headers);
    }

    [Fact]
    public void Create_HttpReferrer_WinsOverARefererHeader()
    {
        var load = PendingLoad.Create("app://x/", new LoadUrlOptions
        {
            HttpReferrer = "https://option.example/",
            ExtraHeaders = "Referer: https://header.example/",
        })!;

        Assert.Equal("https://option.example/", load.Referrer!.Url);
        Assert.Equal(ReferrerPolicy.Default, load.Referrer.Policy);
        Assert.Empty(load.Headers);
    }

    [Theory]
    [InlineData("app://test/nav?a=1", "app://test/nav?a=1", true)]
    [InlineData("app://test/nav?a=1#frag", "app://test/nav?a=1", true)]   // the request has no fragment
    [InlineData("HTTPS://Example.com", "https://example.com/", true)]     // canonical form
    [InlineData("app://test/nav?a=1", "app://test/nav?a=2", false)]
    [InlineData("app://test/nav", "app://other/nav", false)]
    public void Matches_ComparesCanonicalUrlsWithoutFragment(string loaded, string requested, bool expected)
    {
        var load = PendingLoad.Create(loaded, new LoadUrlOptions { ExtraHeaders = "X: 1" })!;

        Assert.Equal(expected, load.Matches(requested));
    }

    [Theory]
    [InlineData(ReferrerPolicy.Default, cef_referrer_policy_t.REFERRER_POLICY_REDUCE_REFERRER_GRANULARITY_ON_TRANSITION_CROSS_ORIGIN)]
    [InlineData(ReferrerPolicy.StrictOriginWhenCrossOrigin, cef_referrer_policy_t.REFERRER_POLICY_REDUCE_REFERRER_GRANULARITY_ON_TRANSITION_CROSS_ORIGIN)]
    [InlineData(ReferrerPolicy.UnsafeUrl, cef_referrer_policy_t.REFERRER_POLICY_NEVER_CLEAR_REFERRER)]
    [InlineData(ReferrerPolicy.NoReferrerWhenDowngrade, cef_referrer_policy_t.REFERRER_POLICY_CLEAR_REFERRER_ON_TRANSITION_FROM_SECURE_TO_INSECURE)]
    [InlineData(ReferrerPolicy.NoReferrer, cef_referrer_policy_t.REFERRER_POLICY_NO_REFERRER)]
    [InlineData(ReferrerPolicy.Origin, cef_referrer_policy_t.REFERRER_POLICY_ORIGIN)]
    [InlineData(ReferrerPolicy.SameOrigin, cef_referrer_policy_t.REFERRER_POLICY_CLEAR_REFERRER_ON_TRANSITION_CROSS_ORIGIN)]
    [InlineData(ReferrerPolicy.StrictOrigin, cef_referrer_policy_t.REFERRER_POLICY_ORIGIN_CLEAR_ON_TRANSITION_FROM_SECURE_TO_INSECURE)]
    public void ToCef_MapsEachPolicy(ReferrerPolicy policy, cef_referrer_policy_t expected)
        => Assert.Equal(expected, PendingLoad.ToCef(policy));
}
