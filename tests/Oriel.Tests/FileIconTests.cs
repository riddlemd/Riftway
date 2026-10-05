using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class FileIconQueryTests
{
    [Theory]
    [InlineData(@"C:\a\notes.TXT", ".TXT", true)]
    [InlineData(@"C:\a\b.tar.gz", ".gz", true)]
    [InlineData(@"C:\a\app.exe", @"C:\a\app.exe", false)]
    [InlineData(@"C:\a\lib.DLL", @"C:\a\lib.DLL", false)]
    [InlineData(@"C:\a\icon.ico", @"C:\a\icon.ico", false)]
    [InlineData(@"C:\a\README", @"C:\a\README", false)]
    public void IconQuery_GroupsByExtensionExceptExecutablesAndIcons(string path, string query, bool byType)
        => Assert.Equal((query, byType), OrielApp.IconQuery(path));
}

public sealed class FileIconPixelTests
{
    [Fact]
    public void HasAlpha_LooksAtEveryAlphaByte()
    {
        Assert.False(IconPixels.HasAlpha([9, 9, 9, 0, 9, 9, 9, 0]));
        Assert.True(IconPixels.HasAlpha([9, 9, 9, 0, 9, 9, 9, 1]));
    }

    [Fact]
    public void Premultiply_ScalesColourByAlphaAndKeepsOpaque()
    {
        byte[] p = [200, 100, 50, 128, 200, 100, 50, 255, 200, 100, 50, 0];
        IconPixels.Premultiply(p);
        Assert.Equal([100, 50, 25, 128, 200, 100, 50, 255, 0, 0, 0, 0], p);
    }
}

public sealed class GetFileIconTests : IDisposable
{
    private readonly IFileIconBackend _saved = OrielApp.FileIconBackend;
    private readonly FakeBackend _backend = new();

    public GetFileIconTests() => OrielApp.FileIconBackend = _backend;

    public void Dispose() => OrielApp.FileIconBackend = _saved;

    [Theory]
    [InlineData(null, false)]
    [InlineData(FileIconSize.Small, true)]
    [InlineData(FileIconSize.Normal, false)]
    [InlineData(FileIconSize.Large, false)]
    public async Task Size_SmallOnlyForSmall(FileIconSize? size, bool small)
    {
        await OrielApp.LoadFileIcon("a.txt", size is { } s ? new FileIconOptions { Size = s } : null);
        Assert.Equal((".txt", small, true), (_backend.Query, _backend.Small, _backend.ByType));
    }

    [Fact]
    public async Task Pixels_BecomeTheImage()
    {
        var image = await OrielApp.LoadFileIcon("a.txt", null);
        Assert.Equal((2, 1), image.GetSize());
        Assert.Equal(FakeBackend.Pixels, image.ToBitmap());
    }

    [Fact]
    public async Task NoIcon_FaultsTheTask()
    {
        _backend.None = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => OrielApp.LoadFileIcon("a.txt", null));
    }

    private sealed class FakeBackend : IFileIconBackend
    {
        public static readonly byte[] Pixels = [1, 2, 3, 255, 4, 5, 6, 255];
        public string? Query;
        public bool Small, ByType, None;

        public (byte[] Bgra, int Width, int Height)? Load(string query, bool small, bool byType)
        {
            (Query, Small, ByType) = (query, small, byType);
            return None ? null : ((byte[])Pixels.Clone(), 2, 1);
        }
    }
}

public sealed class WindowsFileIconTests
{
    [WindowsOnlyFact]
    public void TextType_HasA32And16PixelIcon()
    {
        if (!OperatingSystem.IsWindows()) return;
        var normal = WindowsFileIcon.Load(".txt", small: false, byType: true);
        var small = WindowsFileIcon.Load(".txt", small: true, byType: true);
        Assert.Equal((32, 32), (normal?.Width, normal?.Height));
        Assert.Equal((16, 16), (small?.Width, small?.Height));
        Assert.True(IconPixels.HasAlpha(normal!.Value.Bgra));
    }

    [WindowsOnlyFact]
    public void MissingExecutable_HasNoIcon()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(WindowsFileIcon.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), small: false, byType: false));
    }
}
