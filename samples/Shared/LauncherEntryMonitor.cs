using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Oriel.Samples;

/// <summary>Reads <c>com.canonical.Unity.LauncherEntry</c> <c>Update</c> signals off the session bus with dbus-monitor, a
/// decoder independent of Oriel's own D-Bus code. Each signal is its body as dbus-monitor prints it, whitespace collapsed.</summary>
internal sealed partial class LauncherEntryMonitor : IDisposable
{
    private const string ProbeInterface = "org.oriel.LauncherEntryProbe";
    private readonly Process _process;
    // Guarded by itself; the last entry grows while dbus-monitor prints it.
    private readonly List<(string Header, string Body)> _messages = [];

    private LauncherEntryMonitor(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            lock (_messages)
            {
                if (!line.StartsWith(' ')) _messages.Add((line, ""));
                else if (_messages.Count > 0)
                    _messages[^1] = (_messages[^1].Header, (_messages[^1].Body + " " + Spaces().Replace(line.Trim(), " ")).TrimStart());
            }
        };
        process.BeginOutputReadLine();
    }

    /// <summary>Starts dbus-monitor and waits until it sees signals; null without a session bus or the tool.</summary>
    public static LauncherEntryMonitor? Start()
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not { Length: > 0 } || !File.Exists("/usr/bin/dbus-monitor")) return null;
        var start = new ProcessStartInfo("/usr/bin/dbus-monitor",
            ["--session", "type='signal',interface='com.canonical.Unity.LauncherEntry'", $"type='signal',interface='{ProbeInterface}'"])
        { RedirectStandardOutput = true, RedirectStandardError = true };
        var monitor = new LauncherEntryMonitor(Process.Start(start)!);
        return monitor.Sync() ? monitor : null;
    }

    /// <summary>The Update signals seen so far, as (app uri, properties) strings, after <paramref name="from"/> of them.</summary>
    public List<string> Updates(int from = 0)
    {
        lock (_messages)
            return [.. _messages.Where(m => m.Header.EndsWith(" path=/com/canonical/unity/launcherentry; interface=com.canonical.Unity.LauncherEntry; member=Update", StringComparison.Ordinal))
                .Skip(from).Select(m => m.Body)];
    }

    /// <summary>Waits until every signal sent before this call has been printed: a probe signal sent after them comes back.</summary>
    public bool Sync()
    {
        var id = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var start = new ProcessStartInfo("/usr/bin/dbus-send", ["--session", "--type=signal", "/org/oriel/Probe", ProbeInterface + ".Ping", "string:" + id])
            { RedirectStandardOutput = true };
            // To EOF, not WaitForExit, which can hang inside the CEF process on Linux.
            using (var send = Process.Start(start)!) send.StandardOutput.ReadToEnd();
            for (var i = 0; i < 10; i++)
            {
                lock (_messages)
                    if (_messages.Any(m => m.Body == $"string \"{id}\"")) return true;
                Thread.Sleep(50);
            }
        }
        return false;
    }

    /// <summary>The body dbus-monitor prints for an Update signal of <paramref name="uri"/> with these properties, each
    /// value as dbus-monitor types it (<c>int64 7</c>, <c>boolean true</c>, <c>double 0.5</c>).</summary>
    public static string Update(string uri, params (string Key, string Value)[] properties)
        => $"string \"{uri}\" array [ " + string.Concat(properties.Select(p => $"dict entry( string \"{p.Key}\" variant {p.Value} ) ")) + "]";

    public void Dispose()
    {
        try { _process.Kill(); } catch (InvalidOperationException) { }
        _process.Dispose();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
