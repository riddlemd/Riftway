using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Riftway.Asar;

namespace Riftway.Tests;

// Fixtures are electron/asar's own test archives and inputs (Fixtures/asar/LICENSE.md): reading them is reading what
// Electron reads, and packing their inputs must give their expected archives byte for byte.
public sealed class AsarTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "asar");
    private readonly string _temp = Directory.CreateTempSubdirectory("riftway-asar-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static string Fixture(string path) => Path.Combine(Fixtures, path);
    private static byte[] Input(string path) => File.ReadAllBytes(Fixture("input/packthis/" + path));

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // An archive with this header JSON and these file bytes, pickled as Electron's.
    private string Craft(string json, string data = "")
    {
        var header = Encoding.UTF8.GetBytes(json);
        var aligned = (header.Length + 3) & ~3;
        var body = Encoding.UTF8.GetBytes(data);
        var bytes = new byte[16 + aligned + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(8 + aligned));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)(4 + aligned));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), header.Length);
        header.CopyTo(bytes, 16);
        body.CopyTo(bytes, 16 + aligned);
        var path = Path.Combine(_temp, Guid.NewGuid().ToString("n") + ".asar");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Read_ListsTheHeaderInItsOrder()
    {
        using var archive = AsarArchive.Open(Fixture("input/extractthis.asar"));
        Assert.Equal(["dir1", "dir2", "emptyfile.txt", "file0.txt"], archive.Root.Children.Select(c => c.Name));
        Assert.Equal(["dir1/file1.txt", "dir2/file2.png", "dir2/file3.txt", "emptyfile.txt", "file0.txt"],
            archive.EnumerateFiles().Select(e => e.Path));
        Assert.Equal(AsarEntryKind.Directory, archive.GetEntry("dir2")!.Kind);
    }

    [Theory]
    [InlineData("dir1/file1.txt")]
    [InlineData("dir2/file2.png")]
    [InlineData("dir2/file3.txt")]
    [InlineData("emptyfile.txt")]
    [InlineData("file0.txt")]
    public void Read_GivesEachFilesBytes(string path)
    {
        using var archive = AsarArchive.Open(Fixture("input/extractthis.asar"));
        Assert.Equal(Input(path), archive.ReadAllBytes(path));
        Assert.Equal(Input(path).Length, archive.GetEntry(path)!.Size);
    }

    [Fact]
    public void Read_AnUnpackedFile_ComesFromTheUnpackedFolder()
    {
        var archivePath = Path.Combine(_temp, "app.asar");
        File.Copy(Fixture("input/extractthis-unpack.asar"), archivePath);
        using var archive = AsarArchive.Open(archivePath);
        var png = archive.GetEntry("dir2/file2.png")!;
        Assert.True(png.Unpacked);
        Assert.Throws<FileNotFoundException>(() => archive.ReadAllBytes("dir2/file2.png"));

        Directory.CreateDirectory(Path.Combine(_temp, "app.asar.unpacked", "dir2"));
        File.WriteAllBytes(Path.Combine(_temp, "app.asar.unpacked", "dir2", "file2.png"), Input("dir2/file2.png"));
        Assert.Equal(Input("dir2/file2.png"), archive.ReadAllBytes("dir2/file2.png"));
        Assert.Equal(Input("file0.txt"), archive.ReadAllBytes("file0.txt"));
    }

    [Fact]
    public void Links_AreFollowedUnlessAsked()
    {
        using var archive = AsarArchive.Open(Fixture("input/stat-symlink.asar"));
        var link = archive.GetEntry("real.txt", followLinks: false)!;
        Assert.Equal(AsarEntryKind.Link, link.Kind);
        Assert.Equal("Current/real.txt", link.LinkTarget);
        Assert.Equal("A/real.txt", archive.GetEntry("real.txt")!.Path);
        Assert.Equal("A/real.txt", archive.GetEntry("Current/real.txt")!.Path);
        Assert.Equal(19, archive.ReadAllBytes("real.txt").Length);
        Assert.Equal(archive.ReadAllBytes("A/real.txt"), archive.ReadAllBytes("Current/real.txt"));
        // A link on the way is followed even when the last part's is not.
        Assert.Equal("A/real.txt", archive.GetEntry("Current/real.txt", followLinks: false)!.Path);
        Assert.Equal("A", archive.GetEntry("Current/real.txt/..", followLinks: false)!.Path);
    }

    [Fact]
    public void Links_InALoop_Fail()
    {
        using var archive = AsarArchive.Open(Craft("{\"files\":{\"a\":{\"link\":\"b\"},\"b\":{\"link\":\"a\"}}}"));
        Assert.Equal(AsarEntryKind.Link, archive.GetEntry("a", followLinks: false)!.Kind);
        Assert.Throws<IOException>(() => archive.GetEntry("a"));
    }

    [Fact]
    public void Paths_StayInsideTheArchive()
    {
        using var archive = AsarArchive.Open(Fixture("input/extractthis.asar"));
        Assert.Null(archive.GetEntry("../extractthis.asar"));
        Assert.Null(archive.GetEntry("dir1/../../x"));
        Assert.Null(archive.GetEntry("../file0.txt"));
        Assert.Null(archive.GetEntry("dir1/../../file0.txt"));
        Assert.Null(archive.GetEntry("missing.txt"));
        Assert.Null(archive.GetEntry("file0.txt/x"));
        Assert.Equal("dir2/file3.txt", archive.GetEntry("dir1/../dir2/./file3.txt")!.Path);
        Assert.Equal("dir2/file3.txt", archive.GetEntry("/dir2//file3.txt")!.Path);
        Assert.Same(archive.Root, archive.GetEntry(""));
        Assert.Equal(OperatingSystem.IsWindows(), archive.GetEntry(@"dir2\file3.txt") is not null);
        Assert.Throws<FileNotFoundException>(() => archive.ReadAllBytes("missing.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => archive.ReadAllBytes("dir1"));
    }

    [Fact]
    public void Header_IsTheJsonAndItsHash()
    {
        var bytes = File.ReadAllBytes(Fixture("expected/packthis.asar"));
        using var archive = AsarArchive.Open(Fixture("expected/packthis.asar"));
        Assert.StartsWith("{\"files\":{\".hiddenfile.txt\":{\"size\":19,\"offset\":\"0\",", archive.HeaderString);
        var json = Encoding.UTF8.GetBytes(archive.HeaderString);
        Assert.Equal(json, bytes.AsSpan(16, json.Length).ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(json)), archive.HeaderHash);
        Assert.Equal(8 + 1540, archive.DataOffset);
    }

    [Fact]
    public void Integrity_IsCheckedWhenAsked()
    {
        using (var archive = AsarArchive.Open(Fixture("expected/packthis.asar")))
        {
            var entry = archive.GetEntry("dir2/file2.png")!;
            Assert.Equal("SHA256", entry.Integrity!.Algorithm);
            Assert.Equal(4 * 1024 * 1024, entry.Integrity.BlockSize);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Input("dir2/file2.png"))), entry.Integrity.Hash);
            Assert.Equal(Input("dir2/file2.png"), archive.ReadAllBytes("dir2/file2.png", verifyIntegrity: true));
            Assert.Empty(archive.ReadAllBytes("emptyfile.txt", verifyIntegrity: true));
        }

        var tampered = Path.Combine(_temp, "tampered.asar");
        var bytes = File.ReadAllBytes(Fixture("expected/packthis.asar"));
        bytes[8 + 1540 + 28 + 100] ^= 0xFF; // a byte of dir2/file2.png
        File.WriteAllBytes(tampered, bytes);
        using (var archive = AsarArchive.Open(tampered))
        {
            Assert.Throws<InvalidDataException>(() => archive.ReadAllBytes("dir2/file2.png", verifyIntegrity: true));
            Assert.NotEqual(Input("dir2/file2.png"), archive.ReadAllBytes("dir2/file2.png"));
            Assert.Equal(Input("dir2/file3.txt"), archive.ReadAllBytes("dir2/file3.txt", verifyIntegrity: true));
        }

        using (var archive = AsarArchive.Open(Fixture("input/extractthis.asar")))
            Assert.Throws<InvalidDataException>(() => archive.ReadAllBytes("file0.txt", verifyIntegrity: true));
    }

    [Fact]
    public void Integrity_ChecksTheWholeHashToo()
    {
        var integrity = $"\"integrity\":{{\"algorithm\":\"SHA256\",\"hash\":\"{Sha("xyz")}\",\"blockSize\":4194304,\"blocks\":[\"{Sha("abc")}\"]}}";
        using var archive = AsarArchive.Open(Craft("{\"files\":{\"f\":{\"size\":3,\"offset\":\"0\"," + integrity + "}}}", "abc"));
        using (var stream = archive.OpenRead("f", verifyIntegrity: true)) Assert.Equal((int)'a', stream.ReadByte()); // blocks match
        Assert.Throws<InvalidDataException>(() => archive.ReadAllBytes("f", verifyIntegrity: true));
        Assert.Equal("abc", Encoding.UTF8.GetString(archive.ReadAllBytes("f")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\\\b")]
    public void Open_RefusesNamesThatAreNotOnePart(string name)
        => Assert.Throws<InvalidDataException>(() => AsarArchive.Open(Craft("{\"files\":{\"" + name + "\":{\"size\":0,\"offset\":\"0\"}}}")));

    [Fact]
    public void Read_RefusesAFileOutsideTheArchive()
    {
        using var archive = AsarArchive.Open(Craft("{\"files\":{\"f\":{\"size\":4,\"offset\":\"1\"},\"g\":{\"size\":3,\"offset\":\"1\"}}}", "abcd"));
        Assert.Throws<InvalidDataException>(() => archive.ReadAllBytes("f"));
        Assert.Equal("bcd", Encoding.UTF8.GetString(archive.ReadAllBytes("g")));
    }

    [Fact]
    public void OpenRead_SeeksWithinTheEntry()
    {
        using var archive = AsarArchive.Open(Fixture("expected/packthis.asar"));
        foreach (var verify in new[] { false, true })
        {
            using var stream = archive.OpenRead("dir2/file2.png", verify);
            Assert.Equal(182, stream.Length);
            stream.Seek(100, SeekOrigin.Begin);
            var rest = new byte[200];
            var read = stream.ReadAtLeast(rest, 200, throwOnEndOfStream: false);
            Assert.Equal(Input("dir2/file2.png")[100..], rest[..read]);
            Assert.Equal(0, stream.Read(rest));
        }
    }

    [Fact]
    public void Open_RefusesWhatIsNotAnArchive()
    {
        var path = Path.Combine(_temp, "bad.asar");
        File.WriteAllBytes(path, [4, 0, 0, 0, 0xFF, 0xFF, 0, 0, 1, 2]);
        Assert.Throws<InvalidDataException>(() => AsarArchive.Open(path));
        File.WriteAllBytes(path, File.ReadAllBytes(Fixture("expected/packthis.asar"))[..100]);
        Assert.Throws<InvalidDataException>(() => AsarArchive.Open(path));
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => AsarArchive.Open(path));
    }

    [Theory]
    // As electron/asar's own tests make them: the unpack archives with --exclude-hidden.
    [InlineData("packthis", "packthis.asar", null, null)]
    [InlineData("packthis", "packthis-unpack.asar", ".png", null)]
    [InlineData("packthis", "packthis-unpack-dir.asar", null, "dir2")]
    [InlineData("packthis-unicode-path", "packthis-unicode-path.asar", null, null)]
    public void Pack_MatchesElectronsArchive(string input, string expected, string? unpackSuffix, string? unpackDirectory)
    {
        var output = Path.Combine(_temp, expected);
        AsarPacker.Pack(Fixture("input/" + input), output, new AsarPackOptions
        {
            IncludeHidden = unpackSuffix is null && unpackDirectory is null,
            Unpack = unpackSuffix is null ? null : p => p.EndsWith(unpackSuffix, StringComparison.Ordinal),
            UnpackDirectory = unpackDirectory is null ? null : p => p == unpackDirectory,
        });
        Assert.Equal(File.ReadAllBytes(Fixture("expected/" + expected)), File.ReadAllBytes(output));

        var unpacked = output + ".unpacked";
        if (unpackSuffix is null && unpackDirectory is null) Assert.False(Directory.Exists(unpacked));
        else
        {
            using var archive = AsarArchive.Open(output);
            var outside = archive.EnumerateFiles().Where(e => e.Unpacked).Select(e => e.Path).ToList();
            Assert.Equal(unpackDirectory is null ? ["dir2/file2.png"] : ["dir2/file2.png", "dir2/file3.txt"], outside);
            foreach (var path in outside)
                Assert.Equal(Input(path), File.ReadAllBytes(Path.Combine(unpacked, path)));
            foreach (var entry in archive.EnumerateFiles())
                Assert.Equal(Input(entry.Path), archive.ReadAllBytes(entry.Path, verifyIntegrity: true));
        }
    }

    [Fact]
    public void Pack_CanLeaveOutHiddenFiles()
    {
        var output = Path.Combine(_temp, "out.asar");
        AsarPacker.Pack(Fixture("input/packthis"), output, new AsarPackOptions { IncludeHidden = false });
        using var archive = AsarArchive.Open(output);
        Assert.Equal(["dir1", "dir2", "emptyfile.txt", "file0.txt"], archive.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void Pack_SortsByCodeUnit()
    {
        var source = Directory.CreateDirectory(Path.Combine(_temp, "src")).FullName;
        foreach (var name in new[] { "a.txt", "B.txt", "_.txt" }) File.WriteAllText(Path.Combine(source, name), name);
        var output = Path.Combine(_temp, "out.asar");
        AsarPacker.Pack(source, output);
        using var archive = AsarArchive.Open(output);
        Assert.Equal(["B.txt", "_.txt", "a.txt"], archive.Root.Children.Select(c => c.Name));
        Assert.Equal(["0", "5", "10"], archive.Root.Children.Select(c => c.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Pack_UnpacksEverythingUnderAnUnpackedDirectory()
    {
        var source = Directory.CreateDirectory(Path.Combine(_temp, "src")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "media", "deep"));
        File.WriteAllText(Path.Combine(source, "media", "deep", "clip.txt"), "clip");
        File.WriteAllText(Path.Combine(source, "index.html"), "html");
        var output = Path.Combine(_temp, "out.asar");
        AsarPacker.Pack(source, output, new AsarPackOptions { UnpackDirectory = p => p == "media" });
        using var archive = AsarArchive.Open(output);
        Assert.True(archive.GetEntry("media/deep")!.Unpacked);
        Assert.True(archive.GetEntry("media/deep/clip.txt")!.Unpacked);
        Assert.False(archive.GetEntry("index.html")!.Unpacked);
        Assert.Equal("clip", File.ReadAllText(Path.Combine(output + ".unpacked", "media", "deep", "clip.txt")));
        Assert.Equal("clip", Encoding.UTF8.GetString(archive.ReadAllBytes("media/deep/clip.txt", verifyIntegrity: true)));
    }

    [Fact]
    public void Pack_StoresEqualContentsOnce()
    {
        var source = Directory.CreateDirectory(Path.Combine(_temp, "src")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "same");
        File.WriteAllText(Path.Combine(source, "b.txt"), "same");
        File.WriteAllText(Path.Combine(source, "c.txt"), "other");
        var output = Path.Combine(_temp, "out.asar");
        AsarPacker.Pack(source, output);
        using var archive = AsarArchive.Open(output);
        Assert.Equal(archive.GetEntry("a.txt")!.Offset, archive.GetEntry("b.txt")!.Offset);
        Assert.Equal(4, archive.GetEntry("c.txt")!.Offset);
        Assert.Equal(archive.DataOffset + 9, new FileInfo(output).Length);
        Assert.Equal("same", Encoding.UTF8.GetString(archive.ReadAllBytes("b.txt", verifyIntegrity: true)));
    }

    [Fact]
    public void Pack_HashesFourMegabyteBlocks()
    {
        var source = Directory.CreateDirectory(Path.Combine(_temp, "src")).FullName;
        var big = new byte[4 * 1024 * 1024 + 10];
        new Random(7).NextBytes(big);
        File.WriteAllBytes(Path.Combine(source, "big.bin"), big);
        var output = Path.Combine(_temp, "out.asar");
        AsarPacker.Pack(source, output);

        long offset;
        using (var archive = AsarArchive.Open(output))
        {
            var integrity = archive.GetEntry("big.bin")!.Integrity!;
            Assert.Equal([Convert.ToHexStringLower(SHA256.HashData(big.AsSpan(0, 4 * 1024 * 1024))),
                Convert.ToHexStringLower(SHA256.HashData(big.AsSpan(4 * 1024 * 1024)))], integrity.Blocks);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(big)), integrity.Hash);
            Assert.Equal(big, archive.ReadAllBytes("big.bin", verifyIntegrity: true));
            offset = archive.DataOffset;
        }

        var bytes = File.ReadAllBytes(output);
        bytes[offset + 4 * 1024 * 1024 + 5] ^= 1;
        File.WriteAllBytes(output, bytes);
        using (var archive = AsarArchive.Open(output))
        {
            using var stream = archive.OpenRead("big.bin", verifyIntegrity: true);
            var first = new byte[4 * 1024 * 1024];
            stream.ReadExactly(first); // the first block is intact
            Assert.Equal(big.AsSpan(0, first.Length).ToArray(), first);
            Assert.Throws<InvalidDataException>(() => stream.ReadByte());
        }
    }

    [Fact]
    public void Pack_KeepsLinksInsideAndRefusesOthers()
    {
        var source = Directory.CreateDirectory(Path.Combine(_temp, "src")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "A"));
        File.WriteAllText(Path.Combine(source, "A", "real.txt"), "real");
        try { File.CreateSymbolicLink(Path.Combine(source, "real.txt"), Path.Combine("A", "real.txt")); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // no symlink privilege
        var output = Path.Combine(_temp, "out.asar");
        AsarPacker.Pack(source, output);
        using (var archive = AsarArchive.Open(output))
        {
            Assert.Equal("A/real.txt", archive.GetEntry("real.txt", followLinks: false)!.LinkTarget);
            Assert.Equal("real", Encoding.UTF8.GetString(archive.ReadAllBytes("real.txt")));
        }

        File.CreateSymbolicLink(Path.Combine(source, "out.txt"), Path.Combine(_temp, "elsewhere.txt"));
        Assert.Throws<IOException>(() => AsarPacker.Pack(source, Path.Combine(_temp, "out2.asar")));
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("q\"b\\s/", "\"q\\\"b\\\\s/\"")]
    [InlineData("\b\f\n\r\t\u0001\u001f\u007f", "\"\\b\\f\\n\\r\\t\\u0001\\u001f\u007f\"")]
    [InlineData("女の子 é \U0001F600", "\"女の子 é \U0001F600\"")]
    public void HeaderStrings_AreQuotedAsJsonStringifyDoes(string value, string json)
        => Assert.Equal(json, AsarPacker.Quote(value));

    [Fact]
    public void LoneSurrogates_AreEscaped() // built at run time: attribute strings cannot hold lone surrogates
        => Assert.Equal("\"\\ud800x\\udc00\"", AsarPacker.Quote(new string(['\ud800', 'x', '\udc00'])));
}
