using System.Reflection;

namespace Riftway;

/// <summary>The app's fuses: build-time switches a launcher cannot flip, as Electron's fuses are. Set with
/// <c>RiftwayFuse&lt;Name&gt;</c> MSBuild properties, which put <c>Riftway.Fuse.&lt;Name&gt;</c> assembly metadata in the
/// app's assembly (covered by its signature as Electron's fuse bits are by the exe's). <c>@electron/fuses read</c>'s view.</summary>
public static class Fuses
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_metadata = new(() => Metadata(Assembly.GetEntryAssembly()));

    /// <summary>Archives must match the header hash in the app's exe, and every block read its hash
    /// (<c>RiftwayFuseArchiveIntegrity</c>; Electron's <c>EnableEmbeddedAsarIntegrityValidation</c>). Off by default.</summary>
    public static bool ArchiveIntegrity => Get(nameof(ArchiveIntegrity), false);

    /// <summary>Blazor windows take their static files only from <c>resources/app.asar</c>, never from a loose
    /// <c>wwwroot</c> (<c>RiftwayFuseOnlyLoadFromArchive</c>; Electron's <c>OnlyLoadAppFromAsar</c>). Off by default.</summary>
    public static bool OnlyLoadFromArchive => Get(nameof(OnlyLoadFromArchive), false);

    /// <summary>.NET startup hooks may run in the app (<c>RiftwayFuseStartupHooks</c>; Electron's <c>RunAsNode</c>: a
    /// launcher's code in the app's process). Off sets <c>StartupHookSupport=false</c>, which the runtime enforces. On by default.</summary>
    public static bool StartupHooks => Get(nameof(StartupHooks), true);

    /// <summary>Chromium's remote debugging switches are honoured (<c>RiftwayFuseRemoteDebugging</c>; Electron's
    /// <c>EnableNodeCliInspectArguments</c>). Off strips <c>--remote-debugging-*</c> and <c>--remote-allow-origins</c>.
    /// On by default.</summary>
    public static bool RemoteDebugging => Get(nameof(RemoteDebugging), true);

    /// <summary>The app starts with .NET variables that load other code set (<c>RiftwayFuseDotnetEnvironment</c>;
    /// Electron's <c>EnableNodeOptionsEnvironmentVariable</c>). Off refuses to start with any of
    /// <see cref="InjectingVariables"/> set. On by default.</summary>
    public static bool DotnetEnvironment => Get(nameof(DotnetEnvironment), true);

    /// <summary>Environment variables through which a launcher gets other code into a .NET process.</summary>
    public static IReadOnlyList<string> InjectingVariables { get; } =
    [
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE",
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_64", "CORECLR_PROFILER_PATH_32",
        "CORECLR_PROFILER_PATH_ARM64", "DOTNET_PROFILER_PATH",
    ];

    /// <summary>The remote debugging switches <see cref="RemoteDebugging"/> off removes.</summary>
    internal static IReadOnlyList<string> RemoteDebuggingSwitches { get; } =
        ["remote-debugging-port", "remote-debugging-pipe", "remote-debugging-address", "remote-allow-origins"];

    private static bool Get(string name, bool defaultValue)
        => s_metadata.Value.TryGetValue(name, out var value) && bool.TryParse(value, out var on) ? on : defaultValue;

    /// <summary>An assembly's fuse values by name.</summary>
    internal static IReadOnlyDictionary<string, string> Metadata(Assembly? assembly)
    {
        var fuses = new Dictionary<string, string>(StringComparer.Ordinal);
        if (assembly is null) return fuses;
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (attribute.Key.StartsWith("Riftway.Fuse.", StringComparison.Ordinal) && attribute.Value is not null)
                fuses[attribute.Key["Riftway.Fuse.".Length..]] = attribute.Value;
        return fuses;
    }

    /// <summary>The injecting variables set (non-empty) in an environment.</summary>
    internal static IReadOnlyList<string> InjectingVariablesSet(Func<string, string?> environment)
        => [.. InjectingVariables.Where(name => !string.IsNullOrEmpty(environment(name)))];

    /// <summary><c>RiftwayApp.Create</c>: with <see cref="DotnetEnvironment"/> off, a process started with code-loading
    /// variables ends before the app runs.</summary>
    internal static void EnforceAtStartup()
    {
        if (DotnetEnvironment) return;
        var set = InjectingVariablesSet(Environment.GetEnvironmentVariable);
        if (set.Count > 0)
            Environment.FailFast($"The DotnetEnvironment fuse is off and the process was started with {string.Join(", ", set)} set.");
    }
}
