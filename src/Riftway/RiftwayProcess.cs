using System.Diagnostics;
using Riftway.Cef.Platform;

// Each platform call sits behind an OperatingSystem check the analyzer follows; the samplers' callbacks it cannot.
#pragma warning disable CA1416

namespace Riftway;

/// <summary>The process's CPU use; Electron's <c>CPUUsage</c>.</summary>
/// <param name="PercentCPUUsage">Percent of all CPUs used since the previous call; 0 on the first.</param>
/// <param name="CumulativeCPUUsage">Seconds of CPU time used since the process started.</param>
/// <param name="IdleWakeupsPerSecond">Wakeups from idle per second since the previous call; 0 on the first, and always 0
/// on Windows, as Chromium.</param>
public sealed record CpuUsage(double PercentCPUUsage, double? CumulativeCPUUsage, int IdleWakeupsPerSecond);

/// <summary>The system's memory in kilobytes; Electron's <c>process.getSystemMemoryInfo()</c> result. A field is null on
/// the OSes Electron leaves it out of.</summary>
/// <param name="Total">Physical memory.</param>
/// <param name="Free">Memory not in use (Windows: available physical memory).</param>
/// <param name="Available">Linux's MemAvailable.</param>
/// <param name="SwapTotal">Swap (Windows: the page file), Windows and Linux.</param>
/// <param name="SwapFree">Free swap, Windows and Linux.</param>
/// <param name="FileBacked">File-backed pages, macOS.</param>
/// <param name="Purgeable">Purgeable pages, macOS.</param>
public sealed record SystemMemoryInfo(long Total, long Free, long? Available, long? SwapTotal, long? SwapFree, long? FileBacked, long? Purgeable);

/// <summary>This process's memory in kilobytes; Electron's <c>process.getProcessMemoryInfo()</c> result.</summary>
/// <param name="ResidentSet">The working set (Windows, Linux).</param>
/// <param name="Private">Memory only this process uses: private commit on Windows.</param>
/// <param name="Shared">Memory shared with other processes. Always 0 here: Chromium sums it from tracing's shared-memory graph,
/// which Riftway does not build.</param>
public sealed record ProcessMemoryInfo(long? ResidentSet, long Private, long Shared);

/// <summary>Versions of the parts of this process; Electron's <c>process.versions</c>.</summary>
/// <param name="Chrome">Chromium's version.</param>
/// <param name="Riftway">Riftway's version, where Electron has <c>electron</c>.</param>
public sealed record ProcessVersions(string Chrome, string Riftway);

/// <summary>The main process; Electron's <c>process</c> object, for what a .NET main process has.</summary>
public static class RiftwayProcess
{
    private static readonly CpuSampler s_cpu = new(() => Process.GetCurrentProcess().TotalProcessorTime, Stopwatch.GetElapsedTime);
    private static readonly EventRate s_wakeups = new(Stopwatch.GetElapsedTime);

    /// <summary>Always <c>browser</c>, Electron's name for the main process. Electron's <c>process.type</c>.</summary>
    public static string Type => "browser";

    /// <summary>Electron's <c>process.versions</c>.</summary>
    public static ProcessVersions Versions => new(RiftwayApp.ChromeVersion(), RiftwayApp.RiftwayVersion());

    /// <summary>Whether the process runs with package identity (MSIX / Desktop Bridge); Electron's Windows-only
    /// <c>process.windowsStore</c>, which Electron leaves undefined rather than false.</summary>
    public static bool WindowsStore { get; } = OperatingSystem.IsWindows() && ProcessInfo.WindowsIsPackaged();

