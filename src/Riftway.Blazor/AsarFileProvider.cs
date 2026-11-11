using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Riftway.Asar;

namespace Riftway.Blazor;

/// <summary>Static files served from an ASAR archive, as a packed Electron app's pages are: what Blazor's
/// <c>wwwroot</c> becomes once packed. Unpacked files are read from <c>&lt;archive&gt;.unpacked</c>.</summary>
/// <remarks>The archive is read in place and never changes while open, so <see cref="Watch"/> never fires.</remarks>
public sealed class AsarFileProvider : IFileProvider, IDisposable
{
    private readonly AsarArchive _archive;
    private readonly bool _verifyIntegrity;
    private readonly DateTimeOffset _lastModified;

    /// <summary>Opens the archive.</summary>
    /// <param name="archivePath">The <c>.asar</c> file.</param>
    /// <param name="verifyIntegrity">Check each block read against its SHA-256 in the header; a mismatch fails the read
    /// with <see cref="InvalidDataException"/>, as Electron's integrity check refuses it.</param>
    public AsarFileProvider(string archivePath, bool verifyIntegrity = false)
    {
        _archive = AsarArchive.Open(archivePath);
        _verifyIntegrity = verifyIntegrity;
        _lastModified = File.GetLastWriteTimeUtc(_archive.FilePath);
    }

    /// <summary>The archive served.</summary>
    public AsarArchive Archive => _archive;

    /// <inheritdoc />
    public IFileInfo GetFileInfo(string subpath)
    {
        var entry = _archive.GetEntry(subpath);
        return entry is { Kind: AsarEntryKind.File } ? new EntryInfo(this, entry) : new NotFoundFileInfo(subpath);
    }

    /// <inheritdoc />
    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        var entry = _archive.GetEntry(subpath);
        return entry is { Kind: AsarEntryKind.Directory } ? new Contents(this, entry) : NotFoundDirectoryContents.Singleton;
    }

    /// <inheritdoc />
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    /// <summary>Closes the archive.</summary>
    public void Dispose() => _archive.Dispose();

    private sealed class EntryInfo(AsarFileProvider owner, AsarEntry entry, string? name = null) : IFileInfo
    {
        public bool Exists => true;
        public bool IsDirectory => entry.Kind == AsarEntryKind.Directory;
        public long Length => IsDirectory ? -1 : entry.Unpacked && PhysicalPath is { } path && File.Exists(path) ? new FileInfo(path).Length : entry.Size;
        public string Name => name ?? entry.Name;
        public DateTimeOffset LastModified => owner._lastModified;

        public string? PhysicalPath => entry.Unpacked && !IsDirectory
            ? Path.Combine(owner._archive.FilePath + ".unpacked", entry.Path.Replace('/', Path.DirectorySeparatorChar))
            : null;

        public Stream CreateReadStream() => IsDirectory
            ? throw new InvalidOperationException($"\"{entry.Path}\" is a directory.")
            : owner._archive.OpenRead(entry.Path, owner._verifyIntegrity);
    }

    private sealed class Contents(AsarFileProvider owner, AsarEntry directory) : IDirectoryContents
    {
        public bool Exists => true;

        public IEnumerator<IFileInfo> GetEnumerator()
        {
            foreach (var child in directory.Children)
            {
                var target = child.Kind == AsarEntryKind.Link ? owner._archive.GetEntry(child.Path) : child;
                if (target is not null && target.Kind != AsarEntryKind.Link) yield return new EntryInfo(owner, target, child.Name);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
