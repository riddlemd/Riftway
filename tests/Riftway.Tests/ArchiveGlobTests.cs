using Riftway.Pack;

namespace Riftway.Tests;

// Riftway.Pack's --unpack / --unpack-dir patterns, matched as @electron/asar matches them (minimatch).
public sealed class ArchiveGlobTests
{
    [Theory]
    [InlineData("*.png", "dir2/file2.png", true)] // matchBase: a slashless pattern matches the name anywhere
    [InlineData("*.png", "file2.png", true)]
    [InlineData("*.png", "dir2/file2.pngx", false)]
    [InlineData("dir2/*.png", "dir2/a.png", true)]
    [InlineData("dir2/*.png", "x/dir2/a.png", false)]
    [InlineData("dir2/*.png", "dir2/sub/a.png", false)]
    [InlineData("**/*.css", "a/b/c.css", true)]
    [InlineData("**/*.css", "c.css", true)]
    [InlineData("media/**", "media/a/b.mp4", true)]
    [InlineData("*.{png,jpg}", "x/a.jpg", true)]
    [InlineData("*.{png,jpg}", "x/a.gif", false)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file10.txt", false)]
    [InlineData("[ab].txt", "a.txt", true)]
    [InlineData("[!ab].txt", "c.txt", true)]
    [InlineData("[!ab].txt", "a.txt", false)]
    [InlineData("a+b(1).txt", "a+b(1).txt", true)]
    [InlineData("a+b(1).txt", "aab(1).txt", false)]
    [InlineData("dir2\\*.png", "dir2/a.png", true)]
    public void Files(string pattern, string path, bool matches) => Assert.Equal(matches, new ArchiveGlob(pattern).MatchesFile(path));

    [Theory]
    [InlineData("dir2", "dir2", true)]
    [InlineData("dir2", "dir2/sub", true)]
    [InlineData("dir2", "dir20", true)] // @electron/asar's isUnpackedDir is a plain startsWith
    [InlineData("dir2", "dir1", false)]
    [InlineData("{x1,x2}", "x2", true)] // electron/asar's packthis-unpack-dir-glob
    [InlineData("{x1,x2}", "x3", false)]
    [InlineData("media/*", "media/clips", true)]
    [InlineData("media/", "media", true)]
    public void Directories(string pattern, string path, bool matches) => Assert.Equal(matches, new ArchiveGlob(pattern).MatchesDirectory(path));
}
