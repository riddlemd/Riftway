using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Riftway.Asar;

/// <summary>What an entry in an ASAR archive is.</summary>
public enum AsarEntryKind
{
    /// <summary>A file, stored in the archive or, when <see cref="AsarEntry.Unpacked"/>, beside it.</summary>
    File,
    /// <summary>A directory.</summary>
    Directory,
    /// <summary>A symbolic link to another entry, by its path from the archive's root.</summary>
    Link,
}

/// <summary>A file's integrity in an ASAR header: the SHA-256 of its contents and of each block of them.</summary>
/// <param name="Algorithm">The hash algorithm; Electron writes and reads only <c>SHA256</c>.</param>
/// <param name="Hash">The whole file's hash, lowercase hex.</param>
/// <param name="BlockSize">The block size the file was hashed in (4 MiB from <c>@electron/asar</c>).</param>
/// <param name="Blocks">Each block's hash, lowercase hex; an empty file has the one hash of nothing.</param>
public sealed record AsarIntegrity(string Algorithm, string Hash, int BlockSize, IReadOnlyList<string> Blocks);

/// <summary>An entry in an <see cref="AsarArchive"/>, as its header describes it.</summary>
public sealed class AsarEntry
{
    private readonly List<AsarEntry>? _children;
    private readonly Dictionary<string, AsarEntry>? _byName;

    internal AsarEntry(string path, AsarEntryKind kind, AsarEntry? parent)
    {
        Path = path;
        Kind = kind;
        Parent = parent;
        if (kind == AsarEntryKind.Directory)
        {
            _children = [];
            _byName = new(StringComparer.Ordinal);
        }
    }

    /// <summary>The entry's path from the archive's root, <c>/</c>-separated; empty for the root.</summary>
    public string Path { get; }

    /// <summary>The entry's name: the last part of <see cref="Path"/>.</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>Whether this is a file, a directory or a link.</summary>
    public AsarEntryKind Kind { get; }

    /// <summary>The directory holding this entry; null for the root.</summary>
    public AsarEntry? Parent { get; }

    /// <summary>A file's size in bytes.</summary>
    public long Size { get; internal init; }

    /// <summary>A packed file's offset from <see cref="AsarArchive.DataOffset"/>; -1 when there is none.</summary>
    public long Offset { get; internal init; } = -1;

    /// <summary>Whether the entry lives in the <c>.unpacked</c> folder beside the archive rather than in it.</summary>
    public bool Unpacked { get; internal init; }

    /// <summary>Whether the file was executable when packed (never set by packing on Windows).</summary>
    public bool Executable { get; internal init; }

    /// <summary>A file's hashes, when the header has them.</summary>
    public AsarIntegrity? Integrity { get; internal init; }

    /// <summary>A link's target: an entry's path from the archive's root.</summary>
    public string? LinkTarget { get; internal init; }

    /// <summary>A directory's entries, in the header's order; empty otherwise.</summary>
    public IReadOnlyList<AsarEntry> Children => (IReadOnlyList<AsarEntry>?)_children ?? [];

    internal AsarEntry? Child(string name) => _byName is not null && _byName.TryGetValue(name, out var child) ? child : null;

    internal void Add(AsarEntry child)
    {
        // As JSON.parse: a repeated key keeps its first place and its last value.
        if (_byName!.TryGetValue(child.Name, out var existing)) _children![_children.IndexOf(existing)] = child;
        else _children!.Add(child);
        _byName[child.Name] = child;
    }
}

/// <summary>An ASAR archive, Electron's app archive format: a pickled JSON header of offsets, sizes and hashes, then the
/// files back to back. Reads are positional, so one archive serves any number of threads.</summary>
/// <remarks>Paths are <c>/</c>-separated (also <c>\</c> on Windows, as Electron); links are followed as Electron's
/// archive does, and no path leaves the archive. Unpacked files are read from <c>&lt;archive&gt;.unpacked</c>.</remarks>
public sealed class AsarArchive : IDisposable
{
    private const int MaxLinkDepth = 40; // @electron/asar's SYMLINK_MAX_DEPTH, Linux's SYMLOOP_MAX
    private readonly SafeFileHandle _handle;
    private readonly long _length;

    private AsarArchive(string path, SafeFileHandle handle, long length, string header, long dataOffset, AsarEntry root, string headerHash)
    {
        FilePath = path;
        _handle = handle;
        _length = length;
        HeaderString = header;
        DataOffset = dataOffset;
        Root = root;
        HeaderHash = headerHash;
    }

    /// <summary>The archive's full path.</summary>
    public string FilePath { get; }

