using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Riftway.Asar;

/// <summary>The <c>ArchiveIntegrity</c> fuse, Electron's <c>EnableEmbeddedAsarIntegrityValidation</c>: with it on, an
/// archive the app serves must be listed in the app's exe with the SHA-256 of its header, as Electron's Windows
/// <c>Integrity</c>/<c>ElectronAsar</c> resource lists it, and every block read must match its hash.</summary>
internal static partial class AppArchiveIntegrity
{
    private static readonly Lazy<Policy> s_current = new(Load);

    /// <summary>The fuse's state for this process.</summary>
    /// <param name="Enabled">Whether archives must be checked.</param>
    /// <param name="Hashes">Header hashes by the archive's full path.</param>
    /// <param name="Error">Why no archive can pass (no or malformed integrity resource), when enabled.</param>
    internal sealed record Policy(bool Enabled, IReadOnlyDictionary<string, string> Hashes, string? Error);

    internal static Policy Current => s_current.Value;

    private static Policy Load()
    {
        var enabled = Fuses.ArchiveIntegrity;
        var exe = Environment.ProcessPath;
        // Electron checks archives on macOS and Windows only; on Linux the fuse has no effect.
        if (!enabled || OperatingSystem.IsLinux() || exe is null) return new(false, new Dictionary<string, string>(), null);
        if (!OperatingSystem.IsWindows())
            return new(true, new Dictionary<string, string>(), "The ArchiveIntegrity fuse is on, but Riftway reads archive hashes only from a Windows exe so far (DRIFT A46).");
        return Parse(true, ReadWindowsResource(exe, "Integrity", "ElectronAsar"), Path.GetDirectoryName(exe)!);
    }

    /// <summary>The policy an integrity resource's JSON gives, its paths relative to <paramref name="baseDirectory"/>.</summary>
    internal static Policy Parse(bool enabled, string? json, string baseDirectory)
    {
        var hashes = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!enabled) return new(false, hashes, null);
        if (json is null) return new(true, hashes, "The ArchiveIntegrity fuse is on, but the app has no archive integrity (Integrity/ElectronAsar resource).");
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var alg = item.GetProperty("alg").GetString();
                if (!string.Equals(alg, "sha256", StringComparison.OrdinalIgnoreCase))
                    return new(true, hashes, $"The archive integrity names algorithm \"{alg}\"; only sha256 is supported.");
                var file = item.GetProperty("file").GetString() ?? "";
                hashes[Path.GetFullPath(Path.Combine(baseDirectory, file.Replace('\\', Path.DirectorySeparatorChar)))] =
                    (item.GetProperty("value").GetString() ?? "").ToLowerInvariant();
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(true, hashes, $"The app's archive integrity is malformed: {e.Message}");
        }
        return new(true, hashes, null);
    }

    /// <summary>Null when the archive may be served; otherwise why the app must stop.</summary>
    internal static string? Check(Policy policy, string archivePath, string headerHash)
    {
        if (!policy.Enabled) return null;
        if (policy.Error is not null) return policy.Error;
        if (!policy.Hashes.TryGetValue(Path.GetFullPath(archivePath), out var expected))
            return $"The ArchiveIntegrity fuse is on, but {archivePath} has no integrity in the app.";
        return string.Equals(expected, headerHash, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Integrity check failed for asar archive {archivePath} ({expected} vs {headerHash}).";
    }

    // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE: the exe's resources without running anything.
    private static string? ReadWindowsResource(string exe, string type, string name)
    {
        var module = LoadLibraryExW(exe, 0, 0x2 | 0x20);
        if (module == 0) return null;
        try
        {
            var resource = FindResourceW(module, name, type);
            if (resource == 0) return null;
            var size = SizeofResource(module, resource);
            var data = LockResource(LoadResource(module, resource));
            if (data == 0) return null;
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { FreeLibrary(module); }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint LoadLibraryExW(string path, nint file, uint flags);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool FreeLibrary(nint module);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindResourceW(nint module, string name, string type);
    [LibraryImport("kernel32.dll")] private static partial nint LoadResource(nint module, nint resource);
    [LibraryImport("kernel32.dll")] private static partial nint LockResource(nint data);
    [LibraryImport("kernel32.dll")] private static partial uint SizeofResource(nint module, nint resource);
}
