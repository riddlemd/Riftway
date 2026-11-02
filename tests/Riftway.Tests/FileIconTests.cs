using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class FileIconGroupTests
{
    [Theory]
    [InlineData(@"C:\a\notes.TXT", ".TXT")]
    [InlineData(@"C:\a\b.tar.gz", ".tar.gz")]
    [InlineData(@"C:\a\b.tar.ZST", ".tar.ZST")]
    [InlineData(@"C:\a\b.toolong.gz", ".gz")]
    [InlineData(@"C:\a\b..gz", ".gz")]
    [InlineData(@"C:\a\script.User.js", ".User.js")]
    [InlineData(@"C:\a\app.exe", @"C:\a\app.exe")]
    [InlineData(@"C:\a\lib.DLL", @"C:\a\lib.DLL")]
    [InlineData(@"C:\a\icon.ico", @"C:\a\icon.ico")]
    [InlineData(@"C:\a\b.tar.exe", @"C:\a\b.tar.exe")]
    [InlineData(@"C:\a\README", "")]
    [InlineData(@"C:\dir.v2\README", "")]
    [InlineData(@"C:\a\.bashrc", ".bashrc")]
    [InlineData(@"C:\a\trailing.", ".")]
    [InlineData(@"C:\a\x.txt\", ".txt")]
    [InlineData(@"C:", "")]
    [InlineData("..", "")]
    [InlineData("", "")]
    public void IconGroup_IsThePathForExecutablesAndIconsElseChromiumsExtension(string path, string group)
        => Assert.Equal(group, RiftwayApp.IconGroup(path));

    [Fact]
    public void NormalizeIconPath_TurnsSlashesIntoBackslashesOnWindows()
    {
        if (OperatingSystem.IsWindows()) Assert.Equal(@"C:\a\b.txt", RiftwayApp.NormalizeIconPath("C:/a/b.txt"));
    }

    // Windows path semantics (/ becomes \); getFileIcon has a backend on Windows only.
    [WindowsOnlyTheory]
    [InlineData(FileIconSize.Small, 16)]
    [InlineData(FileIconSize.Normal, 32)]
    [InlineData(FileIconSize.Large, 48)]
    public void FileIconPixelSize_IsUtilReadIcons(FileIconSize size, int pixels)
        => Assert.Equal(pixels, RiftwayApp.FileIconPixelSize(size));
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

[Collection(nameof(FileIconCollection))]
public sealed class GetFileIconTests : IDisposable
{
    private readonly IFileIconBackend _saved = RiftwayApp.FileIconBackend;
    private readonly FakeBackend _backend = new();

    public GetFileIconTests()
    {
        RiftwayApp.FileIconBackend = _backend;
        RiftwayApp.ClearFileIconCache();
    }

    public void Dispose()
    {
        RiftwayApp.FileIconBackend = _saved;
        RiftwayApp.ClearFileIconCache();
    }

    [Theory]
    [InlineData(null, FileIconSize.Normal)]
    [InlineData(FileIconSize.Small, FileIconSize.Small)]
    [InlineData(FileIconSize.Large, FileIconSize.Large)]
    public async Task ATypeIsLookedUpByExtensionAtTheAskedSize(FileIconSize? size, FileIconSize expected)
    {
        await RiftwayApp.LoadFileIcon(@"C:\a.txt", size is { } s ? new FileIconOptions { Size = s } : null);
        Assert.Equal([$"type:.txt:{expected}"], _backend.Calls);
    }

    [Fact]
    public async Task AFileWithoutExtensionIsLookedUpByTheEmptyType()
    {
        await RiftwayApp.LoadFileIcon(@"C:\a\README", null);
        Assert.Equal(["type::Normal"], _backend.Calls);
    }

    // Windows path semantics (/ becomes \); getFileIcon has a backend on Windows only.
    [WindowsOnlyTheory]
    [InlineData(FileIconSize.Small, 16)]
    [InlineData(FileIconSize.Normal, 32)]
    [InlineData(FileIconSize.Large, 48)]
    public async Task AnExecutableIsReadFromTheFile(FileIconSize size, int pixels)
    {
        await RiftwayApp.LoadFileIcon(@"C:/a/app.exe", new FileIconOptions { Size = size });
        Assert.Equal([$"file:C:\\a\\app.exe:{pixels}"], _backend.Calls);
    }

    [Fact]
    public async Task AnUnreadableExecutableFallsBackToItsExtensionsIcon()
    {
        _backend.NoFile = true;
        var image = await RiftwayApp.LoadFileIcon(@"C:\a\app.EXE", null);
        Assert.Equal([@"file:C:\a\app.EXE:32", "type:.EXE:Normal"], _backend.Calls);
        Assert.Equal(FakeBackend.Pixels, image.ToBitmap(2));
    }

    [Fact]
    public async Task TheImageCarriesTheBackendsScale()
    {
        var type = await RiftwayApp.LoadFileIcon("a.txt", null);
        Assert.Equal([2.0, 4.0], type.GetScaleFactors());
        Assert.Equal((1, 1), type.GetSize(4));
        Assert.Equal((1, 1), type.GetSize(2));
        var file = await RiftwayApp.LoadFileIcon("a.exe", null);
        Assert.Equal([1.0], file.GetScaleFactors());
        Assert.Equal((2, 2), file.GetSize());
    }

    [Fact]
    public async Task APathSeenBeforeIsAnsweredFromTheCachePerSize()
    {
        await RiftwayApp.LoadFileIcon(@"C:\a.txt", null);
        await RiftwayApp.LoadFileIcon(@"C:\a.txt", null);
        Assert.Single(_backend.Calls);

        await RiftwayApp.LoadFileIcon(@"C:\a.txt", new FileIconOptions { Size = FileIconSize.Small });
        await RiftwayApp.LoadFileIcon(@"C:\b.txt", null);
        Assert.Equal(3, _backend.Calls.Count);
        await RiftwayApp.LoadFileIcon(@"C:\b.txt", null);
        Assert.Equal(3, _backend.Calls.Count);
    }

    [Fact]
    public async Task NoIconFaultsWithElectronsMessageAndIsNotCached()
    {
        _backend.None = true;
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => RiftwayApp.LoadFileIcon("a.txt", null));
        Assert.Equal("Failed to get file icon.", e.Message);

        _backend.None = false;
        await RiftwayApp.LoadFileIcon("a.txt", null);
        Assert.Equal(2, _backend.Calls.Count);
    }

    [Fact]
    public async Task AGroupNotReadFromTheFileNeverReadsIt()
    {
        _backend.ByType = true;
        await RiftwayApp.LoadFileIcon("/a/app.exe", null);
        Assert.Equal(["type:type..exe:Normal"], _backend.Calls);
    }

    [Fact]
    public async Task PathsInOneGroupShareItsCachedIcon()
    {
        _backend.ByType = true;
        await RiftwayApp.LoadFileIcon("/a/x.txt", null);
        await RiftwayApp.LoadFileIcon("/b/y.txt", null);
        await RiftwayApp.LoadFileIcon("/b/y.txt", null);
        Assert.Equal(["type:type..txt:Normal", "type:type..txt:Normal"], _backend.Calls);
    }

    [Fact]
    public async Task TheCachedIconIsCopiedIntoEachImage()
    {
        var first = await RiftwayApp.LoadFileIcon("a.txt", null);
        var second = await RiftwayApp.LoadFileIcon("a.txt", null);
        Assert.NotSame(first.BitmapAt(2).Bgra, second.BitmapAt(2).Bgra);
    }

    // The Windows getApplicationInfoForProtocol path, with Windows path normalization.
    [WindowsOnlyFact]
    public async Task ApplicationInfoUsesTheLargeIconOfTheNormalizedPath()
    {
        var info = await RiftwayApp.ApplicationInfoFor("C:/apps/x.exe", "X");
        Assert.Equal((@"C:\apps\x.exe", "X"), (info.Path, info.Name));
        Assert.Equal([@"file:C:\apps\x.exe:48"], _backend.Calls);
        _backend.None = _backend.NoFile = true;
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => RiftwayApp.ApplicationInfoFor(@"C:\apps\y.exe", "Y"));
        Assert.Equal("Failed to get file icon.", e.Message);
    }

    private sealed class FakeBackend : IFileIconBackend
    {
        public static readonly byte[] Pixels = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        public List<string> Calls { get; } = [];
        public bool None, NoFile, ByType;

        public (string Group, bool FromFile) GroupFor(string path)
            => ByType ? ("type." + RiftwayApp.PosixFinalExtension(path), false) : RiftwayApp.WindowsIconGroup(path);

        public FileIconPixels[]? ForGroup(string group, FileIconSize size)
        {
            Calls.Add($"type:{group}:{size}");
            return None ? null : [new FileIconPixels((byte[])Pixels.Clone(), 2, 2, 2f), new FileIconPixels([.. Pixels, .. Pixels, .. Pixels, .. Pixels], 4, 4, 4f)];
        }

        public FileIconPixels[]? FromFile(string path, int pixels)
        {
            Calls.Add($"file:{path}:{pixels}");
            return NoFile ? null : [new FileIconPixels((byte[])Pixels.Clone(), 2, 2, 1f)];
        }
    }
}

[CollectionDefinition(nameof(FileIconCollection), DisableParallelization = true)]
public sealed class FileIconCollection;

[Collection(nameof(FileIconCollection))]
public sealed class WindowsFileIconTests
{
    private static readonly string s_notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

    [WindowsOnlyFact]
    public void TextType_HasASmallAndALargeSystemIcon()
    {
        if (!OperatingSystem.IsWindows()) return;
        var normal = WindowsFileIcon.LoadForExtension(".txt", small: false);
        var small = WindowsFileIcon.LoadForExtension(".txt", small: true);
        float scale = WindowsFileIcon.SystemDpiScale();
        Assert.Equal(((int)(32 * scale), (int)(32 * scale)), (normal?.Width, normal?.Height));
        Assert.Equal(((int)(16 * scale), (int)(16 * scale)), (small?.Width, small?.Height));
        Assert.True(IconPixels.HasAlpha(normal!.Value.Bgra));
    }

    [WindowsOnlyFact]
    public void AnExecutablesFirstIconLoadsAtTheAskedSize()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal((48, 48), WindowsFileIcon.LoadFromFile(s_notepad, 48) is var (_, w, h) ? (w, h) : default);
        Assert.Equal((16, 16), WindowsFileIcon.LoadFromFile(s_notepad, 16) is var (_, w2, h2) ? (w2, h2) : default);
    }

    [WindowsOnlyFact]
    public void AMissingFileOrANonImageHasNoFileIcon()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(WindowsFileIcon.LoadFromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), 32));
        string ico = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ico");
        File.WriteAllBytes(ico, [0, 0, 1, 0, 0, 0]);
        try { Assert.Null(WindowsFileIcon.LoadFromFile(ico, 32)); }
        finally { File.Delete(ico); }
    }

    [WindowsOnlyFact]
    public async Task AMissingExecutableResolvesWithTheExecutableTypeIcon()
    {
        if (!OperatingSystem.IsWindows()) return;
        var missing = await RiftwayApp.LoadFileIcon(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), null);
        Assert.Equal(WindowsFileIcon.LoadForExtension(".exe", small: false)!.Value.Bgra, missing.ToBitmap(WindowsFileIcon.SystemDpiScale()));
    }

    [WindowsOnlyFact]
    public async Task AnExecutablesLargeIconIs48AtScale1()
    {
        if (!OperatingSystem.IsWindows()) return;
        var large = await RiftwayApp.LoadFileIcon(s_notepad, new FileIconOptions { Size = FileIconSize.Large });
        Assert.Equal([1.0], large.GetScaleFactors());
        Assert.Equal((48, 48), large.GetSize());
    }
}

