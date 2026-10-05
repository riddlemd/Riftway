using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What <see cref="OrielApp.SetLoginItemSettings"/> applies.</summary>
public sealed record LoginItemSettings
{
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

    /// <summary>Launch hidden. macOS only; ignored on Windows.</summary>
    public bool OpenAsHidden { get; init; }
}

/// <summary>Which executable <see cref="OrielApp.GetLoginItemSettings"/> asks about.</summary>
public sealed record LoginItemQuery
{
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

/// <summary>The login-item state <see cref="OrielApp.GetLoginItemSettings"/> reads.</summary>
public sealed record LoginItemStatus
{
    /// <summary>The <c>Run</c> value named by the AppUserModelID is the command for the queried path and args.</summary>
    public bool OpenAtLogin { get; init; }

    /// <summary>Whether the app was launched at login; always false on Windows, as in Electron.</summary>
    public bool WasOpenedAtLogin { get; init; }

    /// <summary>Some entry in <see cref="LaunchItems"/> is enabled, whatever its arguments.</summary>
    public bool ExecutableWillLaunchAtLogin { get; init; }

    /// <summary>Every <c>HKCU</c> then <c>HKLM</c> <c>Run</c> entry whose program is the queried path, ignoring case.</summary>
    public IReadOnlyList<LoginItemLaunchItem> LaunchItems { get; init; } = [];
}

public sealed unsafe partial class OrielApp
{
    /// <summary>Adds or removes the app's login item. Electron's <c>app.setLoginItemSettings</c>; registry failures are
    /// silent, as in Electron.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public void SetLoginItemSettings(LoginItemSettings settings) => ApplyLoginItem(settings);

    internal static void ApplyLoginItem(LoginItemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!OperatingSystem.IsWindows())
        {
            throw LoginItemsUnsupported();
        }

        string name = string.IsNullOrEmpty(settings.Name) ? WindowsAppRegistry.AppUserModelId() : settings.Name;
        WindowsAppRegistry.SetLoginItem(name, settings.OpenAtLogin, settings.Enabled, settings.Path, settings.Args);
    }

    /// <summary>Reads the login items for an executable. Electron's <c>app.getLoginItemSettings</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public LoginItemStatus GetLoginItemSettings(LoginItemQuery? query = null) => ReadLoginItem(query);

    internal static LoginItemStatus ReadLoginItem(LoginItemQuery? query)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw LoginItemsUnsupported();
        }

        var (open, will, items) = WindowsAppRegistry.GetLoginItems(query?.Path, query?.Args);
        return new LoginItemStatus
        {
            OpenAtLogin = open,
            ExecutableWillLaunchAtLogin = will,
            LaunchItems = items.Select(i => new LoginItemLaunchItem(i.Name, i.Path, i.Args, i.Scope, i.Enabled)).ToList(),
        };
    }

    private static PlatformNotSupportedException LoginItemsUnsupported()
        => new("Login items are implemented on Windows only.");
}
