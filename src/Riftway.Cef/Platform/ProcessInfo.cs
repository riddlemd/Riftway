using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The OS's answers behind Electron's <c>process</c> methods, as Chromium's base library reads them.</summary>
public static unsafe class ProcessInfo
{
    /// <summary>System memory in bytes, by Chromium's <c>base::SystemMemoryInfo</c> fields; null where the OS has none.</summary>
    public sealed record SystemMemory(ulong Total, ulong Free, ulong? Available, ulong? SwapTotal, ulong? SwapFree, ulong? FileBacked, ulong? Purgeable);

    /// <summary>[NSProcessInfo operatingSystemVersion] as major.minor.patch.</summary>
    [SupportedOSPlatform("macos")]
    public static string MacOperatingSystemVersion()
    {
        var processInfo = Send(Class("NSProcessInfo"u8), Sel("processInfo"u8));
        NSOperatingSystemVersion version;
        // Three NSIntegers: x64 returns them through a hidden pointer (objc_msgSend_stret), arm64 through x8 as usual.
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            ((delegate* unmanaged<NSOperatingSystemVersion*, nint, nint, void>)s_msgSendStret)(&version, processInfo, Sel("operatingSystemVersion"u8));
        else
            version = ((delegate* unmanaged<nint, nint, NSOperatingSystemVersion>)s_msgSend)(processInfo, Sel("operatingSystemVersion"u8));
        return $"{version.Major}.{version.Minor}.{version.Patch}";
    }

    /// <summary>uname's release, e.g. <c>6.8.0-45-generic</c>.</summary>
    [SupportedOSPlatform("linux")]
    public static string LinuxKernelRelease()
    {
        // glibc's utsname: six 65-byte fields; release is the third.
        var buffer = stackalloc byte[65 * 6];
        if (uname(buffer) != 0) return "";
        return Marshal.PtrToStringUTF8((nint)(buffer + 65 * 2)) ?? "";
    }

    /// <summary>Chromium's mac <c>GetSystemMemoryInfo</c>: free less speculative pages, file-backed and purgeable pages.</summary>
    [SupportedOSPlatform("macos")]
    public static SystemMemory? MacSystemMemory()
    {
        ulong total = 0;
        var size = (nuint)sizeof(ulong);
        fixed (byte* name = "hw.memsize"u8) if (sysctlbyname(name, &total, &size, null, 0) != 0) return null;
        var host = mach_host_self();
        nuint page;
        if (host_page_size(host, &page) != 0) return null;
        var info = stackalloc byte[HostVmInfo64Count * 4];
        var count = (uint)HostVmInfo64Count;
        if (host_statistics64(host, HostVmInfo64, info, &count) != 0) return null;
        // vm_statistics64: offsets of free_count, purgeable_count, speculative_count and external_page_count.
        ulong Pages(int offset) => *(uint*)(info + offset);
        var free = Pages(0) >= Pages(92) ? (Pages(0) - Pages(92)) * page : 0;
        return new SystemMemory(total, free, null, null, null, Pages(136) * page, Pages(88) * page);
    }