public sealed class MacFileIconRuleTests
{
    [Theory]
    [InlineData("/a/notes.TXT", ".TXT")]
    [InlineData("/a/b.tar.gz", ".gz")]
    [InlineData("/a/README", "")]
    [InlineData("/dir.v2/README", "")]
    [InlineData("/a/.bashrc", ".bashrc")]
    [InlineData("/a/trailing.", ".")]
    [InlineData("/a/x.txt/", ".txt")]
    [InlineData(@"/a/back\slash.v1\name", @".v1\name")]
    [InlineData("/", "")]
    [InlineData("..", "")]
    [InlineData("a/.", "")]
    [InlineData("", "")]
    public void PosixFinalExtension_IsChromiums(string path, string extension)
        => Assert.Equal(extension, RiftwayApp.PosixFinalExtension(path));

    [Theory]
    [InlineData(FileIconSize.Small, 16)]
    [InlineData(FileIconSize.Normal, 32)]
    public void MacSizes_AreReadIconsPoints(FileIconSize size, int points)
        => Assert.Equal(points, RiftwayApp.MacFileIconPoints(size));

    [Fact]
    public void MacLarge_IsNotSupported()
        => Assert.Throws<NotSupportedException>(() => RiftwayApp.MacFileIconPoints(FileIconSize.Large));
}

