using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway;

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
                // POSIX Crashpad uses flock, which .NET's Lock (fcntl; unsupported on macOS) never meets.
                if (OperatingSystem.IsWindows()) file.Lock(0, 12);
                else if (flock((int)file.SafeFileHandle.DangerousGetHandle(), LockExclusive) != 0) throw new IOException("settings.dat is locked");
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
    /// <summary>Gives this process's unhandled native exceptions back to Crashpad (chrome_elf's <c>CrashForException</c>);
    /// false when there is nothing to give them to.</summary>
    /// <remarks>The bootstrap layout starts Crashpad before .NET, whose runtime then installs its own unhandled-exception
    /// filter; in the apphost layout Crashpad starts after the runtime and its filter is the last one set.</remarks>
    public static bool RestoreCrashpadExceptionFilter()
    {
        if (Export("CrashForException_ExportThunk") is not { } crash || Export("IsCrashReportingEnabledImpl") is not { } enabled) return false;
        if (((delegate* unmanaged[Cdecl]<byte>)enabled)() == 0) return false;
        _ = SetUnhandledExceptionFilter(crash);
        return true;
    }

    private const int LockExclusive = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    [DllImport("kernel32.dll")]
    private static extern nint SetUnhandledExceptionFilter(nint filter);

    /// <summary>macOS: drops .NET's EXC_BREAKPOINT port from the calling (UI) thread, so Chromium's CHECK and
    /// IMMEDIATE_CRASH traps reach Crashpad's task-level port.</summary>
    /// <remarks>.NET's PAL hooks breakpoints on every thread it knows for its debugger; a trap outside managed code is
    /// then dispatched back to the same instruction forever, and the browser hangs instead of crashing
    /// (PAL_MachExceptionMode=2 does the same for a whole process, but only when set before the runtime starts).</remarks>
    public static bool LeaveBreakpointsToCrashpad()
    {
        const uint breakpointMask = 1u << 6; // EXC_MASK_BREAKPOINT
        const int exceptionDefault = 1;
        // ARM_THREAD_STATE64 / x86_THREAD_STATE64: the kernel checks the flavour even for a null port.
        var flavor = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 6 : 4;
        var thread = mach_thread_self();
        var result = thread_set_exception_ports(thread, breakpointMask, 0, exceptionDefault, flavor);
        if (NativeLibrary.TryGetExport(NativeLibrary.Load("/usr/lib/libSystem.dylib"), "mach_task_self_", out var task))
            _ = mach_port_deallocate(*(uint*)task, thread);
        return result == 0;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint mach_thread_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int thread_set_exception_ports(uint thread, uint mask, uint port, int behavior, int flavor);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int mach_port_deallocate(uint task, uint name);
    private static nint? Export(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (s_elf == 0 && !NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "chrome_elf.dll"), out s_elf)) return null;
        return NativeLibrary.TryGetExport(s_elf, name, out var address) ? address : null;
    }
}
