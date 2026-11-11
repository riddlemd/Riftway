using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Riftway.Asar;

/// <summary>What <see cref="AsarPacker.Pack"/> puts in the archive and what it leaves beside it.</summary>
public sealed class AsarPackOptions
{
    /// <summary>Whether files and directories whose names start with <c>.</c> are packed (<c>@electron/asar</c>'s
    /// <c>dot</c>, on by default).</summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>Files (by their <c>/</c>-separated path from the source) to keep in <c>&lt;archive&gt;.unpacked</c>,
    /// listed in the header but stored outside it, as <c>@electron/asar</c>'s <c>unpack</c>.</summary>
    public Func<string, bool>? Unpack { get; init; }

    /// <summary>Directories to keep in <c>&lt;archive&gt;.unpacked</c> with everything under them, as
    /// <c>unpackDir</c>.</summary>
    public Func<string, bool>? UnpackDirectory { get; init; }
}

/// <summary>Writes ASAR archives as <c>@electron/asar</c>'s <c>createPackage</c> does, byte for byte: entries in sorted
/// path order, each file's SHA-256 integrity in 4 MiB blocks, and equal contents stored once.</summary>
public static class AsarPacker
{
    private const int BlockSize = 4 * 1024 * 1024;

    /// <summary>Packs a directory into an archive; unpacked files are copied to <c>&lt;archive&gt;.unpacked</c>.</summary>
    /// <exception cref="DirectoryNotFoundException">The source is not a directory.</exception>
    /// <exception cref="IOException">A link points outside the source, or a file is 4 GiB or more (as Electron).</exception>
    public static void Pack(string sourceDirectory, string archivePath, AsarPackOptions? options = null)
    {
        options ??= new AsarPackOptions();
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"{source} is not a directory.");
        var archive = Path.GetFullPath(archivePath);
        var unpackedRoot = archive + ".unpacked";

        var paths = new List<string>();
        Crawl(source, "", options.IncludeHidden, paths);
        paths.Sort(StringComparer.Ordinal); // JavaScript's default sort: UTF-16 code units

        var root = new Node { Files = [] };
        var unpackedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var contentOffsets = new Dictionary<string, string>(StringComparer.Ordinal);
        var packed = new List<string>();
        var offset = 0L;
        foreach (var relative in paths)
        {
            var full = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
            var parentPath = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            var parentUnpacked = unpackedDirectories.Contains(parentPath);
            var info = new FileInfo(full);
            var node = NodeAt(root, relative);
            if (info.LinkTarget is not null)
            {
                var target = Path.GetRelativePath(source, Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(full)!));
                if (Path.IsPathRooted(target) || target == ".." || target.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new IOException($"{full}: link \"{info.LinkTarget}\" points out of the package.");
                node.Unpacked = parentUnpacked || options.Unpack?.Invoke(relative) == true;
                node.Link = target.Replace(Path.DirectorySeparatorChar, '/');
                if (node.Unpacked)
                {
                    var copy = Path.Combine(unpackedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    if (File.Exists(copy) || Directory.Exists(copy)) File.Delete(copy);
                    File.CreateSymbolicLink(copy, info.LinkTarget);
                }
            }
            else if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                if (parentUnpacked || options.UnpackDirectory?.Invoke(relative) == true)
                {
                    unpackedDirectories.Add(relative);
                    node.Unpacked = true;
                }
                node.Files ??= [];
            }
            else
            {
                if (info.Length > uint.MaxValue) throw new IOException($"{full}: file size can not be larger than 4.2GB");
                node.Size = info.Length;
                node.Integrity = Hash(full);
                if (parentUnpacked || options.Unpack?.Invoke(relative) == true)
                {
                    node.Unpacked = true;
                    var copy = Path.Combine(unpackedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(full, copy, overwrite: true);
                }
                else
                {
                    node.Executable = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(full) & UnixFileMode.UserExecute) != 0;
                    if (contentOffsets.TryGetValue(node.Integrity.Hash, out var shared)) node.Offset = shared;
                    else
                    {
                        node.Offset = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        contentOffsets[node.Integrity.Hash] = node.Offset;
                        offset += info.Length;
                        packed.Add(full);
                    }
                }
            }
        }