[Collection(nameof(FileIconCollection))]
public sealed class MacFileIconTests
{
    [Fact]
    public void AMissingFileIsTypedByItsExtension()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal("public.plain-text", MacFileIcon.TypeFor("/no/such/dir/a.txt", "txt"));
        Assert.Equal("public.png", MacFileIcon.TypeFor("/no/such/dir/a.PNG", "PNG"));
    }

    [Fact]
    public void AnExistingFileIsTypedByItsContentNotItsName()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dir = Directory.CreateTempSubdirectory("riftway-icon").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(dir, "looks.txt")).FullName;
            Assert.Equal("public.folder", MacFileIcon.TypeFor(folder, "txt"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NoNameAndNoExtensionIsPublicContent()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal("public.content", MacFileIcon.TypeFor("", null));
        Assert.StartsWith("dyn.", MacFileIcon.TypeFor("/no/such/dir/a.zzqqxv", "zzqqxv"));
    }

    [Fact]
    public void ATypeIconIsDrawnAtEachScale()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var icon = MacFileIcon.Icon("public.plain-text", 16, [1, 2]);
        Assert.Equal([(1.0, 16, 16), (2.0, 32, 32)], icon.Select(b => (b.Scale, b.Width, b.Height)));
        foreach (var b in icon)
        {
            Assert.Equal(b.Width * b.Height * 4, b.Bgra.Length);
            Assert.True(IconPixels.HasAlpha(b.Bgra));
            // Premultiplied: no colour channel exceeds its alpha.
            for (var i = 0; i < b.Bgra.Length; i += 4) Assert.True(Math.Max(b.Bgra[i], Math.Max(b.Bgra[i + 1], b.Bgra[i + 2])) <= b.Bgra[i + 3]);
        }
    }

    [Fact]
    public void TheBackendGroupsByTypeFromTheExtensionWithoutItsDot()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(("public.plain-text", false), new MacFileIconBackend().GroupFor("/no/such/dir/a.txt"));
        Assert.Equal(("public.content", false), new MacFileIconBackend().GroupFor("/no/such/dir/README"));
    }

    [Fact]
    public void AnUnknownIdentifierHasNoIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Empty(MacFileIcon.Icon("", 16, [1, 2]));
    }

    [Fact]
    public async Task GetFileIcon_HasOneAndTwoTimesRepresentations()
    {
        if (!OperatingSystem.IsMacOS()) return;
        RiftwayApp.ClearFileIconCache();
        var image = await RiftwayApp.LoadFileIcon("/no/such/dir/a.txt", new FileIconOptions { Size = FileIconSize.Small });
        Assert.Equal([1.0, 2.0], image.GetScaleFactors());
        Assert.Equal((16, 16), image.GetSize(1));
        Assert.Equal((16, 16), image.GetSize(2));
        Assert.Equal(32, image.BitmapAt(2).Width);
        await Assert.ThrowsAsync<NotSupportedException>(() => RiftwayApp.LoadFileIcon("/no/such/dir/a.txt", new FileIconOptions { Size = FileIconSize.Large }));
    }
}
