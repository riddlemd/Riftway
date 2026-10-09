using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The app that handles a protocol. Electron's <c>app.getApplicationInfoForProtocol()</c> result.</summary>
/// <param name="Icon">The app's icon.</param>
/// <param name="Path">The app's executable.</param>
/// <param name="Name">The app's display name.</param>
public sealed record ApplicationInfoForProtocol(NativeImage Icon, string Path, string Name);

public sealed unsafe partial class OrielApp
{
    /// <summary>Registers this app as the handler of <paramref name="protocol"/> (for example <c>myapp</c>).
    /// Electron's <c>app.setAsDefaultProtocolClient</c>.</summary>
    /// <param name="protocol">The scheme, without <c>://</c>: an ASCII letter, then letters, digits, <c>+</c>, <c>-</c> or <c>.</c>.</param>
    /// <param name="path">The executable to launch; null is the running process.</param>
    /// <param name="args">Arguments placed before the URL on the command line.</param>
    /// <returns>False for an invalid scheme or when registration fails.</returns>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public bool SetAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => SetProtocolClient(protocol, path, args);

    internal static bool SetProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
        => OperatingSystem.IsWindows()
            ? WindowsAppRegistry.SetAsDefaultProtocolClient(protocol, path, args)
            : throw ProtocolUnsupported();

    /// <summary>Removes this app's registration for <paramref name="protocol"/>. Electron's
    /// <c>app.removeAsDefaultProtocolClient</c>.</summary>
    /// <returns>True when the registration was this app's and is gone, and also when nothing or another app is
    /// registered; false for an invalid scheme or when the registry refuses.</returns>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public bool RemoveAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => RemoveProtocolClient(protocol, path, args);

    internal static bool RemoveProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
        => OperatingSystem.IsWindows()
            ? WindowsAppRegistry.RemoveAsDefaultProtocolClient(protocol, path, args)
            : throw ProtocolUnsupported();

    /// <summary>Whether the registered command of <paramref name="protocol"/> is exactly this app's (case-sensitive).
    /// Electron's <c>app.isDefaultProtocolClient</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public bool IsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => IsProtocolClient(protocol, path, args);

    internal static bool IsProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
        => OperatingSystem.IsWindows()
            ? WindowsAppRegistry.IsDefaultProtocolClient(protocol, path, args)
            : throw ProtocolUnsupported();

    /// <summary>The display name of the app that opens <paramref name="url"/>'s scheme, or an empty string when the
    /// scheme is not a registered URL protocol. Electron's <c>app.getApplicationNameForProtocol</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public string GetApplicationNameForProtocol(string url)
        => ProtocolNameFor(url);

    internal static string ProtocolNameFor(string url)
        => OperatingSystem.IsWindows()
            ? WindowsAppRegistry.GetAppInfoForProtocol(url, WindowsAppRegistry.AssocstrFriendlyAppName)
            : throw ProtocolUnsupported();

    /// <summary>The executable, display name and large icon of the app that opens <paramref name="url"/>'s scheme.
    /// Electron's <c>app.getApplicationInfoForProtocol</c>.</summary>
    /// <remarks>The task faults with <see cref="InvalidOperationException"/>: "Unable to retrieve installation path to
    /// app", "Unable to retrieve display name of app" or "Failed to get file icon.".</remarks>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public Task<ApplicationInfoForProtocol> GetApplicationInfoForProtocol(string url)
        => ProtocolInfoFor(url);

    internal static Task<ApplicationInfoForProtocol> ProtocolInfoFor(string url)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw ProtocolUnsupported();
        }

        string path = WindowsAppRegistry.GetAppInfoForProtocol(url, WindowsAppRegistry.AssocstrExecutable);
        if (path.Length == 0)
        {
            return Task.FromException<ApplicationInfoForProtocol>(new InvalidOperationException("Unable to retrieve installation path to app"));
        }

        string name = WindowsAppRegistry.GetAppInfoForProtocol(url, WindowsAppRegistry.AssocstrFriendlyAppName);
        return ApplicationInfoFor(path, name);
    }

    internal static Task<ApplicationInfoForProtocol> ApplicationInfoFor(string path, string name)
    {
        if (name.Length == 0)
        {
            return Task.FromException<ApplicationInfoForProtocol>(new InvalidOperationException("Unable to retrieve display name of app"));
        }

        string normalized = NormalizeIconPath(path);
        return Task.Run(() => new ApplicationInfoForProtocol(FileIconImage(LookupFileIcon(normalized, FileIconSize.Large)), normalized, name));
    }

    private static PlatformNotSupportedException ProtocolUnsupported()
        => new("Protocol clients are implemented on Windows only.");
}
