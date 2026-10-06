using System.Globalization;

namespace Oriel;

/// <summary>A process's memory in kilobytes; Electron's <c>MemoryInfo</c>. Not reported on Linux, as in Electron.</summary>
/// <param name="WorkingSetSize">Memory pinned to physical RAM.</param>
/// <param name="PeakWorkingSetSize">The most memory ever pinned to physical RAM.</param>
/// <param name="PrivateBytes">Memory not shared with other processes (Windows).</param>
public sealed record ProcessMemoryInfo(long WorkingSetSize, long PeakWorkingSetSize, long? PrivateBytes);

/// <summary>One of the app's processes; Electron's <c>ProcessMetric</c>.</summary>
/// <param name="Pid">Its process id.</param>
/// <param name="Type">Chromium's English name for its kind: <c>Browser</c>, <c>Tab</c>, <c>GPU</c>, <c>Utility</c>.</param>
/// <param name="Cpu">Its CPU use since the previous <see cref="OrielApp.GetAppMetrics"/>; 0 the first time.</param>
/// <param name="CreationTime">When it started, in milliseconds since the Unix epoch.</param>
/// <param name="ServiceName">A utility process's service, e.g. <c>network.mojom.NetworkService</c>.</param>
/// <param name="Name">A utility process's display name: never known here (drift A18).</param>
/// <param name="Memory">Its memory, except on Linux.</param>
public sealed record ProcessMetric(int Pid, string Type, CpuUsage Cpu, double CreationTime, string? ServiceName, string? Name, ProcessMemoryInfo? Memory);

public sealed partial class OrielApp
{
    private readonly Lock _metricsGate = new();
    private readonly Dictionary<int, (OrielProcess.CpuSampler Cpu, OrielProcess.EventRate Wakeups)> _samplers = [];

    /// <summary>The app's processes: this one and the Chromium processes it started, each with its CPU use since the
    /// previous call. Electron's <c>app.getAppMetrics()</c>; child processes are listed on Linux only so far (drift A18).</summary>
    public IReadOnlyList<ProcessMetric> GetAppMetrics()
    {
        var self = Environment.ProcessId;
        var processes = new List<(int Pid, string Type, string? Service)> { (self, "Browser", null) };
        if (OperatingSystem.IsLinux()) processes.AddRange(LinuxChildren(self));
        var metrics = new List<ProcessMetric>();
        lock (_metricsGate)
        {
            foreach (var stale in _samplers.Keys.Except(processes.Select(p => p.Pid)).ToList()) _samplers.Remove(stale);
            foreach (var (pid, type, service) in processes)
            {
                if (ReadProcess(pid) is not { } info) continue;
                if (!_samplers.TryGetValue(pid, out var samplers))
                {
                    samplers = (new OrielProcess.CpuSampler(() => TimeSpan.Zero, System.Diagnostics.Stopwatch.GetElapsedTime), new OrielProcess.EventRate(System.Diagnostics.Stopwatch.GetElapsedTime));
                    _samplers[pid] = samplers;
                }
                var (percent, cumulative) = samplers.Cpu.Sample(info.Cpu);
                var wakeups = OperatingSystem.IsWindows() || info.Wakeups is not { } count ? 0 : samplers.Wakeups.PerSecond(count);
                metrics.Add(new ProcessMetric(pid, type, new CpuUsage(percent / Environment.ProcessorCount, cumulative, wakeups), info.CreationTime, service, null, null));
            }
        }
        return metrics;
    }

    // A process's cumulative CPU time, start time and voluntary context switches; null once it is gone.
    private static (TimeSpan Cpu, double CreationTime, ulong? Wakeups)? ReadProcess(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            if (ParseLinuxStat(TryRead($"/proc/{pid}/stat")) is not { } stat || LinuxBootTime() is not { } boot) return null;
            var ticks = LinuxClockTicks;
            return (TimeSpan.FromSeconds((double)(stat.UserTicks + stat.SystemTicks) / ticks), (boot + (double)stat.StartTicks / ticks) * 1000,
                LinuxVoluntarySwitches(TryRead($"/proc/{pid}/status")));
        }
        if (pid != Environment.ProcessId) return null;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        return (current.TotalProcessorTime, OrielProcess.GetCreationTime() ?? 0, null);
    }

    // Every descendant whose --type is one Chromium tracks as a child process: renderers, the GPU process, utilities.
    // The zygotes and the crash handler are not, as Electron's list leaves them out.
    private static IEnumerable<(int Pid, string Type, string? Service)> LinuxChildren(int root)
    {
        foreach (var pid in LinuxDescendants(root))
            // Chromium rewrites a child's title into one space-separated string, so split on spaces as well as NULs.
            if (ChildKind(TryRead($"/proc/{pid}/cmdline").Split(['\0', ' '], StringSplitOptions.RemoveEmptyEntries)) is { } kind) yield return (pid, kind.Type, kind.Service);
    }

    internal static IEnumerable<int> LinuxDescendants(int root)
    {
        var parents = new Dictionary<int, int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), out var pid) && ParseLinuxStat(TryRead(Path.Combine(dir, "stat"))) is { } stat) parents[pid] = stat.ParentPid;
        bool Descends(int pid)
        {
            for (var depth = 0; depth < 64 && parents.TryGetValue(pid, out var parent); depth++, pid = parent)
                if (parent == root) return true;
            return false;
        }
        return parents.Keys.Where(Descends).Order();
    }

    // content::GetProcessTypeNameInEnglish for the --type a child was launched with; the service is a utility's sub-type.
    internal static (string Type, string? Service)? ChildKind(IReadOnlyList<string> arguments)
    {
        string? Switch(string name) => arguments.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal))?[(name.Length + 3)..];
        return Switch("type") switch
        {
            "renderer" => ("Tab", null),
            "gpu-process" => ("GPU", null),
            "utility" => ("Utility", Switch("utility-sub-type") is { Length: > 0 } service ? service : null),
            _ => null,
        };
    }

    // /proc/<pid>/stat: the fields after the command's closing parenthesis, which may itself contain spaces.
    internal static (int ParentPid, ulong UserTicks, ulong SystemTicks, ulong StartTicks)? ParseLinuxStat(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // After the command: state, ppid, ..., utime is field 14 and stime 15, starttime 22 (1-based over the whole line).
        if (fields.Length < 20) return null;
        static ulong At(string[] f, int field) => ulong.Parse(f[field - 3], CultureInfo.InvariantCulture);
        return ((int)At(fields, 4), At(fields, 14), At(fields, 15), At(fields, 22));
    }

    private static double? LinuxBootTime()
    {
        foreach (var line in TryRead("/proc/stat").Split('\n'))
            if (line.StartsWith("btime ", StringComparison.Ordinal) && double.TryParse(line[6..], CultureInfo.InvariantCulture, out var seconds)) return seconds;
        return null;
    }

    private static ulong? LinuxVoluntarySwitches(string status)
    {
        foreach (var line in status.Split('\n'))
            if (line.StartsWith("voluntary_ctxt_switches:", StringComparison.Ordinal)
                && ulong.TryParse(line["voluntary_ctxt_switches:".Length..].Trim(), CultureInfo.InvariantCulture, out var count)) return count;
        return null;
    }

    // sysconf(_SC_CLK_TCK), as Chromium converts /proc ticks.
    private static double LinuxClockTicks => s_clockTicks ??= sysconf(2);

    private static double? s_clockTicks;

    [System.Runtime.InteropServices.DllImport("libc")] private static extern long sysconf(int name);

    private static string TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
