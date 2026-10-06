namespace Oriel;

public sealed partial class OrielApp
{
    /// <summary>The process's command line as Chromium reads it. Electron's <c>app.commandLine</c>.</summary>
    public AppCommandLine CommandLine { get; } = new();
}

/// <summary>Chromium's command line: switches the app adds before <see cref="OrielApp.Run"/> reach Chromium as if typed.
/// Electron's <c>CommandLine</c> (<c>app.commandLine</c>).</summary>
public sealed class AppCommandLine
{
    private readonly Lock _gate = new();
    private readonly List<(string Name, string? Value)> _appended = [];

    /// <summary>Adds <c>--name</c> (or <c>--name=value</c>). Chromium reads its switches at start-up, so most only work when
    /// added before <see cref="OrielApp.Run"/>, as in Electron. Electron's <c>appendSwitch()</c>.</summary>
    public void AppendSwitch(string name, string? value = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate) _appended.Add((name, value));
    }

    /// <summary>Whether the switch is on the command line or was appended. Electron's <c>hasSwitch()</c>.</summary>
    public bool HasSwitch(string name) => Find(name) is not null;

    /// <summary>The switch's value; empty when it is absent or has none. Electron's <c>getSwitchValue()</c>.</summary>
    public string GetSwitchValue(string name) => Find(name)?.Value ?? string.Empty;

    internal IReadOnlyList<(string Name, string? Value)> Appended
    {
        get { lock (_gate) return [.. _appended]; }
    }

    private (string Name, string? Value)? Find(string name)
    {
        lock (_gate)
        {
            for (var i = _appended.Count - 1; i >= 0; i--)
                if (_appended[i].Name == name) return _appended[i];
        }
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
        {
            if (!argument.StartsWith("--", StringComparison.Ordinal)) continue;
            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? argument[2..] : argument[2..equals];
            if (key == name) return (key, equals < 0 ? null : argument[(equals + 1)..]);
        }
        return null;
    }
}
