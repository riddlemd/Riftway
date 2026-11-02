using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>The app that handles a protocol. Electron's <c>app.getApplicationInfoForProtocol()</c> result.</summary>
/// <param name="Icon">The app's icon.</param>
/// <param name="Path">The app's executable (macOS: its bundle).</param>
/// <param name="Name">The app's display name.</param>
public sealed record ApplicationInfoForProtocol(NativeImage Icon, string Path, string Name);

public sealed unsafe partial class RiftwayApp
{
    /// <summary>Registers this app as the handler of <paramref name="protocol"/> (for example <c>myapp</c>).
    /// Electron's <c>app.setAsDefaultProtocolClient</c>.</summary>
    /// <param name="protocol">The scheme, without <c>://</c>: an ASCII letter, then letters, digits, <c>+</c>, <c>-</c> or <c>.</c>.</param>
    /// <param name="path">Windows: the executable to launch; null is the running process.</param>
    /// <param name="args">Windows: arguments placed before the URL on the command line.</param>
    /// <returns>False for an invalid scheme or when registration fails. macOS: LaunchServices names the main bundle's
    /// identifier, false without one. Linux: the desktop file <see cref="SetDesktopName"/> names becomes the
    /// <c>x-scheme-handler</c> default, false when it is not installed.</returns>
    public bool SetAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => SetProtocolClient(protocol, path, args);

