using Riftway.Asar;
using Riftway.Pack;

// riftway-pack pack <source> <archive> [--unpack <glob>]... [--unpack-dir <glob>]... [--exclude-hidden]
// @electron/asar's `asar pack` and its options. Errors are MSBuild's canonical format, so a publish shows them as such.
if (args.Length < 3 || args[0] != "pack")
{
    Console.Error.WriteLine("Riftway.Pack : error RWPACK1: usage: Riftway.Pack pack <source> <archive> [--unpack <glob>]... [--unpack-dir <glob>]... [--exclude-hidden] [--header-hash-file <file>]");
    return 2;
}

var unpack = new List<ArchiveGlob>();
var unpackDirectories = new List<ArchiveGlob>();
var includeHidden = true;
string? headerHashFile = null;
for (var i = 3; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--unpack" when i + 1 < args.Length: unpack.Add(new ArchiveGlob(args[++i])); break;
        case "--unpack-dir" when i + 1 < args.Length: unpackDirectories.Add(new ArchiveGlob(args[++i])); break;
        case "--exclude-hidden": includeHidden = false; break;
        case "--header-hash-file" when i + 1 < args.Length: headerHashFile = args[++i]; break;
        default:
            Console.Error.WriteLine($"Riftway.Pack : error RWPACK1: unknown or incomplete option \"{args[i]}\".");
            return 2;
    }
}

try
{
    AsarPacker.Pack(args[1], args[2], new AsarPackOptions
    {
        IncludeHidden = includeHidden,
        Unpack = unpack.Count == 0 ? null : path => unpack.Exists(g => g.MatchesFile(path)),
        UnpackDirectory = unpackDirectories.Count == 0 ? null : path => unpackDirectories.Exists(g => g.MatchesDirectory(path)),
    });
    using var archive = AsarArchive.Open(args[2]);
    var files = archive.EnumerateFiles().ToList();
    Console.WriteLine($"{archive.FilePath}: {files.Count(f => !f.Unpacked)} files packed, {files.Count(f => f.Unpacked)} unpacked, header {archive.HeaderHash}");
    // The SHA-256 of the header, which the app's exe carries for the ArchiveIntegrity fuse.
    if (headerHashFile is not null) File.WriteAllText(headerHashFile, archive.HeaderHash);
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"Riftway.Pack : error RWPACK2: {e.Message}");
    return 1;
}