    /// <summary>Chromium's Windows <c>GetSystemMemoryInfo</c>, from GlobalMemoryStatusEx; Electron's free is the available
    /// physical memory.</summary>
    [SupportedOSPlatform("windows")]
    public static SystemMemory? WindowsSystemMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        if (GlobalMemoryStatusEx(&status) == 0) return null;
        return new SystemMemory(status.TotalPhys, status.AvailPhys, null, status.TotalPageFile, status.AvailPageFile, null, null);
    }

    /// <summary>This process's working set and private commit in bytes; Chromium's <c>ProcessMetrics::GetMemoryInfo</c> on
    /// Windows, which feeds Electron's <c>getProcessMemoryInfo</c>. Null if the OS refuses.</summary>
    [SupportedOSPlatform("windows")]
    public static (ulong WorkingSet, ulong Private)? WindowsProcessMemory()
    {
        var counters = new ProcessMemoryCountersEx { Size = (uint)sizeof(ProcessMemoryCountersEx) };
        if (K32GetProcessMemoryInfo(-1, &counters, counters.Size) == 0) return null;
        return (counters.WorkingSetSize, counters.PrivateUsage);
    }

    /// <summary>A Windows process's times, memory and integrity level, as <c>ProcessMetric</c> reads them.</summary>
    /// <param name="CpuTime">Kernel plus user time.</param>
    /// <param name="CreationFileTime">When it started, as a FILETIME (100 ns since 1601).</param>
    /// <param name="IntegrityRid">The last sub-authority of its token's integrity SID, or null if the token is unreadable.</param>
    public sealed record WindowsProcessDetails(TimeSpan CpuTime, ulong CreationFileTime, ulong WorkingSet, ulong PeakWorkingSet, ulong PrivateBytes, uint? IntegrityRid);

    /// <summary>Every process's id and parent id, from a Toolhelp snapshot; empty if the snapshot fails.</summary>
    [SupportedOSPlatform("windows")]
    public static Dictionary<int, int> WindowsParents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) return parents;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
            for (var ok = Process32FirstW(snapshot, &entry) != 0; ok; ok = Process32NextW(snapshot, &entry) != 0)
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        return parents;
    }

    /// <summary>A process's command line, from the kernel (class 60, Windows 10+); null if it cannot be read.</summary>
    [SupportedOSPlatform("windows")]
    public static string? WindowsCommandLine(int pid)
    {
        var process = OpenProcess(QueryLimitedInformation, 0, (uint)pid);
        if (process == 0) return null;
        try
        {
            var buffer = new byte[8192];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                uint needed;
                int status;
                fixed (byte* p = buffer) status = NtQueryInformationProcess(process, 60, p, (uint)buffer.Length, &needed);
                if (status >= 0)
                {
                    // UNICODE_STRING: Length (bytes), MaximumLength, then the pointer into this same buffer.
                    fixed (byte* p = buffer) return new string(*(char**)(p + 8), 0, *(ushort*)p / 2);
                }
                // STATUS_BUFFER_OVERFLOW, STATUS_BUFFER_TOO_SMALL, STATUS_INFO_LENGTH_MISMATCH: retry with the size it asked for.
                if (status is not (unchecked((int)0x80000005) or unchecked((int)0xC0000023) or unchecked((int)0xC0000004))) return null;
                buffer = new byte[Math.Max(needed, (uint)buffer.Length * 2)];
            }
            return null;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>A process's times, memory counters and token integrity level; null if it cannot be opened or its times read.
    /// The integrity level alone is null if the token is refused.</summary>
    [SupportedOSPlatform("windows")]
    public static WindowsProcessDetails? WindowsDetails(int pid)
    {
        var process = OpenProcess(QueryLimitedInformation, 0, (uint)pid);
        if (process == 0) return null;
        try
        {
            ulong created, exited, kernel, user;
            if (GetProcessTimes(process, &created, &exited, &kernel, &user) == 0) return null;
            var counters = new ProcessMemoryCountersEx { Size = (uint)sizeof(ProcessMemoryCountersEx) };
            if (K32GetProcessMemoryInfo(process, &counters, counters.Size) == 0) return null;
            return new WindowsProcessDetails(TimeSpan.FromTicks((long)(kernel + user)), created, counters.WorkingSetSize, counters.PeakWorkingSetSize, counters.PrivateUsage, IntegrityRid(process));
        }
        finally { CloseHandle(process); }
    }

    // Electron's ProcessMetric::GetIntegrityLevel: TokenIntegrityLevel's SID, whose last sub-authority is the level.
    [SupportedOSPlatform("windows")]
    private static uint? IntegrityRid(nint process)
    {
        nint token;
        if (OpenProcessToken(process, 8, &token) == 0) return null;
        try
        {
            uint length;
            GetTokenInformation(token, 25, null, 0, &length);
            if (length == 0 || length > 4096) return null;
            var label = stackalloc byte[(int)length];
            if (GetTokenInformation(token, 25, label, length, &length) == 0) return null;
            // TOKEN_MANDATORY_LABEL begins with the SID pointer.
            var sid = *(void**)label;
            return *GetSidSubAuthority(sid, (uint)(*GetSidSubAuthorityCount(sid) - 1));
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Whether the process runs with package identity (MSIX / Desktop Bridge); Electron's
    /// <c>IsRunningInDesktopBridge</c>.</summary>
    [SupportedOSPlatform("windows")]
    public static bool WindowsIsPackaged()
    {
        // Electron's buffer: PACKAGE_FAMILY_NAME_MAX_LENGTH wide chars; only success counts.
        var length = 64u;
        var name = stackalloc char[65];
        return GetPackageFamilyName(-1, &length, name) == 0;
    }

    /// <summary>This process's physical footprint, from TASK_VM_INFO, as Chromium's mac ProcessMetrics::GetMemoryInfo.</summary>
    [SupportedOSPlatform("macos")]
    public static ulong? MacPhysicalFootprint()
    {
        // task_vm_info: phys_footprint is the 64-bit field at byte 144; the kernel fills as much as count asks for.
        var info = stackalloc ulong[64];
        var count = 128u;
        if (task_info(mach_task_self(), 22, info, &count) != 0 || count < 38) return null;
        return info[144 / 8];
    }

    /// <summary>The task's interrupt wakeups, which Chromium's mac <c>GetIdleWakeupsPerSecond</c> counts.</summary>
    [SupportedOSPlatform("macos")]
    public static ulong? MacInterruptWakeups()
    {
        // task_power_info: total_user, total_system, task_interrupt_wakeups, ... six 64-bit fields.
        var info = stackalloc ulong[6];
        var count = (uint)TaskPowerInfoCount;
        if (task_info(mach_task_self(), TaskPowerInfo, info, &count) != 0) return null;
        return info[2];
    }

    /// <summary>Raises the soft limit on open files to <paramref name="max"/> or the hard limit, whichever is lower, if it
    /// is below that already; Chromium's <c>IncreaseFdLimitTo</c>.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static void IncreaseFdLimitTo(ulong max)
    {
        var resource = OperatingSystem.IsMacOS() ? 8 : 7;
        RLimit limit;
        if (getrlimit(resource, &limit) != 0 || limit.Current >= max) return;
        limit.Current = Math.Min(max, limit.Maximum);
        _ = setrlimit(resource, &limit);
    }

    /// <summary>The soft and hard limits on open files.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static (ulong Soft, ulong Hard) FdLimit()
    {
        RLimit limit;
        return getrlimit(OperatingSystem.IsMacOS() ? 8 : 7, &limit) == 0 ? (limit.Current, limit.Maximum) : (0, 0);
    }

    /// <summary>A process as libproc describes it, for app.getAppMetrics on macOS.</summary>
    public sealed record MacProcess(int Pid, int ParentPid, double CreationTime, TimeSpan Cpu, ulong ResidentBytes, ulong InterruptWakeups, bool Sandboxed);

    /// <summary>This process's resident size and its maximum, from MACH_TASK_BASIC_INFO, as Electron reads its own.</summary>
    [SupportedOSPlatform("macos")]
    public static (ulong Resident, ulong ResidentMax)? MacSelfMemory()
    {
        // mach_task_basic_info: virtual size, resident size, resident size max, then times; twelve 32-bit words.
        var info = stackalloc ulong[6];
        var count = 12u;
        if (task_info(mach_task_self(), 20, info, &count) != 0) return null;
        return (info[1], info[2]);
    }

    /// <summary>Every process's pid and parent, from libproc.</summary>
    [SupportedOSPlatform("macos")]
    public static IReadOnlyList<(int Pid, int ParentPid)> MacProcessTree()
    {
        var count = proc_listallpids(null, 0);
        if (count <= 0) return [];
        var pids = new int[count + 64];
        fixed (int* buffer = pids) count = proc_listallpids(buffer, pids.Length * sizeof(int));
        var tree = new List<(int, int)>();
        for (var i = 0; i < count; i++)
            if (BsdInfo(pids[i]) is { } info) tree.Add((pids[i], info.ParentPid));
        return tree;
    }

    /// <summary>One process's metrics; null once it is gone.</summary>
    [SupportedOSPlatform("macos")]
    public static MacProcess? MacProcessInfo(int pid)
    {
        if (BsdInfo(pid) is not { } bsd) return null;
        // proc_taskinfo: resident size at 8, total user and system time at 16 and 24, in Mach absolute time units.
        var task = stackalloc byte[96];
        if (proc_pidinfo(pid, 4, 0, task, 96) != 96) return null;
        var timebase = new MachTimebase();
        mach_timebase_info(&timebase);
        var ticks = *(ulong*)(task + 16) + *(ulong*)(task + 24);
        var cpu = TimeSpan.FromTicks((long)(ticks * timebase.Numer / timebase.Denom / 100));
        // rusage_info_v4: interrupt wakeups at 40, as Chromium counts task_interrupt_wakeups.
        var usage = stackalloc byte[512];
        var hasUsage = proc_pid_rusage(pid, 4, usage) == 0;
        return new MacProcess(pid, bsd.ParentPid, bsd.StartSeconds * 1000 + bsd.StartMicroseconds / 1000.0, cpu, *(ulong*)(task + 8),
            hasUsage ? *(ulong*)(usage + 40) : 0, sandbox_check(pid, null, 0) != 0);
    }

    /// <summary>A process's arguments, from KERN_PROCARGS2: argc, the executable path, then argv.</summary>
    [SupportedOSPlatform("macos")]
    public static IReadOnlyList<string> MacProcessArguments(int pid)
    {
        var mib = stackalloc int[] { 1, 49, pid };
        nuint size = 0;
        if (sysctl(mib, 3, null, &size, null, 0) != 0 || size < 4) return [];
        var buffer = new byte[size];
        fixed (byte* p = buffer) if (sysctl(mib, 3, p, &size, null, 0) != 0) return [];
        return ParseProcArgs(buffer.AsSpan(0, (int)size));
    }

    // KERN_PROCARGS2's layout: argc, the executable path, NUL padding, then argc NUL-terminated arguments.
    internal static IReadOnlyList<string> ParseProcArgs(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 4) return [];
        var argc = BitConverter.ToInt32(buffer);
        var at = 4;
        while (at < buffer.Length && buffer[at] != 0) at++;
        while (at < buffer.Length && buffer[at] == 0) at++;
        var arguments = new List<string>();
        while (arguments.Count < argc && at < buffer.Length)
        {
            var end = buffer[at..].IndexOf((byte)0);
            end = end < 0 ? buffer.Length : at + end;
            arguments.Add(System.Text.Encoding.UTF8.GetString(buffer[at..end]));
            at = end + 1;
        }
        return arguments;
    }

    // proc_bsdinfo: ppid at 16, start seconds and microseconds at 120 and 128.
    [SupportedOSPlatform("macos")]
    private static (int ParentPid, ulong StartSeconds, ulong StartMicroseconds)? BsdInfo(int pid)
    {
        var info = stackalloc byte[136];
        if (proc_pidinfo(pid, 3, 0, info, 136) != 136) return null;
        return (*(int*)(info + 16), *(ulong*)(info + 120), *(ulong*)(info + 128));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebase
    {
        public uint Numer, Denom;
    }

    private const int HostVmInfo64 = 4;
    private const int HostVmInfo64Count = 38;
    private const int TaskPowerInfo = 21;
    private const int TaskPowerInfoCount = 12;
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private static readonly nint s_msgSend = OperatingSystem.IsMacOS() ? NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend") : 0;
    private static readonly nint s_msgSendStret = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend_stret") : 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSOperatingSystemVersion
    {
        public nint Major, Minor, Patch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current, Maximum;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private const uint QueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(LibSystem)] private static extern uint mach_host_self();
    [DllImport(LibSystem)] private static extern uint mach_task_self();
    [DllImport(LibSystem)] private static extern int host_page_size(uint host, nuint* pageSize);
    [DllImport(LibSystem)] private static extern int host_statistics64(uint host, int flavor, byte* info, uint* count);
    [DllImport(LibSystem)] private static extern int task_info(uint task, int flavor, ulong* info, uint* count);
    [DllImport(LibSystem)] private static extern int proc_listallpids(int* buffer, int bufferSize);
    [DllImport(LibSystem)] private static extern int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int bufferSize);
    [DllImport(LibSystem)] private static extern int proc_pid_rusage(int pid, int flavor, void* buffer);
    [DllImport(LibSystem)] private static extern int mach_timebase_info(MachTimebase* info);
    [DllImport(LibSystem)] private static extern int sysctl(int* name, uint length, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);
    [DllImport(LibSystem)] private static extern int sandbox_check(int pid, byte* operation, int type);
    [DllImport(LibSystem)] private static extern int sysctlbyname(byte* name, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);
    [DllImport("libc", EntryPoint = "uname")] private static extern int uname(byte* buffer);
    [DllImport("libc", EntryPoint = "getrlimit")] private static extern int getrlimit(int resource, RLimit* limit);
    [DllImport("libc", EntryPoint = "setrlimit")] private static extern int setrlimit(int resource, RLimit* limit);
    [DllImport("kernel32")] private static extern int GlobalMemoryStatusEx(MemoryStatusEx* status);
    // -1 is the GetCurrentProcess pseudo-handle.
    [DllImport("kernel32")] private static extern int K32GetProcessMemoryInfo(nint process, ProcessMemoryCountersEx* counters, uint size);
    [DllImport("kernel32")] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32")] private static extern int Process32FirstW(nint snapshot, ProcessEntry32* entry);
    [DllImport("kernel32")] private static extern int Process32NextW(nint snapshot, ProcessEntry32* entry);
    [DllImport("kernel32")] private static extern int CloseHandle(nint handle);
    [DllImport("kernel32")] private static extern nint OpenProcess(uint access, int inherit, uint pid);
    [DllImport("kernel32")] private static extern int GetProcessTimes(nint process, ulong* created, ulong* exited, ulong* kernel, ulong* user);
    [DllImport("advapi32")] private static extern int OpenProcessToken(nint process, uint access, nint* token);
    [DllImport("advapi32")] private static extern int GetTokenInformation(nint token, int information, void* buffer, uint length, uint* returned);
    [DllImport("advapi32")] private static extern byte* GetSidSubAuthorityCount(void* sid);
    [DllImport("advapi32")] private static extern uint* GetSidSubAuthority(void* sid, uint index);
    [DllImport("ntdll")] private static extern int NtQueryInformationProcess(nint process, int information, void* buffer, uint length, uint* returned);
    [DllImport("kernel32")] private static extern int GetPackageFamilyName(nint process, uint* length, char* name);
}
