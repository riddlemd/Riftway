using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway.Tests;

public sealed class UrlPatternTests
{
    [Theory]
    [InlineData("<all_urls>", "app://bundle/x", true)]
    [InlineData("*://*/*", "https://a.example/x", true)]
    [InlineData("*://*/*", "http://a.example/", true)]
    [InlineData("*://*/*", "ftp://a.example/x", false)]          // * is http or https only
    [InlineData("*://*/*", "app://bundle/x", false)]
    [InlineData("app://bundle/*", "app://bundle/index.html", true)]
    [InlineData("https://example.com/*", "https://example.com/a", true)]
    [InlineData("https://example.com/*", "http://example.com/a", false)]
    [InlineData("https://example.com/*", "https://www.example.com/a", false)]
    [InlineData("https://*.example.com/*", "https://www.example.com/a", true)]
    [InlineData("https://*.example.com/*", "https://example.com/a", true)]  // *. includes the domain itself
    [InlineData("https://*.example.com/*", "https://badexample.com/a", false)]
    [InlineData("https://EXAMPLE.com/*", "https://example.COM/a", true)]
    [InlineData("https://example.com/a/*", "https://example.com/b/c", false)]
    [InlineData("https://example.com/*.js", "https://example.com/lib/x.js", true)]
    [InlineData("https://example.com/*.js", "https://example.com/lib/x.css", false)]
    [InlineData("https://example.com/a", "https://example.com/a?q=1", false)]  // the path includes the query
    [InlineData("https://example.com/a?q=*", "https://example.com/a?q=1", true)]
    [InlineData("https://example.com/*", "https://example.com:8443/a", true)]   // no port: any port
    [InlineData("https://example.com:8443/*", "https://example.com:8443/a", true)]
    [InlineData("https://example.com:8443/*", "https://example.com/a", false)]
    [InlineData("https://example.com:443/*", "https://example.com/a", true)]
    [InlineData("https://example.com:*/*", "https://example.com:9/a", true)]
    [InlineData("file:///tmp/*", "file:///tmp/a.txt", true)]
    [InlineData("file:///tmp/*", "file:///etc/a.txt", false)]
    public void Matches(string pattern, string url, bool expected) => Assert.Equal(expected, UrlPattern.Parse(pattern).Matches(url));

    [Theory]
    [InlineData("example.com")]
    [InlineData("https://example.com")]   // no path
    [InlineData("https:///a")]            // no host
    [InlineData("https://www.*.com/*")]   // * only as the leading label
    [InlineData("https://example.com:x/*")]
    [InlineData("1https://example.com/*")]
    [InlineData("://example.com/*")]
    public void Parse_RejectsWhatChromiumRejects(string pattern)
        => Assert.Equal($"Invalid url pattern {pattern}", Assert.Throws<ArgumentException>(() => UrlPattern.Parse(pattern)).Message.Split(" (")[0]);
}

public sealed class WebRequestFilterTests
{
    [Fact]
    public void Empty_MatchesEverything() => Assert.True(new WebRequestFilter().Compile()("app://x/", "other"));

    [Fact]
    public void Urls_AnyPatternMatches()
    {
        var matches = new WebRequestFilter { Urls = ["https://a.test/*", "https://b.test/*"] }.Compile();
        Assert.True(matches("https://b.test/x", "xhr"));
        Assert.False(matches("https://c.test/x", "xhr"));
    }

    [Fact]
    public void ExcludeUrls_WinOverUrls()
    {
        var matches = new WebRequestFilter { Urls = ["<all_urls>"], ExcludeUrls = ["https://a.test/private/*"] }.Compile();
        Assert.False(matches("https://a.test/private/x", "xhr"));
        Assert.True(matches("https://a.test/public/x", "xhr"));
    }

    [Fact]
    public void Types_LimitTheResourceTypes()
    {
        var matches = new WebRequestFilter { Types = ["script", "xhr"] }.Compile();
        Assert.True(matches("https://a.test/", "xhr"));
        Assert.False(matches("https://a.test/", "image"));
    }

    [Fact]
    public void Compile_ThrowsForAnInvalidPattern()
        => Assert.Throws<ArgumentException>(() => new WebRequestFilter { ExcludeUrls = ["nope"] }.Compile());

    [Theory]
    [InlineData("other")]     // a request's type, but not a filter's in Electron
    [InlineData("subframe")]
    public void Compile_ThrowsForATypeElectronRejects(string type)
        => Assert.StartsWith($"Invalid type {type}", Assert.Throws<ArgumentException>(() => new WebRequestFilter { Types = ["xhr", type] }.Compile()).Message);

    [Fact]
    public void Compile_AcceptsEveryElectronType()
        => Assert.True(new WebRequestFilter { Types = ["cspReport", "font", "image", "mainFrame", "media", "object", "ping", "script", "stylesheet", "subFrame", "webSocket", "xhr"] }
            .Compile()("https://a.test/", "webSocket"));
}

