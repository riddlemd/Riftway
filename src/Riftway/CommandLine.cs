using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>The command line Chromium starts with: the process's arguments plus what the app changes. Electron's
/// <c>CommandLine</c> (<c>app.commandLine</c>).</summary>
/// <remarks>Chromium reads its switches when it starts, so changes reach it only when made before
/// <see cref="RiftwayApp.Run"/>; later ones are still recorded and readable. Switch names are given without the leading
/// <c>--</c> and, as in Electron, lowercased.</remarks>
public sealed class CommandLine
{
    private readonly Lock _gate = new();
    // Chromium's switch map: last value wins; "" for a switch without one.
    private readonly Dictionary<string, string> _switches = new(StringComparer.Ordinal);
    // Replayed in order onto Chromium's command line, as Electron's calls change it in order.
    private readonly List<Change> _changes = [];

    private abstract record Change;
    private sealed record AppendedSwitch(string Name, string Value) : Change;
    private sealed record RemovedSwitch(string Name) : Change;
    private sealed record AppendedArgument(string Value) : Change;

    internal CommandLine(IEnumerable<string> processArguments)
    {
        foreach (var argument in processArguments)
        {
            // Chromium reads everything after a bare -- as arguments.
            if (argument == "--") break;
            if (ParseSwitch(argument) is { } parsed)
                _switches[OperatingSystem.IsWindows() ? ToLowerAscii(parsed.Name) : parsed.Name] = parsed.Value;
        }
    }

