using System.Text;
using Microsoft.Extensions.FileProviders;
using Riftway.Asar;
using Riftway.Blazor;

namespace Riftway.Tests;

public sealed class AsarFileProviderTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("riftway-asarfp-").FullName;
    private readonly string _archive;

    public AsarFileProviderTests()
    {
        var source = Path.Combine(_temp, "wwwroot");
        Directory.CreateDirectory(Path.Combine(source, "css"));
        Directory.CreateDirectory(Path.Combine(source, "media"));
        File.WriteAllText(Path.Combine(source, "index.html"), "<!doctype html>app");
        File.WriteAllText(Path.Combine(source, "css", "site.css"), "body{}");
        File.WriteAllText(Path.Combine(source, "media", "clip.txt"), "clip");
        _archive = Path.Combine(_temp, "resources", "app.asar");
        AsarPacker.Pack(source, _archive, new AsarPackOptions { UnpackDirectory = p => p == "media" });
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static string Text(IFileInfo file)
    {
        using var reader = new StreamReader(file.CreateReadStream(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("/index.html")]
    [InlineData("css/../index.html")]
    public void Files_AreServedFromTheArchive(string path)
    {
        using var provider = new AsarFileProvider(_archive);
        var file = provider.GetFileInfo(path);
        Assert.True(file.Exists);
        Assert.False(file.IsDirectory);
        Assert.Equal("index.html", file.Name);
        Assert.Equal(18, file.Length);
        Assert.Null(file.PhysicalPath);
        Assert.Equal(File.GetLastWriteTimeUtc(_archive), file.LastModified.UtcDateTime);
        Assert.Equal("<!doctype html>app", Text(file));
    }

    [Fact]
    public void UnpackedFiles_AreServedFromBesideIt()
    {
        using var provider = new AsarFileProvider(_archive);
        var clip = provider.GetFileInfo("media/clip.txt");
        Assert.Equal(Path.Combine(_archive + ".unpacked", "media", "clip.txt"), clip.PhysicalPath);
        Assert.Equal("clip", Text(clip));
    }

    [Theory]
    [InlineData("missing.html")]
    [InlineData("../app.asar")]
    [InlineData("css")]
    [InlineData("index.html/x")]
    public void WhatIsNotAFile_DoesNotExist(string path)
    {
        using var provider = new AsarFileProvider(_archive);
        Assert.False(provider.GetFileInfo(path).Exists);
    }

    [Fact]
    public void Directories_ListTheirEntries()
    {
        using var provider = new AsarFileProvider(_archive);
        var root = provider.GetDirectoryContents("");
        Assert.True(root.Exists);
        Assert.Equal(["css", "index.html", "media"], root.Select(f => f.Name));
        Assert.Equal([true, false, true], root.Select(f => f.IsDirectory));
        Assert.Equal(["site.css"], provider.GetDirectoryContents("/css/").Select(f => f.Name));
        Assert.False(provider.GetDirectoryContents("index.html").Exists);
        Assert.False(provider.GetDirectoryContents("nope").Exists);
    }

    [Fact]
    public void IntegrityChecks_FailTheRead()
    {
        var bytes = File.ReadAllBytes(_archive);
        var at = Encoding.UTF8.GetString(bytes).LastIndexOf("body{}", StringComparison.Ordinal);
        bytes[at] = (byte)'B';
        File.WriteAllBytes(_archive, bytes);
        using var checking = new AsarFileProvider(_archive, verifyIntegrity: true);
        Assert.Throws<InvalidDataException>(() => Text(checking.GetFileInfo("css/site.css")));
        using var trusting = new AsarFileProvider(_archive);
        Assert.Equal("Body{}", Text(trusting.GetFileInfo("css/site.css")));
    }

    [Fact]
    public void Watch_NeverChanges()
    {
        using var provider = new AsarFileProvider(_archive);
        Assert.False(provider.Watch("**/*").HasChanged);
    }

    [Fact]
    public void BlazorStaticFiles_PreferTheAppArchive()
    {
        var wwwroot = Path.Combine(_temp, "wwwroot");
        // Electron's order: resources/app.asar beside the app before loose files; an explicit ContentRoot decides.
        Use(BlazorWebView.StaticFiles(new BlazorWebViewOptions(), _temp), p => Assert.IsType<AsarFileProvider>(p));
        Use(BlazorWebView.StaticFiles(new BlazorWebViewOptions { ContentRoot = wwwroot }, _temp), p => Assert.IsType<PhysicalFileProvider>(p));
        Use(BlazorWebView.StaticFiles(new BlazorWebViewOptions { ContentRoot = _archive }, Path.Combine(_temp, "x")),
            p => Assert.Equal("<!doctype html>app", Text(p.GetFileInfo("index.html"))));

        File.Delete(_archive); // no handle left open
        Use(BlazorWebView.StaticFiles(new BlazorWebViewOptions(), _temp), p => Assert.IsType<PhysicalFileProvider>(p));
        Use(BlazorWebView.StaticFiles(new BlazorWebViewOptions(), Path.Combine(_temp, "empty")), p => Assert.IsType<NullFileProvider>(p));
    }

    private static void Use(IFileProvider provider, Action<IFileProvider> check)
    {
        try { check(provider); }
        finally { (provider as IDisposable)?.Dispose(); }
    }
}