    /// <summary>The app's resources directory, whether or not it exists: the bundle's <c>Contents/Resources</c> on macOS,
    /// else <c>resources</c> beside the executable. Electron's <c>process.resourcesPath</c>.</summary>
    public static string ResourcesPath { get; } = ResourcesPathFor(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "app"), OperatingSystem.IsMacOS());

    // Electron's GetResourcesPath: MainApplicationBundlePath (three levels above the executable) on macOS, DIR_ASSETS
    // (the executable's directory) elsewhere.
    internal static string ResourcesPathFor(string executable, bool macOS)
    {
        var directory = Path.GetDirectoryName(executable) ?? "";
        if (!macOS) return Path.Combine(directory, "resources");
        var bundle = Path.GetDirectoryName(Path.GetDirectoryName(directory)) ?? "";
        return Path.Combine(bundle, "Contents", "Resources");
    }

    /// <summary>Crashes the process at once with a segmentation fault, as Electron's <c>process.crash()</c>: no events,
    /// no managed exception handlers.</summary>
    public static void Crash()
    {
        // A fault inside native code: .NET turns a managed null write into a NullReferenceException instead.
        if (OperatingSystem.IsWindows()) WindowsMemset(0, 0, 1);
        else UnixMemset(0, 0, 1);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "memset")] private static extern nint UnixMemset(nint destination, int value, nuint count);
    [System.Runtime.InteropServices.DllImport("ucrtbase", EntryPoint = "memset")] private static extern nint WindowsMemset(nint destination, int value, nuint count);

    /// <summary>Hangs the UI thread, Electron's main thread, for ever: the app stops responding. Called from another
    /// thread, it returns while the UI thread hangs. Electron's <c>process.hang()</c>.</summary>
    public static void Hang()
    {
        if (RiftwayApp.CurrentOrNull is { IsReady: true }) Riftway.Cef.CefThread.RunOnUi(HangHere);
        else HangHere();
    }

    private static void HangHere()
    {
        for (;;) Thread.Sleep(1000);
    }

    /// <summary>When the process started, in milliseconds since the Unix epoch, or null if unknown. Electron's
    /// <c>process.getCreationTime()</c>.</summary>
    public static double? GetCreationTime()
    {
        try { return (Process.GetCurrentProcess().StartTime.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The process's CPU use since the previous call, shared by every caller as in Electron. Electron's
    /// <c>process.getCPUUsage()</c>.</summary>
    public static CpuUsage GetCPUUsage()
    {
        var (percent, cumulative) = s_cpu.Sample();
        var wakeups = OperatingSystem.IsLinux() ? LinuxVoluntarySwitches() : OperatingSystem.IsMacOS() ? ProcessInfo.MacInterruptWakeups() : null;
        return new CpuUsage(percent / Environment.ProcessorCount, cumulative, wakeups is { } count ? s_wakeups.PerSecond(count) : 0);
    }

    /// <summary>The system's memory, as Chromium reads it. Electron's <c>process.getSystemMemoryInfo()</c>.</summary>
    /// <exception cref="InvalidOperationException">The OS did not say, with Electron's message.</exception>
    public static SystemMemoryInfo GetSystemMemoryInfo()
    {
        var memory = OperatingSystem.IsLinux() ? ParseMeminfo(SafeRead("/proc/meminfo"))
            : OperatingSystem.IsMacOS() ? ProcessInfo.MacSystemMemory()
            : OperatingSystem.IsWindows() ? ProcessInfo.WindowsSystemMemory() : null;
        if (memory is null) throw new InvalidOperationException("Unable to retrieve system memory information");
        return ToSystemMemoryInfo(memory);
    }

    // Electron reports every field in whole KiB (ByteSize::InKiB truncates).
    internal static SystemMemoryInfo ToSystemMemoryInfo(ProcessInfo.SystemMemory memory)
    {
        static long Kib(ulong bytes) => (long)(bytes / 1024);
        static long? OptionalKib(ulong? bytes) => bytes is { } b ? Kib(b) : null;
        return new SystemMemoryInfo(Kib(memory.Total), Kib(memory.Free), OptionalKib(memory.Available), OptionalKib(memory.SwapTotal),
            OptionalKib(memory.SwapFree), OptionalKib(memory.FileBacked), OptionalKib(memory.Purgeable));
    }

    /// <summary>This process's memory as Chromium's memory instrumentation reports it. Electron's
    /// <c>process.getProcessMemoryInfo()</c>; Windows only so far.</summary>
    /// <exception cref="InvalidOperationException">Before the app is ready, or the OS refused, with Electron's messages.</exception>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public static Task<ProcessMemoryInfo> GetProcessMemoryInfoAsync()
    {
        if (!RiftwayApp.Current.IsReady) return Task.FromException<ProcessMemoryInfo>(new InvalidOperationException("Memory Info is available only after app ready"));
        var info = OperatingSystem.IsWindows() ? ProcessInfo.WindowsProcessMemory() is var (workingSet, privateBytes) ? ToProcessMemoryInfo(workingSet, privateBytes) : null
            : OperatingSystem.IsLinux() ? ParseProcStatus(SafeRead("/proc/self/status"), SafeRead("/proc/self/statm"), Environment.SystemPageSize) is { } status ? LinuxProcessMemoryInfo(status) : null
            : OperatingSystem.IsMacOS() ? ProcessInfo.MacPhysicalFootprint() is { } footprint ? MacProcessMemoryInfo(footprint) : null
            : null;
        return info is null ? Task.FromException<ProcessMemoryInfo>(new InvalidOperationException("Failed to create memory dump")) : Task.FromResult(info);
    }

    /// <summary>The OS's version: macOS's own (e.g. <c>26.0.1</c>), Windows' major.minor.build, Linux's kernel release.
    /// Electron's <c>process.getSystemVersion()</c>.</summary>
    public static string GetSystemVersion()
    {
        if (OperatingSystem.IsMacOS()) return ProcessInfo.MacOperatingSystemVersion();
        if (OperatingSystem.IsLinux()) return ProcessInfo.LinuxKernelRelease();
        var version = Environment.OSVersion.Version;
        return FormatWindowsVersion(version.Major, version.Minor, version.Build);
    }

    /// <summary>Raises the soft limit on open files to <paramref name="maxDescriptors"/> or the hard limit, whichever is
    /// lower (macOS, Linux); does nothing on Windows, where Electron has no such method. Electron's
    /// <c>process.setFdLimit()</c>.</summary>
    public static void SetFdLimit(int maxDescriptors)
    {
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && maxDescriptors > 0) ProcessInfo.IncreaseFdLimitTo((ulong)maxDescriptors);
    }

    // Chromium's SysInfo::OperatingSystemVersion on Windows: major.minor.build, without the UBR patch number.
    internal static string FormatWindowsVersion(int major, int minor, int build) => $"{major}.{minor}.{build}";

    // Chromium saturates the OS dump's kilobytes: the resident set to uint32, private footprint to int32.
    internal static ProcessMemoryInfo ToProcessMemoryInfo(ulong workingSetBytes, ulong privateBytes) =>
        new((long)Math.Min(workingSetBytes / 1024, uint.MaxValue), (long)Math.Min(privateBytes / 1024, int.MaxValue), 0);

    // Chromium's Linux OS dump: resident set from VmRSS; the private footprint is anonymous resident memory plus swap.
    internal static ProcessMemoryInfo LinuxProcessMemoryInfo((ulong Resident, ulong RssAnon, ulong Swap) status)
        => new((long)Math.Min(status.Resident / 1024, uint.MaxValue), (long)((status.RssAnon + status.Swap) / 1024), 0);

    // Chromium's mac OS dump: no resident set in Electron's answer, and the private footprint is the physical footprint
    // less shared memory, which is 0 without tracing's shared-memory graph (drift P8).
    internal static ProcessMemoryInfo MacProcessMemoryInfo(ulong physicalFootprint)
        => new(null, (long)Math.Min(physicalFootprint / 1024, int.MaxValue), 0);

    // VmRSS, RssAnon and VmSwap from /proc/<pid>/status, in bytes. Before Linux 4.5 there is no RssAnon, and Chromium
    // takes resident less shared pages from statm instead.
    internal static (ulong Resident, ulong RssAnon, ulong Swap)? ParseProcStatus(string status, string statm, int pageSize)
    {
        ulong? Field(string name)
        {
            foreach (var line in status.Split('\n'))
                if (line.StartsWith(name + ":", StringComparison.Ordinal)
                    && ulong.TryParse(line[(name.Length + 1)..].Trim().Split(' ')[0], out var kib)) return kib * 1024;
            return null;
        }
        if (Field("RssAnon") is { } anon and not 0) return (Field("VmRSS") ?? 0, anon, Field("VmSwap") ?? 0);
        var pages = statm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pages.Length < 3 || !ulong.TryParse(pages[1], out var resident) || !ulong.TryParse(pages[2], out var shared)) return null;
        return (Field("VmRSS") ?? 0, (resident - shared) * (ulong)pageSize, Field("VmSwap") ?? 0);
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
    }

    private static ulong? LinuxVoluntarySwitches()
    {
        foreach (var line in SafeRead("/proc/self/status").Split('\n'))
            if (line.StartsWith("voluntary_ctxt_switches:", StringComparison.Ordinal)
                && ulong.TryParse(line["voluntary_ctxt_switches:".Length..].Trim(), out var count)) return count;
        return null;
    }

    // Chromium's ParseProcMeminfo: "Name: value kB" lines in any order; null without a MemTotal.
    internal static ProcessInfo.SystemMemory? ParseMeminfo(string meminfo)
    {
        ulong total = 0, free = 0, available = 0, swapTotal = 0, swapFree = 0;
        foreach (var line in meminfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= 1 || !ulong.TryParse(tokens[1], out var kib)) continue;
            var bytes = kib * 1024;
            switch (tokens[0])
            {
                case "MemTotal:": total = bytes; break;
                case "MemFree:": free = bytes; break;
                case "MemAvailable:": available = bytes; break;
                case "SwapTotal:": swapTotal = bytes; break;
                case "SwapFree:": swapFree = bytes; break;
            }
        }
        return total == 0 ? null : new ProcessInfo.SystemMemory(total, free, available, swapTotal, swapFree, null, null);
    }

    // Chromium's ProcessMetrics::GetPlatformIndependentCPUUsage: percent of one CPU since the last call, 0 on the first.
    internal sealed class CpuSampler(Func<TimeSpan> cumulativeCpu, Func<long, TimeSpan> elapsedSince)
    {
        private readonly Lock _gate = new();
        private readonly long _start = Stopwatch.GetTimestamp();
        private TimeSpan _lastCpu;
        private TimeSpan _lastTime;

        public (double Percent, double Cumulative) Sample() => Sample(cumulativeCpu());

        public (double Percent, double Cumulative) Sample(TimeSpan cpu)
        {
            var time = elapsedSince(_start);
            lock (_gate)
            {
                if (_lastCpu == TimeSpan.Zero)
                {
                    _lastCpu = cpu;
                    _lastTime = time;
                    return (0, cpu.TotalSeconds);
                }
                var timeDelta = time - _lastTime;
                if (timeDelta == TimeSpan.Zero) return (0, cpu.TotalSeconds);
                var cpuDelta = cpu - _lastCpu;
                _lastCpu = cpu;
                _lastTime = time;
                return (100.0 * cpuDelta / timeDelta, cpu.TotalSeconds);
            }
        }
    }

    // Chromium's CalculateEventsPerSecond: events per second since the last count, 0 the first time.
    internal sealed class EventRate(Func<long, TimeSpan> elapsedSince)
    {
        private readonly Lock _gate = new();
        private readonly long _start = Stopwatch.GetTimestamp();
        private ulong _lastCount;
        private TimeSpan _lastTime;

        public int PerSecond(ulong count)
        {
            var time = elapsedSince(_start);
            lock (_gate)
            {
                var perSecond = 0;
                if (_lastCount != 0)
                {
                    var timeDelta = time - _lastTime;
                    if (timeDelta == TimeSpan.Zero) return 0;
                    perSecond = (int)Math.Clamp(Math.Round((count - _lastCount) / timeDelta.TotalSeconds, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
                }
                _lastTime = time;
                _lastCount = count;
                return perSecond;
            }
        }
    }
}