    /// <summary>Adds <c>--name</c>, or <c>--name=value</c>; a name that already has a switch prefix keeps it. Electron's
    /// <c>commandLine.appendSwitch()</c>.</summary>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public void AppendSwitch(string name, string? value = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var key = ToLowerAscii(name);
        key = key[PrefixLength(key)..];
        lock (_gate)
        {
            _switches[key] = value ?? "";
            _changes.Add(new AppendedSwitch(key, value ?? ""));
        }
    }

    /// <summary>Adds an argument, which Chromium keeps after the switches. Electron's
    /// <c>commandLine.appendArgument()</c>.</summary>
    public void AppendArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate) _changes.Add(new AppendedArgument(value));
    }

    /// <summary>The switches as they stand, as <c>--name</c> arguments after a program name, for code that reads
    /// base::CommandLine's way.</summary>
    internal IReadOnlyList<string> SwitchArguments()
    {
        lock (_gate) return ["", .. _switches.Keys.Select(name => "--" + name)];
    }

    /// <summary>Whether the switch is present. Electron's <c>commandLine.hasSwitch()</c>.</summary>
    public bool HasSwitch(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate) return _switches.ContainsKey(ToLowerAscii(name));
    }

    /// <summary>The switch's value; empty when it is absent or has none. Electron's <c>commandLine.getSwitchValue()</c>.</summary>
    public string GetSwitchValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate) return _switches.GetValueOrDefault(ToLowerAscii(name), "");
    }

    /// <summary>Removes a switch, whether it came from the process's arguments or <see cref="AppendSwitch"/>. Electron's
    /// <c>commandLine.removeSwitch()</c>.</summary>
    public void RemoveSwitch(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = ToLowerAscii(name);
        lock (_gate)
        {
            _switches.Remove(key);
            _changes.Add(new RemovedSwitch(key));
        }
    }

    /// <summary>Electron's logging switches and variables, as the CEF switches that do the same (shell/common/logging.cc):
    /// <c>--enable-logging</c>, else <c>ELECTRON_ENABLE_LOGGING</c> when set, turns the log on; it goes to a file when
    /// <c>--log-file</c> or <c>ELECTRON_LOG_FILE</c> names one or the destination is <c>file</c>
    /// (<c>riftway_debug.log</c> in <paramref name="userData"/> by default), else to stderr. <c>--log-level</c> 0-3 sets
    /// the level. <c>RIFTWAY_ENABLE_LOGGING</c> and <c>RIFTWAY_LOG_FILE</c> stand in when the Electron names are unset.</summary>
    /// <remarks>CEF sets the log level from its own <c>--log-severity</c>, over Chromium's <c>--log-level</c>; Chromium's
    /// bare <c>--enable-logging</c> means a file, Electron's means stderr.</remarks>
    internal IReadOnlyList<(string Name, string Value)> ElectronLoggingSwitches(Func<string, string?> environment, string userData)
    {
        var switches = new List<(string, string)>();
        string? destination;
        if (HasSwitch("enable-logging")) destination = GetSwitchValue("enable-logging");
        else destination = environment("ELECTRON_ENABLE_LOGGING") ?? environment("RIFTWAY_ENABLE_LOGGING");
        var enabled = destination is not null;

        // Electron sets the level whether or not the log is on; out-of-range or non-numeric values are ignored.
        var severity = GetSwitchValue("log-level") switch
        {
            "0" => "info",
            "1" => "warning",
            "2" => "error",
            "3" => "fatal",
            _ => enabled ? "info" : null,
        };
        if (severity is not null && !HasSwitch("log-severity")) switches.Add(("log-severity", severity));
        if (!enabled) return switches;

        var file = GetSwitchValue("log-file") is { Length: > 0 } fromSwitch ? fromSwitch
            : NonEmpty(environment("ELECTRON_LOG_FILE")) ?? NonEmpty(environment("RIFTWAY_LOG_FILE"));
        if (file is not null || destination == "file")
        {
            switches.Add(("enable-logging", ""));
            switches.Add(("log-file", file ?? Path.Combine(userData, "riftway_debug.log")));
        }
        else
        {
            switches.Add(("enable-logging", "stderr"));
        }
        // Electron's log lines carry the process id and a timestamp only.
        if (!HasSwitch("log-items")) switches.Add(("log-items", "pid,timestamp"));
        return switches;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Chromium's syntax: <c>--name</c>, <c>-name</c> (and <c>/name</c> on Windows), each with an optional
    /// <c>=value</c>; anything else is an argument.</summary>
    internal static (string Name, string Value)? ParseSwitch(string argument)
    {
        var prefix = PrefixLength(argument);
        if (prefix == 0 || argument.Length == prefix) return null;
        var body = argument[prefix..];
        var equals = body.IndexOf('=');
        return equals < 0 ? (body, "") : (body[..equals], body[(equals + 1)..]);
    }

    private static int PrefixLength(string value)
        => value.StartsWith("--", StringComparison.Ordinal) ? 2
            : value.StartsWith('-') || (OperatingSystem.IsWindows() && value.StartsWith('/')) ? 1
            : 0;

    // base::ToLowerASCII: other letters keep their case.
    private static string ToLowerAscii(string value) => string.Create(value.Length, value, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++) span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
    });

    /// <summary>UI thread, the browser process's <c>on_before_command_line_processing</c>: replays the changes.</summary>
    internal unsafe void ApplyTo(cef_command_line_t* commandLine)
    {
        lock (_gate)
        {
            foreach (var change in _changes)
            {
                switch (change)
                {
                    case AppendedSwitch(var name, ""):
                        Call(commandLine, name, null, static (c, n, _) => c->append_switch(c, n));
                        break;
                    case AppendedSwitch(var name, var value):
                        Call(commandLine, name, value, static (c, n, v) => c->append_switch_with_value(c, n, v));
                        break;
                    case RemovedSwitch(var name):
                        Call(commandLine, name, null, static (c, n, _) => c->remove_switch(c, n));
                        break;
                    case AppendedArgument(var value):
                        Call(commandLine, value, null, static (c, a, _) => c->append_argument(c, a));
                        break;
                }
            }
        }
    }

    internal static unsafe void Append(cef_command_line_t* commandLine, IEnumerable<(string Name, string Value)> switches)
    {
        foreach (var (name, value) in switches)
            Call(commandLine, name, value, static (c, n, v) => c->append_switch_with_value(c, n, v));
    }

    private unsafe delegate void CommandLineCall(cef_command_line_t* commandLine, cef_string_utf16_t* first, cef_string_utf16_t* second);

    private static unsafe void Call(cef_command_line_t* commandLine, string first, string? second, CommandLineCall call)
    {
        cef_string_utf16_t a = default, b = default;
        CefString.Set(&a, first);
        CefString.Set(&b, second);
        call(commandLine, &a, &b);
        CefString.Clear(&a);
        CefString.Clear(&b);
    }
}
