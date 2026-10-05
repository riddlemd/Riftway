using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>CEF's crash key call and the Crashpad database exports of chrome_elf.dll (components/crash/core/app/crash_export_thunks.h).</summary>
internal static unsafe class CrashReporterNative
{
    /// <summary>crash_reporter::Report (components/crash/core/app/crashpad.h); time_t is 64-bit with MSVC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Report
    {
        public fixed byte LocalId[64];
        public long CaptureTime;
        public fixed byte RemoteId[64];
        public long UploadTime;
        public int State;
    }

    /// <summary>crash_reporter::ReportUploadState::Uploaded.</summary>
    internal const int Uploaded = 3;

    /// <summary>Sets <paramref name="key"/> in this process's crash metadata, or clears it when <paramref name="value"/> is null.</summary>
    /// <remarks>CEF ignores keys the cfg did not declare at startup.</remarks>
    public static void SetCrashKey(string key, string? value)
    {
        // libcef's cef_set_crash_key_value does nothing until cef_initialize, so on Windows go to chrome_elf directly.
        if (Export("SetCrashKeyValueImpl") is { } address)
        {
            var name = Encoding.UTF8.GetBytes(key);
            var text = Encoding.UTF8.GetBytes(value ?? "");
            fixed (byte* namePointer = name)
            fixed (byte* textPointer = text)
                ((delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, int>)address)(namePointer, (nuint)name.Length, textPointer, (nuint)text.Length);
            return;
        }

        cef_string_utf16_t keyString = default;
        cef_string_utf16_t valueString = default;
        CefString.Set(&keyString, key);
        if (value is not null) CefString.Set(&valueString, value);
        CefNative.cef_set_crash_key_value(&keyString, value is null ? null : &valueString);
        CefString.Clear(&keyString);
        CefString.Clear(&valueString);
    }
    /// <summary>The database's reports as Electron lists them: newest capture first.</summary>
    public static List<CrashReport> GetReports()
    {
        var reports = new List<CrashReport>();
        if (Export("GetCrashReports_ExportThunk") is not { } address) return reports;
        var getReports = (delegate* unmanaged[Cdecl]<Report*, nuint, nuint>)address;
        var count = (int)getReports(null, 0);
        if (count == 0) return reports;
        var buffer = new Report[count];
        fixed (Report* first = buffer) count = Math.Min(count, (int)getReports(first, (nuint)buffer.Length));
        for (var i = 0; i < count; i++) reports.Add(ToCrashReport(buffer[i]));
        return reports;
    }

    /// <summary>Electron's shape for one report: the upload time and the server's id, or the epoch and "" when not uploaded.</summary>
    internal static CrashReport ToCrashReport(in Report report)
    {
        fixed (byte* remote = report.RemoteId)
        {
            var id = report.State == Uploaded ? Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(remote)) : "";
            var date = report.State == Uploaded ? DateTimeOffset.FromUnixTimeSeconds(report.UploadTime) : DateTimeOffset.UnixEpoch;
            return new CrashReport(date, id);
        }
    }

    /// <summary>crashpad::Settings::Data's magic, <c>'CPds'</c> (crashpad/client/settings.cc).</summary>
    internal const uint SettingsMagic = 0x43506473;

    /// <summary>Crashpad's settings.dat version; CEF's patch makes it 2 by appending backoff fields after the options word.</summary>
    internal const uint MaxSettingsVersion = 2;

    /// <summary>Settings::Data::Options::kUploadsEnabled.</summary>
    internal const uint UploadsEnabledOption = 1;


    /// <summary>The kUploadsEnabled bit of a Crashpad settings.dat; null when the file is missing or not that format.</summary>
    internal static bool? ReadUploadsEnabled(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[12];
            file.ReadExactly(header);
            if (!IsSettingsHeader(header)) return null;
            return (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) & UploadsEnabledOption) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Sets or clears the kUploadsEnabled bit in place, keeping the client id and last upload time; the handler
    /// reads it before each upload. Not chrome_elf's SetUploadConsent export: CEF enforces consent by policy, so that always enables.</summary>
    /// <returns>Whether the bit now has that value; an unchanged bit is not rewritten.</returns>
    internal static bool WriteUploadsEnabled(string path, bool enabled)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> header = stackalloc byte[12];
                file.ReadExactly(header);
                if (!IsSettingsHeader(header)) return false;
                // Crashpad locks the whole file while it reads or writes settings; wait our turn rather than tear its write.
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) file.Lock(0, 12);
                var options = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
                if (((options & UploadsEnabledOption) != 0) == enabled) return true;
                options = enabled ? options | UploadsEnabledOption : options & ~UploadsEnabledOption;
                BinaryPrimitives.WriteUInt32LittleEndian(header[8..], options);
                file.Position = 8;
                file.Write(header[8..]);
                file.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception e) when (e is IOException and not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(20);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsSettingsHeader(ReadOnlySpan<byte> header)
        => BinaryPrimitives.ReadUInt32LittleEndian(header) == SettingsMagic
            && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) is >= 1 and <= MaxSettingsVersion;

    /// <summary>settings.dat in this process's Crashpad database, or null before chrome_elf has opened one.</summary>
    internal static string? SettingsPath()
        => Export("GetCrashpadDatabasePath_ExportThunk") is { } address && ((delegate* unmanaged[Cdecl]<char*>)address)() is var database
            && database != null ? Path.Combine(new string(database), "settings.dat") : null;

    /// <summary>Passes the global annotations to a child process; see <see cref="CrashReporter.ChildSwitch"/>.</summary>
    public static void OnChildProcessLaunch(cef_command_line_t* commandLine)
    {
        cef_string_utf16_t name = default;
        CefString.Set(&name, "type");
        var processType = CefString.ReadUserFree(commandLine->get_switch_value(commandLine, &name));
        CefString.Clear(&name);
        if (CrashReporter.ChildSwitchValue(processType) is not { Length: > 0 } value) return;

        cef_string_utf16_t key = default;
        cef_string_utf16_t text = default;
        CefString.Set(&key, CrashReporter.ChildSwitch);
        CefString.Set(&text, value);
        commandLine->append_switch_with_value(commandLine, &key, &text);
        CefString.Clear(&key);
        CefString.Clear(&text);
    }


    private static nint s_elf;

    // Only Windows builds chrome_elf; libcef has already loaded it, so this finds the same module.
    private static nint? Export(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (s_elf == 0 && !NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "chrome_elf.dll"), out s_elf)) return null;
        return NativeLibrary.TryGetExport(s_elf, name, out var address) ? address : null;
    }
}
