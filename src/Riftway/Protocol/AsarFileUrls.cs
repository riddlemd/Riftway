using System.Collections.Concurrent;
using Riftway.Asar;

namespace Riftway;

/// <summary><c>file://</c> URLs into ASAR archives, as Electron's asar URL loader serves them: a path whose first
/// <c>.asar</c> component is an archive file is read from inside it (an unpacked entry from beside it). Anything else,
/// including a path into an archive that names no file there, is left to Chromium's own <c>file://</c> loader, which
/// fails such a path with <c>ERR_FILE_NOT_FOUND</c> as Electron does.</summary>
internal static class AsarFileUrls
{
    // Opened once and kept for the process, as Electron's archive cache; null for a file that is no archive.
    private static readonly ConcurrentDictionary<string, AsarArchive?> s_archives =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Electron's <c>ELECTRON_NO_ASAR</c> (or <c>RIFTWAY_NO_ASAR</c>): no archive support for <c>file://</c>.</summary>
    internal static bool IsDisabledBy(Func<string, string?> environment)
        => !string.IsNullOrEmpty(environment("ELECTRON_NO_ASAR")) || !string.IsNullOrEmpty(environment("RIFTWAY_NO_ASAR"));

    /// <summary>The archive and file a <c>file://</c> URL names; false when it runs through no archive or names no
    /// file inside one. Runs on CEF's IO thread: after an archive's first use this is lookups in memory.</summary>
    internal static bool TryResolve(string url, out AsarArchive archive, out AsarEntry entry)
    {
        archive = null!;
        entry = null!;
        if (url.IndexOf(".asar", StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;

        // Electron's GetAsarArchivePath: the first component ending in .asar that is a file.
        var path = uri.LocalPath;
        var separators = OperatingSystem.IsWindows() ? new[] { '\\', '/' } : ['/'];
        for (var end = path.IndexOfAny(separators, 1); ; end = path.IndexOfAny(separators, end + 1))
        {
            var prefix = end < 0 ? path : path[..end];
            if (prefix.EndsWith(".asar", StringComparison.Ordinal) && File.Exists(prefix))
            {
                var inner = end < 0 ? "" : path[(end + 1)..];
                if (inner.Length == 0 || Open(prefix) is not { } opened) return false;
                if (opened.GetEntry(inner) is not { Kind: AsarEntryKind.File } found) return false;
                archive = opened;
                entry = found;
                return true;
            }
            if (end < 0) return false;
        }
    }

    private static AsarArchive? Open(string path) => s_archives.GetOrAdd(Path.GetFullPath(path), full =>
    {
        try { return AsarArchive.OpenForApp(full); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    });

    /// <summary>Closes and forgets an archive (tests, which delete theirs).</summary>
    internal static void Forget(string path)
    {
        if (s_archives.TryRemove(Path.GetFullPath(path), out var archive)) archive?.Dispose();
    }

    /// <summary>The response for a URL <see cref="TryResolve"/> accepted: the file, typed by its extension.</summary>
    internal static Task<ProtocolResponse> Serve(ProtocolRequest request, CancellationToken cancellation)
    {
        if (!TryResolve(request.Url, out var archive, out var entry)) return Task.FromResult(ProtocolResponse.Status(404));
        return Task.FromResult(new ProtocolResponse
        {
            ContentType = ProtocolResponse.MimeTypeOf(entry.Name),
            Body = archive.OpenRead(entry.Path),
        });
    }
}
