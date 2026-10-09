namespace Oriel;

/// <summary>How <see cref="UtilityProcess.Fork(string, IReadOnlyList{string}, UtilityProcessOptions)"/> starts the child.
/// Electron's <c>ForkOptions</c>.</summary>
public sealed class UtilityProcessOptions
{
    /// <summary>The child's whole environment. Null or empty inherits this process's, as Electron's does.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Options for the .NET host, placed before the module when it runs through <c>dotnet exec</c>
    /// (<c>--roll-forward</c>, <c>--additionalprobingpath</c>, ...). Ignored when the module is an executable (drift UP3).</summary>
    public IReadOnlyList<string>? ExecArgv { get; init; }

    /// <summary>The child's working directory; this process's when null.</summary>
    public string? Cwd { get; init; }

    /// <summary><c>"inherit"</c> (the default), <c>"pipe"</c> or <c>"ignore"</c> for stdout and stderr, or Electron's array
    /// form <c>["ignore", stdout, stderr]</c>. Stdin is always ignored, as in Electron.</summary>
    public UtilityProcessStdio? Stdio { get; init; }

    /// <summary>Electron names the process in <c>app.getAppMetrics()</c> with it; kept, with no effect yet (drift UP5).</summary>
    public string? ServiceName { get; init; }

    /// <summary>No effect: the child has no Chromium network stack to share a session with (drift UP4).</summary>
    public Session? Session { get; init; }

    /// <summary>No effect: the child has no Chromium network stack to share a session with (drift UP4).</summary>
    public string? Partition { get; init; }

    /// <summary>No effect: the child is its own executable, not a Chromium helper app (drift UP4).</summary>
    public bool AllowLoadingUnsignedLibraries { get; init; }

    /// <summary>No effect: the child is started by .NET, not Chromium's launcher (drift UP4).</summary>
    public bool Disclaim { get; init; }

    /// <summary>No effect: the child has no Chromium <c>net</c> module whose auth requests could be answered here (drift UP4).</summary>
    public bool RespondToAuthRequestsFromMainProcess { get; init; }
}

/// <summary>The <c>stdio</c> option: one mode for stdout and stderr, or Electron's three-entry array.</summary>
public sealed class UtilityProcessStdio
{
    private readonly string? _mode;
    private readonly IReadOnlyList<string>? _modes;

    private UtilityProcessStdio(string? mode, IReadOnlyList<string>? modes)
    {
        _mode = mode;
        _modes = modes;
    }

    /// <summary><c>"inherit"</c>, <c>"pipe"</c> or <c>"ignore"</c> for both stdout and stderr.</summary>
    public static UtilityProcessStdio FromMode(string mode) => new(mode ?? throw new ArgumentNullException(nameof(mode)), null);

    /// <summary>Electron's array form: <c>["ignore", stdout, stderr]</c>.</summary>
    public static UtilityProcessStdio FromModes(IReadOnlyList<string> modes) => new(null, modes ?? throw new ArgumentNullException(nameof(modes)));

    /// <inheritdoc cref="FromMode"/>
    public static implicit operator UtilityProcessStdio(string mode) => FromMode(mode);

    /// <inheritdoc cref="FromModes"/>
    public static implicit operator UtilityProcessStdio(string[] modes) => FromModes(modes);

    // Electron's validation and messages (lib/browser/api/utility-process.ts).
    internal (StdioMode Stdout, StdioMode Stderr) Resolve()
    {
        if (_mode is not null)
        {
            return _mode switch
            {
                "inherit" => (StdioMode.Inherit, StdioMode.Inherit),
                "ignore" => (StdioMode.Ignore, StdioMode.Ignore),
                "pipe" => (StdioMode.Pipe, StdioMode.Pipe),
                _ => throw new ArgumentException("stdio must be of the following values: inherit, pipe, ignore"),
            };
        }
        var modes = _modes!;
        if (modes.Count < 3) throw new ArgumentException("configuration missing for stdin, stdout or stderr.");
        if (modes[0] != "ignore") throw new ArgumentException("stdin value other than ignore is not supported.");
        return (Parse(modes[1], "stdout"), Parse(modes[2], "stderr"));
    }

    private static StdioMode Parse(string mode, string stream) => mode switch
    {
        "pipe" => StdioMode.Pipe,
        "ignore" => StdioMode.Ignore,
        "inherit" => StdioMode.Inherit,
        _ => throw new ArgumentException($"{stream} configuration must be of the following values: inherit, pipe, ignore"),
    };
}

internal enum StdioMode
{
    Inherit,
    Pipe,
    Ignore,
}

/// <summary>The command that starts a utility process's module.</summary>
internal sealed record UtilityLaunch(string FileName, IReadOnlyList<string> Arguments, bool ViaHost)
{
    /// <summary>An assembly (<c>.dll</c>) runs through the same .NET host as this process (<c>dotnet exec</c>), or its own
    /// apphost beside it when this process has no host to share (self-contained); anything else is run as an executable.</summary>
    // processPath: This process's executable (Environment.ProcessPath).
    // runtimeDirectory: This process's runtime directory; under a shared install it is `<root>/shared/Microsoft.NETCore.App/<version>/`, beside `<root>/dotnet`.
    internal static UtilityLaunch Resolve(string modulePath, IReadOnlyList<string> args, IReadOnlyList<string> execArgv,
        string? processPath, string runtimeDirectory, bool windows, Func<string, bool> fileExists)
    {
        if (!modulePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return new(modulePath, [.. args], ViaHost: false);

        var muxerName = windows ? "dotnet.exe" : "dotnet";
        if (processPath is not null && string.Equals(Path.GetFileName(processPath), muxerName, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return ViaMuxer(processPath);

        var version = new DirectoryInfo(runtimeDirectory.TrimEnd('/', '\\'));
        if (version.Parent is { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } root } }
            && Path.Combine(root.FullName, muxerName) is var muxer && fileExists(muxer))
            return ViaMuxer(muxer);

        var apphost = Path.ChangeExtension(modulePath, windows ? ".exe" : null);
        if (fileExists(apphost)) return new(apphost, [.. args], ViaHost: false);

        // Last resort: a dotnet on PATH.
        return ViaMuxer("dotnet");

        UtilityLaunch ViaMuxer(string host) => new(host, ["exec", .. execArgv, modulePath, .. args], ViaHost: true);
    }

    /// <summary>The exit code Electron reports: a child this side killed exited normally, whatever signal ended it.</summary>
    /// <remarks>.NET reports a signal as 128 + its number; Chromium reports the bare number, and Electron maps SIGTERM
    /// and SIGKILL to 0 for <c>kill()</c> (electron_api_utility_process.cc, HandleTermination).</remarks>
    internal static int ExitCode(int code, bool killed, bool windows)
        => killed && !windows && code is 128 + 15 or 128 + 9 ? 0 : code;
}