    /// <summary>The header's JSON, as stored.</summary>
    public string HeaderString { get; }

    /// <summary>The SHA-256 of the header's JSON, lowercase hex: what Electron's archive integrity check compares.</summary>
    public string HeaderHash { get; }

    /// <summary>Where the files begin: each packed file's <see cref="AsarEntry.Offset"/> counts from here.</summary>
    public long DataOffset { get; }

    /// <summary>The root directory.</summary>
    public AsarEntry Root { get; }

    /// <summary>Opens an archive and reads its header.</summary>
    /// <exception cref="InvalidDataException">The file is not an ASAR archive, or its header is malformed.</exception>
    public static AsarArchive Open(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            var length = RandomAccess.GetLength(handle);
            Span<byte> sizePickle = stackalloc byte[8];
            if (length < 8 || RandomAccess.Read(handle, sizePickle, 0) != 8) throw Malformed("too short");
            // A pickle: a uint32 payload size, then the payload. The first holds one uint32, the header pickle's size.
            if (BinaryPrimitives.ReadUInt32LittleEndian(sizePickle) != 4) throw Malformed("no header size");
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sizePickle[4..]);
            if (headerSize < 8 || 8 + (long)headerSize > length) throw Malformed("header size out of range");
            var header = new byte[headerSize];
            RandomAccess.Read(handle, header, 8);
            var payload = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (payload > headerSize - 4 || (headerSize - payload) % 4 != 0) throw Malformed("header pickle size");
            var stringLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (stringLength < 0 || 4 + (long)stringLength > payload) throw Malformed("header string size");
            var json = header.AsSpan(8, stringLength);
            string text;
            try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(json); }
            catch (DecoderFallbackException) { throw Malformed("header is not UTF-8"); }

            var root = new AsarEntry("", AsarEntryKind.Directory, null);
            try
            {
                using var document = JsonDocument.Parse(header.AsMemory(8, stringLength), new JsonDocumentOptions { MaxDepth = 1024 });
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                    throw Malformed("no root files");
                ReadDirectory(root, files);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OverflowException)
            {
                throw Malformed(e.Message);
            }

            return new AsarArchive(fullPath, handle, length, text, 8 + (long)headerSize, root,
                Convert.ToHexStringLower(SHA256.HashData(json)));
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        InvalidDataException Malformed(string why) => new($"{fullPath} is not a valid ASAR archive: {why}.");
    }

    private static void ReadDirectory(AsarEntry directory, JsonElement files)
    {
        foreach (var property in files.EnumerateObject())
        {
            var name = property.Name;
            if (name is "" or "." or ".." || name.AsSpan().IndexOfAny('/', '\\', '\0') >= 0)
                throw new FormatException($"entry name \"{name}\" is not a single path component");
            var path = directory.Path.Length == 0 ? name : directory.Path + "/" + name;
            var node = property.Value;
            if (node.ValueKind != JsonValueKind.Object) throw new FormatException($"\"{path}\" is not an object");
            var unpacked = node.TryGetProperty("unpacked", out var u) && u.ValueKind == JsonValueKind.True;
            if (node.TryGetProperty("files", out var children))
            {
                if (children.ValueKind != JsonValueKind.Object) throw new FormatException($"\"{path}\" files is not an object");
                var child = new AsarEntry(path, AsarEntryKind.Directory, directory) { Unpacked = unpacked };
                ReadDirectory(child, children);
                directory.Add(child);
            }
            else if (node.TryGetProperty("link", out var link))
            {
                directory.Add(new AsarEntry(path, AsarEntryKind.Link, directory)
                {
                    Unpacked = unpacked,
                    LinkTarget = link.GetString() ?? throw new FormatException($"\"{path}\" link is null"),
                });
            }
            else
            {
                var size = node.GetProperty("size").GetInt64();
                var offset = -1L;
                if (!unpacked)
                {
                    offset = long.Parse(node.GetProperty("offset").GetString() ?? "", NumberStyles.None, CultureInfo.InvariantCulture);
                }
                if (size < 0) throw new FormatException($"\"{path}\" has a negative size");
                directory.Add(new AsarEntry(path, AsarEntryKind.File, directory)
                {
                    Size = size,
                    Offset = offset,
                    Unpacked = unpacked,
                    Executable = node.TryGetProperty("executable", out var x) && x.ValueKind == JsonValueKind.True,
                    Integrity = node.TryGetProperty("integrity", out var integrity) ? ReadIntegrity(integrity) : null,
                });
            }
        }
    }

    private static AsarIntegrity ReadIntegrity(JsonElement integrity) => new(
        integrity.GetProperty("algorithm").GetString() ?? "",
        integrity.GetProperty("hash").GetString() ?? "",
        integrity.GetProperty("blockSize").GetInt32(),
        [.. integrity.GetProperty("blocks").EnumerateArray().Select(b => b.GetString() ?? "")]);

    /// <summary>Finds an entry by its path, following links on the way and, with <paramref name="followLinks"/>, at the
    /// end; null when there is none or the path leaves the archive.</summary>
    /// <exception cref="IOException">Links nest more than 40 deep.</exception>
    public AsarEntry? GetEntry(string path, bool followLinks = true) => Resolve(path, followLinks, 0);

    private AsarEntry? Resolve(string path, bool followLinks, int depth)
    {
        var parts = path.Split(OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries);
        var current = Root;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == ".") continue;
            if (part == "..")
            {
                if (current.Parent is null) return null;
                current = current.Parent;
                continue;
            }
            if (current.Kind != AsarEntryKind.Directory) return null;
            var child = current.Child(part);
            if (child is null) return null;
            if (child.Kind == AsarEntryKind.Link && (followLinks || i < parts.Length - 1))
            {
                if (depth >= MaxLinkDepth) throw new IOException($"{FilePath}: too many levels of links at \"{child.Path}\".");
                child = Resolve(child.LinkTarget!, followLinks: true, depth + 1);
                if (child is null) return null;
            }
            current = child;
        }
        return current;
    }

    /// <summary>Every file, depth first in the header's order (links are not listed).</summary>
    public IEnumerable<AsarEntry> EnumerateFiles() => Files(Root);

    private static IEnumerable<AsarEntry> Files(AsarEntry directory)
    {
        foreach (var child in directory.Children)
        {
            if (child.Kind == AsarEntryKind.File) yield return child;
            else if (child.Kind == AsarEntryKind.Directory) foreach (var file in Files(child)) yield return file;
        }
    }

    /// <summary>Opens a file for reading; with <paramref name="verifyIntegrity"/>, each block is checked against its hash
    /// before any of it is returned, as Electron's integrity check does.</summary>
    /// <exception cref="FileNotFoundException">There is no such file (or its unpacked copy is missing).</exception>
    /// <exception cref="UnauthorizedAccessException">The path is a directory.</exception>
    /// <exception cref="InvalidDataException">Verifying, and the file has no SHA-256 integrity or does not match it.</exception>
    public Stream OpenRead(string path, bool verifyIntegrity = false)
    {
        var entry = GetEntry(path) ?? throw new FileNotFoundException($"{FilePath} has no \"{path}\".", path);
        if (entry.Kind == AsarEntryKind.Directory) throw new UnauthorizedAccessException($"\"{path}\" in {FilePath} is a directory.");
        AsarIntegrity? integrity = null;
        if (verifyIntegrity)
        {
            integrity = entry.Integrity;
            if (integrity is null || integrity.Algorithm != "SHA256" || integrity.BlockSize <= 0)
                throw new InvalidDataException($"\"{entry.Path}\" in {FilePath} has no SHA256 integrity.");
        }

        if (entry.Unpacked)
        {
            var file = System.IO.Path.Combine(FilePath + ".unpacked", entry.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(file)) throw new FileNotFoundException($"The unpacked file {file} is missing.", file);
            var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new AsarEntryStream(handle, ownsHandle: true, 0, RandomAccess.GetLength(handle), integrity, entry.Path);
        }

        if (entry.Offset < 0 || entry.Offset > _length - DataOffset - entry.Size)
            throw new InvalidDataException($"\"{entry.Path}\" lies outside {FilePath}.");
        return new AsarEntryStream(_handle, ownsHandle: false, DataOffset + entry.Offset, entry.Size, integrity, entry.Path);
    }

    /// <summary>Reads a whole file; with <paramref name="verifyIntegrity"/>, its blocks and its whole hash are checked.</summary>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="UnauthorizedAccessException">The path is a directory.</exception>
    /// <exception cref="InvalidDataException">Verifying, and the file does not match its integrity.</exception>
    public byte[] ReadAllBytes(string path, bool verifyIntegrity = false)
    {
        using var stream = OpenRead(path, verifyIntegrity);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        if (verifyIntegrity && Convert.ToHexStringLower(SHA256.HashData(bytes)) != GetEntry(path)!.Integrity!.Hash)
            throw new InvalidDataException($"\"{path}\" in {FilePath} does not match its integrity hash.");
        return bytes;
    }

    /// <summary>Closes the archive.</summary>
    public void Dispose() => _handle.Dispose();
}
