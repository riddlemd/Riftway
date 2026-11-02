using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>Which SMAppService a macOS login item is; Electron's <c>type</c>.</summary>
public enum LoginItemType
{
    /// <summary>The app itself. Electron's <c>mainAppService</c>.</summary>
    MainAppService,

    /// <summary>A launch agent in the app's <c>Contents/Library/LaunchAgents</c>. Electron's <c>agentService</c>.</summary>
    AgentService,

    /// <summary>A launch daemon in the app's <c>Contents/Library/LaunchDaemons</c>. Electron's <c>daemonService</c>.</summary>
    DaemonService,

    /// <summary>A helper in the app's <c>Contents/Library/LoginItems</c>. Electron's <c>loginItemService</c>.</summary>
    LoginItemService,
}

/// <summary>What <see cref="RiftwayApp.SetLoginItemSettings"/> applies.</summary>
public sealed record LoginItemSettings
{
    /// <summary>macOS: the service to register. Electron's <c>type</c>.</summary>
    public LoginItemType Type { get; init; }

    /// <summary>macOS: the service's plist name or identifier; required unless <see cref="Type"/> is
    /// <see cref="LoginItemType.MainAppService"/>. Electron's <c>serviceName</c>.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Launch the app when the user logs in.</summary>
    public bool OpenAtLogin { get; init; }

    /// <summary>The executable to launch; null or empty is the running process.</summary>
    public string? Path { get; init; }

    /// <summary>Arguments for the launch.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Whether the entry is switched on in Task Manager's Startup tab.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The registry value's name; null or empty is the AppUserModelID.</summary>
    public string? Name { get; init; }

}

/// <summary>Which executable <see cref="RiftwayApp.GetLoginItemSettings"/> asks about.</summary>
public sealed record LoginItemQuery
{
    /// <summary>macOS: the service to ask about. Electron's <c>type</c>.</summary>
    public LoginItemType Type { get; init; }

    /// <summary>macOS: the service's name; required unless <see cref="Type"/> is
    /// <see cref="LoginItemType.MainAppService"/>. Electron's <c>serviceName</c>.</summary>
    public string? ServiceName { get; init; }

    /// <summary>The executable; null or empty is the running process.</summary>
    public string? Path { get; init; }

    /// <summary>The arguments the entry must carry for <see cref="LoginItemStatus.OpenAtLogin"/>.</summary>
    public IReadOnlyList<string>? Args { get; init; }
}

/// <summary>One login entry for the queried executable.</summary>
/// <param name="Name">The registry value's name.</param>
/// <param name="Path">The executable.</param>
/// <param name="Args">Its arguments, without switches (<c>--x</c>, <c>-x</c>, <c>/x</c>), as Chromium's
/// <c>CommandLine::GetArgs</c> reports them.</param>
/// <param name="Scope"><c>user</c> (<c>HKCU</c>) or <c>machine</c> (<c>HKLM</c>).</param>
/// <param name="Enabled">Whether Task Manager has it switched on.</param>
public sealed record LoginItemLaunchItem(string Name, string Path, IReadOnlyList<string> Args, string Scope, bool Enabled);

/// <summary>The login-item state <see cref="RiftwayApp.GetLoginItemSettings"/> reads.</summary>
public sealed record LoginItemStatus
{
    /// <summary>The <c>Run</c> value named by the AppUserModelID is the command for the queried path and args.</summary>
    public bool OpenAtLogin { get; init; }

    /// <summary>Whether the app was launched at login (macOS, from the launch Apple event); always false on Windows,
    /// as in Electron.</summary>
    public bool WasOpenedAtLogin { get; init; }

    /// <summary>macOS: the service's status, <c>not-registered</c>, <c>enabled</c>, <c>requires-approval</c> or
    /// <c>not-found</c>; null elsewhere. Electron's <c>status</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Some entry in <see cref="LaunchItems"/> is enabled, whatever its arguments.</summary>
    public bool ExecutableWillLaunchAtLogin { get; init; }

    /// <summary>Every <c>HKCU</c> then <c>HKLM</c> <c>Run</c> entry whose program is the queried path, ignoring case.</summary>
    public IReadOnlyList<LoginItemLaunchItem> LaunchItems { get; init; } = [];
}

public sealed unsafe partial class RiftwayApp
{
    // Electron's macOS Browser: whether applicationDidFinishLaunching: saw a login-item launch.
    private static bool s_launchedAtLogin;

    /// <summary>Adds or removes the app's login item: the <c>Run</c> registry entry on Windows, the SMAppService on macOS,
    /// nothing on Linux. Electron's <c>app.setLoginItemSettings</c>; failures are silent, as in Electron.</summary>
    /// <exception cref="ArgumentException">macOS: no <see cref="LoginItemSettings.ServiceName"/> for a service type
    /// other than the main app ("'name' is required when type is not mainAppService").</exception>
    public void SetLoginItemSettings(LoginItemSettings settings) => ApplyLoginItem(settings);

    internal static void ApplyLoginItem(LoginItemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (OperatingSystem.IsMacOS())
        {
            var (type, service) = MacLoginService(settings.Type, settings.ServiceName);
            if (MacApp.SetLoginItem(type, service, settings.OpenAtLogin) is { } error)
                Console.Error.WriteLine($"[Riftway] Unable to set login item: {error}");
            return;
        }
        if (!OperatingSystem.IsWindows()) return;

        string name = string.IsNullOrEmpty(settings.Name) ? WindowsAppRegistry.AppUserModelId() : settings.Name;
        WindowsAppRegistry.SetLoginItem(name, settings.OpenAtLogin, settings.Enabled, settings.Path, settings.Args);
    }

    /// <summary>Reads the login items: for an executable on Windows, for an SMAppService on macOS; all false on Linux.
    /// Electron's <c>app.getLoginItemSettings</c>.</summary>
    /// <exception cref="ArgumentException">macOS: no <see cref="LoginItemQuery.ServiceName"/> for a service type other
    /// than the main app.</exception>
    public LoginItemStatus GetLoginItemSettings(LoginItemQuery? query = null) => ReadLoginItem(query);

    internal static LoginItemStatus ReadLoginItem(LoginItemQuery? query)
    {
        if (OperatingSystem.IsMacOS())
        {
            var (type, service) = MacLoginService(query?.Type ?? LoginItemType.MainAppService, query?.ServiceName);
            var status = MacApp.LoginItemStatus(type, service);
            return new LoginItemStatus { OpenAtLogin = status == "enabled", WasOpenedAtLogin = s_launchedAtLogin, Status = status };
        }
        if (!OperatingSystem.IsWindows()) return new LoginItemStatus();

        var (open, will, items) = WindowsAppRegistry.GetLoginItems(query?.Path, query?.Args);
        return new LoginItemStatus
        {
            OpenAtLogin = open,
            ExecutableWillLaunchAtLogin = will,
            LaunchItems = items.Select(i => new LoginItemLaunchItem(i.Name, i.Path, i.Args, i.Scope, i.Enabled)).ToList(),
        };
    }

    // Electron's type strings, and its check that a service other than the app is named.
    internal static (string Type, string ServiceName) MacLoginService(LoginItemType type, string? serviceName)
    {
        var name = type switch
        {
            LoginItemType.AgentService => "agentService",
            LoginItemType.DaemonService => "daemonService",
            LoginItemType.LoginItemService => "loginItemService",
            _ => "mainAppService",
        };
        if (type != LoginItemType.MainAppService && string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("'name' is required when type is not mainAppService", nameof(serviceName));
        return (name, serviceName ?? "");
    }
}