public sealed class WebRequestTests
{
    [Theory]
    [InlineData(cef_resource_type_t.RT_MAIN_FRAME, "mainFrame")]
    [InlineData(cef_resource_type_t.RT_SUB_FRAME, "subFrame")]
    [InlineData(cef_resource_type_t.RT_STYLESHEET, "stylesheet")]
    [InlineData(cef_resource_type_t.RT_SCRIPT, "script")]
    [InlineData(cef_resource_type_t.RT_WORKER, "script")]
    [InlineData(cef_resource_type_t.RT_SERVICE_WORKER, "script")]
    [InlineData(cef_resource_type_t.RT_IMAGE, "image")]
    [InlineData(cef_resource_type_t.RT_FAVICON, "image")]
    [InlineData(cef_resource_type_t.RT_FONT_RESOURCE, "font")]
    [InlineData(cef_resource_type_t.RT_OBJECT, "object")]
    [InlineData(cef_resource_type_t.RT_PLUGIN_RESOURCE, "object")]
    [InlineData(cef_resource_type_t.RT_MEDIA, "media")]
    [InlineData(cef_resource_type_t.RT_XHR, "xhr")]
    [InlineData(cef_resource_type_t.RT_PING, "ping")]
    [InlineData(cef_resource_type_t.RT_CSP_REPORT, "cspReport")]
    [InlineData(cef_resource_type_t.RT_SUB_RESOURCE, "other")]
    [InlineData(cef_resource_type_t.RT_PREFETCH, "other")]
    public void ResourceTypeName_IsElectrons(cef_resource_type_t type, string name) => Assert.Equal(name, WebRequest.ResourceTypeName(type));

    [Theory]
    [InlineData(cef_errorcode_t.ERR_NAME_NOT_RESOLVED, "net::ERR_NAME_NOT_RESOLVED")]
    [InlineData(cef_errorcode_t.ERR_ABORTED, "net::ERR_ABORTED")]
    [InlineData((cef_errorcode_t)(-99999), "net::ERR_-99999")]
    public void ErrorName_IsChromiums(cef_errorcode_t error, string name) => Assert.Equal(name, WebRequest.ErrorName(error));

    [Fact]
    public void ResponseHeaders_GroupRepeatedNamesInOrder()
    {
        var headers = WebRequest.ResponseHeadersOf([new("Set-Cookie", "a=1"), new("Content-Type", "text/html"), new("set-cookie", "b=2")]);
        Assert.Equal(["a=1", "b=2"], headers["Set-Cookie"]);
        Assert.Equal(["text/html"], headers["Content-Type"]);
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void RequestHeaders_LastValueWins()
    {
        var request = new RequestInfo(1, "https://a.test/", "GET", "", cef_resource_type_t.RT_XHR, [new("X-One", "1"), new("x-one", "2"), new("Accept", "*/*")]);
        var headers = WebRequest.RequestHeadersOf(request);
        Assert.Equal("2", headers["X-One"]);
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void UploadData_NullWithoutABody() => Assert.Null(WebRequest.UploadDataOf(null));

    [Fact]
    public void UploadData_IsElectronsShapeInOrder()
    {
        var upload = WebRequest.UploadDataOf([new UploadElement([1, 2], null), new UploadElement(null, "/tmp/a.txt"), new UploadElement(null, null)]);
        Assert.NotNull(upload);
        Assert.Equal(3, upload.Count);
        Assert.Equal("rawData", upload[0].Type);
        Assert.Equal([1, 2], upload[0].Bytes!);
        Assert.Null(upload[0].File);
        Assert.Equal("file", upload[1].Type);
        Assert.Equal("/tmp/a.txt", upload[1].File);
        Assert.Null(upload[1].Bytes);
        Assert.Equal("rawData", upload[2].Type);
        Assert.Empty(upload[2].Bytes!);
    }

    [Theory]
    [InlineData("riftway-ipc://sync/", true)]
    [InlineData("devtools://devtools/bundled/inspector.html", true)]
    [InlineData("chrome://version/", true)]
    [InlineData("CHROME://version/", true)]
    [InlineData("chrome-untrusted://x/", true)]
    [InlineData("https://riftway.test/", false)]
    [InlineData("chrome-extension://abc/x.js", false)]
    [InlineData("x-webrequest://app/data", false)]
    [InlineData("about:blank", false)]
    [InlineData("nocolon", false)]
    public void IsHidden_OnlyInternalSchemes(string url, bool hidden) => Assert.Equal(hidden, WebRequest.IsHidden(url));
}

public sealed class InitiatorOriginTests
{
    [Theory]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    [InlineData("https://example.com:443/", "https://example.com")]
    [InlineData("null", "null")]
    [InlineData("file://", "file://")]
    [InlineData(null, null)]
    public void OriginOf_SerializesLikeChromiumsOrigin(string? initiator, string? origin) => Assert.Equal(origin, WebRequest.OriginOf(initiator));
}