    internal static bool SetProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (OperatingSystem.IsWindows()) return WindowsAppRegistry.SetAsDefaultProtocolClient(protocol, path, args);
        if (!IsValidProtocolScheme(protocol)) return false;
        if (OperatingSystem.IsMacOS()) return MacApp.BundleIdentifier() is { } id && MacApp.SetDefaultHandler(protocol, id);
        if (OperatingSystem.IsLinux()) return LinuxLauncherEntry.DesktopName() is { } desktop && LinuxAppInfo.SetDefaultForScheme(desktop, protocol);
        return false;
    }

    /// <summary>Removes this app's registration for <paramref name="protocol"/>. Electron's
    /// <c>app.removeAsDefaultProtocolClient</c>.</summary>
    /// <returns>Windows: true when the registration was this app's and is gone, and also when nothing or another app is
    /// registered; false for an invalid scheme or when the registry refuses. macOS: false unless this app is the
    /// default, which then passes to the first other app that can open the scheme, or to none. Linux: always false.</returns>
    public bool RemoveAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => RemoveProtocolClient(protocol, path, args);

    internal static bool RemoveProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (OperatingSystem.IsWindows()) return WindowsAppRegistry.RemoveAsDefaultProtocolClient(protocol, path, args);
        if (!OperatingSystem.IsMacOS()) return false;
        if (MacApp.BundleIdentifier() is not { } id || !IsProtocolClient(protocol, path, args)) return false;
        if (MacApp.HandlerBundleIds(protocol) is not { Count: > 0 } handlers) return false;
        return MacApp.SetDefaultHandler(protocol, MacFallbackHandler(id, handlers));
    }

    // Electron's macOS RemoveAsDefaultProtocolClient: the first other app that can open the scheme, else "None".
    internal static string MacFallbackHandler(string ownId, IReadOnlyList<string?> handlers)
        => handlers.FirstOrDefault(h => h is not null && h != ownId) ?? "None";

    /// <summary>Whether this app is the handler of <paramref name="protocol"/>. Electron's
    /// <c>app.isDefaultProtocolClient</c>.</summary>
    /// <remarks>Windows: the registered command is exactly this app's (case-sensitive). macOS: the default app's bundle
    /// identifier is the main bundle's, ignoring case. Linux: the default <c>x-scheme-handler</c> app is the desktop
    /// file <see cref="SetDesktopName"/> names.</remarks>
    public bool IsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => IsProtocolClient(protocol, path, args);

    internal static bool IsProtocolClient(string protocol, string? path, IReadOnlyList<string>? args)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (OperatingSystem.IsWindows()) return WindowsAppRegistry.IsDefaultProtocolClient(protocol, path, args);
        if (!IsValidProtocolScheme(protocol)) return false;
        if (OperatingSystem.IsMacOS())
            return MacApp.BundleIdentifier() is { } id && string.Equals(MacApp.DefaultHandlerBundleId(protocol), id, StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux()) return LinuxLauncherEntry.DesktopName() is { } desktop && LinuxAppInfo.DefaultAppId(protocol) == desktop;
        return false;
    }

    /// <summary>The display name of the app that opens <paramref name="url"/>'s scheme, or an empty string when there is
    /// none. Electron's <c>app.getApplicationNameForProtocol</c>.</summary>
    public string GetApplicationNameForProtocol(string url)
        => ProtocolNameFor(url);

    internal static string ProtocolNameFor(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (OperatingSystem.IsWindows()) return WindowsAppRegistry.GetAppInfoForProtocol(url, WindowsAppRegistry.AssocstrFriendlyAppName);
        if (OperatingSystem.IsMacOS()) return MacApp.AppPathForUrl(url) is { } app ? MacApp.DisplayName(app) : "";
        if (OperatingSystem.IsLinux()) return LinuxAppInfo.DefaultApp(PosixSchemeOf(url))?.Name ?? "";
        return "";
    }

    /// <summary>The executable (macOS: the bundle), display name and icon of the app that opens <paramref name="url"/>'s
    /// scheme. Electron's <c>app.getApplicationInfoForProtocol</c>.</summary>
    /// <remarks>The task faults with <see cref="InvalidOperationException"/>: "Unable to retrieve installation path to
    /// app", "Unable to retrieve display name of app" or "Failed to get file icon.".</remarks>
    public Task<ApplicationInfoForProtocol> GetApplicationInfoForProtocol(string url)
        => ProtocolInfoFor(url);

    internal static Task<ApplicationInfoForProtocol> ProtocolInfoFor(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (OperatingSystem.IsMacOS())
        {
            if (MacApp.AppPathForUrl(url) is not { } app) return NoInstallPath();
            var icon = NativeImage.CreateEmpty();
            foreach (var (png, scale) in MacApp.IconForFile(app, [1.0, 2.0]))
                icon.AddRepresentation(new AddRepresentationOptions { Buffer = png, ScaleFactor = scale });
            return Task.FromResult(new ApplicationInfoForProtocol(icon, app, MacApp.DisplayName(app)));
        }
        if (OperatingSystem.IsLinux())
        {
            if (LinuxAppInfo.DefaultApp(PosixSchemeOf(url)) is not { } info || info.Executable is not { } executable) return NoInstallPath();
            if (info.Name.Length == 0) return Task.FromException<ApplicationInfoForProtocol>(new InvalidOperationException("Unable to retrieve display name of app"));
            if (info.Icon is not { } pixels) return Task.FromException<ApplicationInfoForProtocol>(new InvalidOperationException("Failed to get file icon."));
            var image = NativeImage.CreateFromBitmap(pixels.Bgra, new NativeImageBitmapOptions { Width = pixels.Width, Height = pixels.Height });
            return Task.FromResult(new ApplicationInfoForProtocol(image, executable, info.Name));
        }
        if (!OperatingSystem.IsWindows()) return NoInstallPath();

        string path = WindowsAppRegistry.GetAppInfoForProtocol(url, WindowsAppRegistry.AssocstrExecutable);
        if (path.Length == 0) return NoInstallPath();

        string name = WindowsAppRegistry.GetAppInfoForProtocol(url, WindowsAppRegistry.AssocstrFriendlyAppName);
        return ApplicationInfoFor(path, name);
    }

    private static Task<ApplicationInfoForProtocol> NoInstallPath()
        => Task.FromException<ApplicationInfoForProtocol>(new InvalidOperationException("Unable to retrieve installation path to app"));

    internal static Task<ApplicationInfoForProtocol> ApplicationInfoFor(string path, string name)
    {
        if (name.Length == 0)
        {
            return Task.FromException<ApplicationInfoForProtocol>(new InvalidOperationException("Unable to retrieve display name of app"));
        }

        string normalized = NormalizeIconPath(path);
        return Task.Run(() => new ApplicationInfoForProtocol(FileIconImage(LookupFileIcon(normalized, FileIconSize.Large)), normalized, name));
    }

    // RFC 3986 section 3.1, as Electron's Browser::IsValidProtocolScheme.
    internal static bool IsValidProtocolScheme(string scheme)
    {
        if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0])) return false;
        foreach (var c in scheme.AsSpan(1))
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
        return true;
    }

    // GURL's scheme off Windows: what precedes the first ':' after trimming control characters and spaces, lower-cased;
    // empty when that is not a scheme.
    internal static string PosixSchemeOf(string url)
    {
        var trimmed = url.Trim([.. Enumerable.Range(0, 0x21).Select(i => (char)i)]);
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) return "";
        var scheme = trimmed[..colon];
        return IsValidProtocolScheme(scheme) ? scheme.ToLowerInvariant() : "";
    }
}
