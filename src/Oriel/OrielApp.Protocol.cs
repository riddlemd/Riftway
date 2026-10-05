using Oriel.Cef.Platform;

namespace Oriel;

public sealed unsafe partial class OrielApp
{
    /// <summary>Registers this app as the handler of <paramref name="protocol"/> (for example <c>myapp</c>).
    /// Electron's <c>app.setAsDefaultProtocolClient</c>.</summary>
    /// <param name="protocol">The scheme, without <c>://</c>.</param>
    /// <param name="path">The executable to launch; defaults to the running process.</param>
    /// <param name="args">Arguments placed before the URL on the command line.</param>
    /// <returns>False for an empty protocol or when registration fails.</returns>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public bool SetAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => SetProtocolClient(protocol, path, args);

    internal static bool SetProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
        => OperatingSystem.IsWindows()
            ? WindowsAppRegistry.SetAsDefaultProtocolClient(protocol, path, args)
            : throw ProtocolUnsupported();

    /// <summary>Removes this app's registration for <paramref name="protocol"/>. Electron's
    /// <c>app.removeAsDefaultProtocolClient</c>.</summary>
    /// <returns>False when another app handles the protocol or nothing could be removed.</returns>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public bool RemoveAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => RemoveProtocolClient(protocol, path, args);

    internal static bool RemoveProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
        => OperatingSystem.IsWindows()
            ? WindowsAppRegistry.RemoveAsDefaultProtocolClient(protocol, path, args)
            : throw ProtocolUnsupported();

    /// <summary>Whether this app is the registered handler of <paramref name="protocol"/>. Electron's
    /// <c>app.isDefaultProtocolClient</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public bool IsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => IsProtocolClient(protocol, path, args);

    internal static bool IsProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
        => OperatingSystem.IsWindows()
            ? WindowsAppRegistry.IsDefaultProtocolClient(protocol, path, args)
            : throw ProtocolUnsupported();

    /// <summary>The friendly name of the app that opens <paramref name="url"/>'s scheme, or an empty string.
    /// Electron's <c>app.getApplicationNameForProtocol</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    public string GetApplicationNameForProtocol(string url)
        => ProtocolNameFor(url);

    internal static string ProtocolNameFor(string url)
        => OperatingSystem.IsWindows()
            ? WindowsAppRegistry.GetApplicationNameForProtocol(url)
            : throw ProtocolUnsupported();

    private static PlatformNotSupportedException ProtocolUnsupported()
        => new("Protocol clients are implemented on Windows only.");
}