        var json = new StringBuilder("{\"files\":");
        WriteFiles(json, root);
        json.Append('}');
        var header = Encoding.UTF8.GetBytes(json.ToString());
        var aligned = (header.Length + 3) & ~3;
        var headerPickle = new byte[8 + aligned];
        BinaryPrimitives.WriteUInt32LittleEndian(headerPickle, (uint)(4 + aligned));
        BinaryPrimitives.WriteInt32LittleEndian(headerPickle.AsSpan(4), header.Length);
        header.CopyTo(headerPickle, 8);
        var sizePickle = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(sizePickle, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(sizePickle.AsSpan(4), (uint)headerPickle.Length);

        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write(sizePickle);
        output.Write(headerPickle);
        foreach (var file in packed)
        {
            using var input = File.OpenRead(file);
            input.CopyTo(output);
        }
    }

    private static void Crawl(string directory, string prefix, bool includeHidden, List<string> paths)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (!includeHidden && entry.Name.StartsWith('.')) continue;
            var relative = prefix + entry.Name;
            paths.Add(relative);
            // A linked directory is packed as a link, not walked (glob's follow: false).
            if (entry is DirectoryInfo && entry.LinkTarget is null) Crawl(entry.FullName, relative + "/", includeHidden, paths);
        }
    }

    private static Node NodeAt(Node root, string relative)
    {
        var node = root;
        foreach (var name in relative.Split('/'))
        {
            node.Files ??= [];
            var child = node.Files.Find(f => f.Name == name).Node;
            if (child is null)
            {
                child = new Node();
                node.Files.Add((name, child));
            }
            node = child;
        }
        return node;
    }

    private static AsarIntegrity Hash(string file)
    {
        using var input = File.OpenRead(file);
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var blocks = new List<string>();
        var buffer = new byte[BlockSize];
        while (true)
        {
            var read = input.ReadAtLeast(buffer, BlockSize, throwOnEndOfStream: false);
            if (read == 0 && blocks.Count > 0) break;
            whole.AppendData(buffer, 0, read);
            blocks.Add(Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, read))));
            if (read < BlockSize) break;
        }
        return new AsarIntegrity("SHA256", Convert.ToHexStringLower(whole.GetHashAndReset()), BlockSize, blocks);
    }

    // Keys in @electron/asar's insertion order: a directory's unpacked then files; a file's size, then unpacked or
    // offset and executable, then integrity; a link's unpacked then link.
    private static void WriteNode(StringBuilder json, Node node)
    {
        json.Append('{');
        if (node.Files is not null)
        {
            if (node.Unpacked) json.Append("\"unpacked\":true,");
            json.Append("\"files\":");
            WriteFiles(json, node);
        }
        else if (node.Link is not null)
        {
            if (node.Unpacked) json.Append("\"unpacked\":true,");
            json.Append("\"link\":").Append(Quote(node.Link));
        }
        else
        {
            json.Append("\"size\":").Append(node.Size);
            if (node.Unpacked) json.Append(",\"unpacked\":true");
            else
            {
                json.Append(",\"offset\":").Append(Quote(node.Offset!));
                if (node.Executable) json.Append(",\"executable\":true");
            }
            var integrity = node.Integrity!;
            json.Append(",\"integrity\":{\"algorithm\":").Append(Quote(integrity.Algorithm))
                .Append(",\"hash\":").Append(Quote(integrity.Hash))
                .Append(",\"blockSize\":").Append(integrity.BlockSize)
                .Append(",\"blocks\":[").AppendJoin(',', integrity.Blocks.Select(Quote)).Append("]}");
        }
        json.Append('}');
    }

    private static void WriteFiles(StringBuilder json, Node directory)
    {
        json.Append('{');
        for (var i = 0; i < directory.Files!.Count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append(Quote(directory.Files[i].Name)).Append(':');
            WriteNode(json, directory.Files[i].Node);
        }
        json.Append('}');
    }

    /// <summary>A JSON string as JavaScript's <c>JSON.stringify</c> writes it: only quotes, backslashes, control
    /// characters and lone surrogates escaped, everything else as is.</summary>
    internal static string Quote(string value)
    {
        var json = new StringBuilder(value.Length + 2).Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        json.Append(c).Append(value[++i]);
                    }
                    else if (c < ' ' || char.IsSurrogate(c))
                    {
                        json.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else json.Append(c);
                    break;
            }
        }
        return json.Append('"').ToString();
    }

    private sealed class Node
    {
        public List<(string Name, Node Node)>? Files;
        public bool Unpacked;
        public string? Link;
        public long Size;
        public string? Offset;
        public bool Executable;
        public AsarIntegrity? Integrity;
    }
}
