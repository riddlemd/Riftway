using System.Text;
using Riftway.Asar;

namespace Riftway.Tests;

// file:// URLs into ASAR archives, as Electron's asar URL loader resolves them.
public sealed class AsarFileUrlTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("riftway-asarurl-").FullName;
    private readonly string _archive;

    public AsarFileUrlTests()
    {
        var source = Path.Combine(_temp, "src");
        Directory.CreateDirectory(Path.Combine(source, "css"));
        Directory.CreateDirectory(Path.Combine(source, "my files"));
        File.WriteAllText(Path.Combine(source, "index.html"), "<!doctype html>packed");
        File.WriteAllText(Path.Combine(source, "css", "site.css"), "body{}");
        File.WriteAllText(Path.Combine(source, "my files", "a b.txt"), "spaced");
        _archive = Path.Combine(_temp, "res dir", "app.asar");
        AsarPacker.Pack(source, _archive);
        Directory.CreateDirectory(Path.Combine(_temp, "loose"));
    }

    public void Dispose()
    {
        AsarFileUrls.Forget(_archive);
        Directory.Delete(_temp, recursive: true);
    }

    private string Url(string inner) => new Uri(_archive).AbsoluteUri + "/" + inner;

    [Theory]
    [InlineData("index.html", "index.html")]
    [InlineData("css/site.css", "css/site.css")]
    [InlineData("my%20files/a%20b.txt", "my files/a b.txt")]
    [InlineData("css/site.css?v=2#top", "css/site.css")]
    [InlineData("css/./../index.html", "index.html")]
    public void UrlsInsideAnArchive_Resolve(string inner, string entry)
    {
        Assert.True(AsarFileUrls.TryResolve(Url(inner), out var archive, out var found));
        Assert.Equal(Path.GetFullPath(_archive), archive.FilePath);
        Assert.Equal(entry, found.Path);
    }

    [Theory]
    [InlineData("missing.html")] // declined, so Chromium's own loader fails it with ERR_FILE_NOT_FOUND
    [InlineData("css")]
    [InlineData("")]
    [InlineData("../loose/x.html")]
    public void WhatIsNotAFileInTheArchive_IsDeclined(string inner)
        => Assert.False(AsarFileUrls.TryResolve(Url(inner), out _, out _));

    [Fact]
    public void OtherUrls_AreDeclined()
    {
        var loose = Path.Combine(_temp, "loose", "page.html");
        File.WriteAllText(loose, "loose");
        Assert.False(AsarFileUrls.TryResolve(new Uri(loose).AbsoluteUri, out _, out _));
        Assert.False(AsarFileUrls.TryResolve("https://example.com/app.asar/index.html", out _, out _));
        Assert.False(AsarFileUrls.TryResolve(new Uri(Path.Combine(_temp, "nope.asar", "index.html")).AbsoluteUri, out _, out _));
        // A folder named like an archive is a folder: Electron looks for an archive file.
        var folder = Directory.CreateDirectory(Path.Combine(_temp, "dir.asar")).FullName;
        File.WriteAllText(Path.Combine(folder, "index.html"), "folder");
        Assert.False(AsarFileUrls.TryResolve(new Uri(Path.Combine(folder, "index.html")).AbsoluteUri, out _, out _));
        Assert.False(AsarFileUrls.TryResolve("not a url", out _, out _));
    }

    [Fact]
    public void ACorruptArchive_IsDeclined()
    {
        var bad = Path.Combine(_temp, "bad.asar");
        File.WriteAllBytes(bad, [1, 2, 3]);
        Assert.False(AsarFileUrls.TryResolve(new Uri(bad).AbsoluteUri + "/index.html", out _, out _));
    }

    [Fact]
    public async Task Serve_AnswersWithTheEntryAndItsType()
    {
        var response = await AsarFileUrls.Serve(new ProtocolRequest(Url("css/site.css"), "GET", null, new Dictionary<string, string>(), null, []), default);
        Assert.Equal(200, response.StatusCode);
        Assert.StartsWith("text/css", response.ContentType);
        using var reader = new StreamReader(response.Body!, Encoding.UTF8);
        Assert.Equal("body{}", await reader.ReadToEndAsync());

        var gone = await AsarFileUrls.Serve(new ProtocolRequest(Url("missing.html"), "GET", null, new Dictionary<string, string>(), null, []), default);
        Assert.Equal(404, gone.StatusCode);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("1", null, true)]
    [InlineData(null, "1", true)]
    [InlineData("", null, false)]
    public void NoAsar_FollowsTheEnvironment(string? electron, string? riftway, bool disabled)
        => Assert.Equal(disabled, AsarFileUrls.IsDisabledBy(name => name switch
        {
            "ELECTRON_NO_ASAR" => electron,
            "RIFTWAY_NO_ASAR" => riftway,
            _ => null,
        }));
}
