namespace Oriel;

public sealed partial class OrielApp
{
    /// <summary>The command line Chromium starts with. Electron's <c>app.commandLine</c>.</summary>
    public CommandLine CommandLine { get; } = new(Environment.GetCommandLineArgs().Skip(1));

    // Electron's logging switches, as CEF's; set by Run once userData is final.
    private IReadOnlyList<(string Name, string Value)> _loggingSwitches = [];
}
