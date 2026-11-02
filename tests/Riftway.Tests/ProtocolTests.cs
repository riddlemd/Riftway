namespace Riftway.Tests;

public sealed class ByteRangeTests
{
    [Theory]
    [InlineData("bytes=10-19", 1024, 10, 19)]
    [InlineData("bytes=1000-", 1024, 1000, 1023)]
    [InlineData("bytes=1000-5000", 1024, 1000, 1023)]   // end clamped to the body
    [InlineData("bytes=-24", 1024, 1000, 1023)]         // suffix: the final 24 bytes
    [InlineData("bytes=-5000", 1024, 0, 1023)]          // suffix longer than the body
    [InlineData("bytes=0-0", 1, 0, 0)]
    [InlineData(" BYTES=5-6 ", 10, 5, 6)]
    public void Resolve_Satisfiable(string header, long total, long start, long end)
    {
        var range = ByteRange.Resolve(header, total);

        Assert.Equal(new ByteRange(start, end), range);
        Assert.Equal(end - start + 1, range!.Value.Length);
    }

    [Theory]
    [InlineData(null, 1024)]
    [InlineData("", 1024)]
    [InlineData("items=0-1", 1024)]
    [InlineData("bytes=0-1,5-6", 1024)]   // multi-range: whole body instead
    [InlineData("bytes=1024-", 1024)]     // starts past the end
    [InlineData("bytes=20-10", 1024)]     // end before start
    [InlineData("bytes=-0", 1024)]
    [InlineData("bytes=a-b", 1024)]
    [InlineData("bytes=0-10", 0)]         // empty body
    public void Resolve_SendsTheWholeBody(string? header, long total) => Assert.Null(ByteRange.Resolve(header, total));
}

public sealed class ProtocolMappingTests
{
    [Fact]
    public void ToCefOptions_MapsEachPrivilegeToItsFlag()
    {
        Assert.Equal(1, Protocol.ToCefOptions(new SchemePrivileges { Standard = true }));
        Assert.Equal(8, Protocol.ToCefOptions(new SchemePrivileges { Secure = true }));
        Assert.Equal(16, Protocol.ToCefOptions(new SchemePrivileges { CorsEnabled = true }));
        Assert.Equal(32, Protocol.ToCefOptions(new SchemePrivileges { BypassCsp = true }));
        Assert.Equal(64, Protocol.ToCefOptions(new SchemePrivileges { SupportFetchApi = true }));
        Assert.Equal(0, Protocol.ToCefOptions(new SchemePrivileges { Stream = true }));
    }

    [Fact]
    public void ToCefOptions_CombinesFlags()
        => Assert.Equal(89, Protocol.ToCefOptions(new SchemePrivileges { Standard = true, Secure = true, CorsEnabled = true, SupportFetchApi = true }));

    [Theory]
    [InlineData("text/html; charset=utf-8", "text/html", "utf-8")]
    [InlineData("application/json", "application/json", null)]
    [InlineData("text/plain;CHARSET=\"latin1\"", "text/plain", "latin1")]
    [InlineData("text/plain; format=flowed; charset=utf-8", "text/plain", "utf-8")]
    [InlineData(null, null, null)]
    [InlineData("  ", null, null)]
    public void SplitContentType_SeparatesMimeTypeAndCharset(string? contentType, string? mimeType, string? charset)
        => Assert.Equal((mimeType, charset), ResourceHandler.SplitContentType(contentType));

    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("clip.WEBM", "video/webm")]
    [InlineData("data.unknownext", "application/octet-stream")]
    public void MimeTypeOf_UsesTheExtension(string path, string expected)
        => Assert.Equal(expected, ProtocolResponse.MimeTypeOf(path));
}
