using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What <see cref="OrielApp.SetLoginItemSettings"/> applies.</summary>
public sealed record LoginItemSettings
{
    /// <summary>Launch the app when the user logs in.</summary>
    public bool OpenAtLogin { get; init; }

    /// <summary>The executable to launch; defaults to the running process.</summary>
    public string? Path { get; init; }

    /// <summary>Arguments for the launch.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Whether the entry is switched on in Task Manager's Startup tab.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The registry value's name; defaults to the app's name.</summary>
    public string? Name { get; init; }

    /// <summary>Launch hidden. macOS only; ignored on Windows.</summary>
    public bool OpenAsHidden { get; init; }
}

/// <summary>Which executable <see cref="OrielApp.GetLoginItemSettings"/> asks about.</summary>
public sealed record LoginItemQuery
{
    /// <summary>The executable; defaults to the running process.</summary>
    public string? Path { get; init; }

    /// <summary>The arguments the entry must carry.</summary>
    public IReadOnlyList<string>? Args { get; init; }
}

/// <summary>One login entry for the queried executable.</summary>
/// <param name="Name">The registry value's name.</param>
/// <param name="Path">The executable.</param>
/// <param name="Args">Its arguments.</param>
/// <param name="Scope">Always <c>user</c> on Windows.</param>
/// <param name="Enabled">Whether Task Manager has it switched on.</param>
public sealed record LoginItemLaunchItem(string Name, string Path, IReadOnlyList<string> Args, string Scope, bool Enabled);

/// <summary>The login-item state <see cref="OrielApp.GetLoginItemSettings"/> reads.</summary>
public sealed record LoginItemStatus
{
    /// <summary>An entry for the queried path and args exists.</summary>
    public bool OpenAtLogin { get; init; }

    /// <summary>That entry exists and is enabled, so the executable runs at login.</summary>
    public bool ExecutableWillLaunchAtLogin { get; init; }

    /// <summary>Every entry whose executable is the queried path.</summary>
    public IReadOnlyList<LoginItemLaunchItem> LaunchItems { get; init; } = [];
}

public sealed unsafe partial class OrielApp
{
    /// <summary>Adds or removes the app's login item. Electron's <c>app.setLoginItemSettings</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    /// <exception cref="InvalidOperationException">The registry write failed.</exception>
    public void SetLoginItemSettings(LoginItemSettings settings) => ApplyLoginItem(settings);

    internal static void ApplyLoginItem(LoginItemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!OperatingSystem.IsWindows())
        {
            throw LoginItemsUnsupported();
        }

        string name = settings.Name ?? DefaultLoginItemName();
        if (!WindowsAppRegistry.SetLoginItem(name, settings.OpenAtLogin, settings.Enabled, settings.Path, settings.Args))
        {
            throw new InvalidOperationException($"Could not update the login item '{name}'.");
        }
    }

    /// <summary>Reads the login item for an executable. Electron's <c>app.getLoginItemSettings</c>.</summary>
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
            LaunchItems = items.Select(i => new LoginItemLaunchItem(i.Name, i.Path, i.Args, "user", i.Enabled)).ToList(),
        };
    }

    internal static string DefaultLoginItemName() => CurrentOrNull?.Name ?? "Oriel";

    private static PlatformNotSupportedException LoginItemsUnsupported()
        => new("Login items are implemented on Windows only.");
}
