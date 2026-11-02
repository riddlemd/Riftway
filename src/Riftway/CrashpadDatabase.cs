using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Riftway;

/// <summary>Reads a Crashpad database's reports on macOS and Linux, where no chrome_elf exports them, as Chromium's
/// <c>crash_reporter::GetReports</c> (components/crash/core/app/crashpad.cc) lists them.</summary>
internal static partial class CrashpadDatabase
{
    /// <summary>One report's metadata, as crashpad::CrashReportDatabase::Report holds it.</summary>
    internal readonly record struct Entry(long CreationTime, long LastUploadAttemptTime, bool Uploaded, string Id);

    /// <summary>crash_report_database_generic.cc's ReportMetadata::kVersion.</summary>
    internal const int GenericMetadataVersion = 1;

    /// <summary>sizeof(ReportMetadata) on 64-bit Linux: int32 version, int32 attempts, int64 last attempt, time_t creation, uint8 attributes, padding.</summary>
    internal const int GenericMetadataSize = 32;

    /// <summary>The database's completed and pending reports, newest capture first; empty when it cannot be read.</summary>
    public static List<CrashReport> Read(string? database)
    {
        var entries = new List<Entry>();
        if (database is null) return [];
        foreach (var state in new[] { "completed", "pending" })
        {
            var directory = Path.Combine(database, state);
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var dump in Directory.EnumerateFiles(directory, "*.dmp"))
                    if ((OperatingSystem.IsMacOS() ? ReadXattrs(dump, database) : ReadMeta(dump)) is { } entry)
                        entries.Add(state == "pending" ? entry with { Uploaded = false } : entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return ToReports(entries);
    }

    /// <summary>Chromium's order (capture time, newest first) and Electron's shape for each entry.</summary>
    internal static List<CrashReport> ToReports(IEnumerable<Entry> entries)
        => entries.OrderByDescending(e => e.CreationTime)
            .Select(e => e.Uploaded ? new CrashReport(DateTimeOffset.FromUnixTimeSeconds(e.LastUploadAttemptTime), e.Id) : new CrashReport(DateTimeOffset.UnixEpoch, ""))
            .ToList();

    /// <summary>Parses a Linux <c>&lt;uuid&gt;.meta</c>: the ReportMetadata struct, then the server's id to the end of the file.</summary>
    internal static Entry? ParseGenericMetadata(ReadOnlySpan<byte> meta)
    {
        if (meta.Length < GenericMetadataSize || BinaryPrimitives.ReadInt32LittleEndian(meta) != GenericMetadataVersion) return null;
        return new Entry(
            BinaryPrimitives.ReadInt64LittleEndian(meta[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(meta[8..]),
            (meta[24] & 1) != 0,
            Encoding.UTF8.GetString(meta[GenericMetadataSize..]));
    }

    /// <summary>Builds an entry from macOS xattr values (crash_report_database_mac.mm); null without uuid or creation time.</summary>
    internal static Entry? ParseXattrs(Func<string, string?> read)
    {
        if (read("uuid") is null || !long.TryParse(read("creation_time"), out var created)) return null;
        _ = long.TryParse(read("last_upload_time"), out var uploadTime);
        return new Entry(created, uploadTime, read("uploaded") == "1", read("id") ?? "");
    }

    /// <summary>The attribute prefix the database uses: new databases mark themselves initialized under the new names.</summary>
    internal static string XattrPrefix(Func<string, string?> readDatabase)
        => readDatabase("org.chromium.crashpad.database.initialized") == "1" ? "org.chromium.crashpad.database." : "com.googlecode.crashpad.";

    private static Entry? ReadMeta(string dump)
    {
        try { return ParseGenericMetadata(File.ReadAllBytes(Path.ChangeExtension(dump, ".meta"))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Entry? ReadXattrs(string dump, string database)
    {
        var prefix = XattrPrefix(name => GetXattr(database, name));
        return ParseXattrs(name => GetXattr(dump, prefix + name));
    }

    private static unsafe string? GetXattr(string path, string name)
    {
        var buffer = stackalloc byte[256];
        var length = getxattr(path, name, buffer, 256, 0, 0);
        return length < 0 ? null : Encoding.UTF8.GetString(buffer, (int)length);
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint getxattr(string path, string name, byte* value, nuint size, uint position, int options);
}
