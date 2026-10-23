using Oriel.Cef.Interop;

namespace Oriel.Cef.Platform;

/// <summary>Where the packaging targets put libcef and the subprocess exe, per OS.</summary>
public static class CefLayout
{
    /// <summary>The subprocess exe's file name next to the app on Windows and Linux.</summary>
    public const string SubprocessName = "Oriel.Subprocess";

    private const string MacFramework = "Chromium Embedded Framework.framework";
    private const string MacLibrary = "Chromium Embedded Framework";

    /// <summary>libcef as seen from the app's main process.</summary>
    /// <exception cref="PlatformNotSupportedException">Not yet supported on this OS.</exception>
    public static string MainProcessLibrary()
    {
        var exeDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS())
            return Path.GetFullPath(Path.Combine(exeDirectory, "..", "Frameworks", MacFramework, MacLibrary));
        if (OperatingSystem.IsLinux())
            return Path.Combine(exeDirectory, "libcef.so");
        if (OperatingSystem.IsWindows())
            return Path.Combine(exeDirectory, "libcef.dll");
        throw new PlatformNotSupportedException($"Oriel does not run on {Environment.OSVersion} yet.");
    }

    /// <summary>libcef as seen from the subprocess exe.</summary>
    /// <exception cref="PlatformNotSupportedException">Not yet supported on this OS.</exception>
    public static string SubprocessLibrary()
    {
        var exeDirectory = AppContext.BaseDirectory;
        // <App>.app/Contents/Frameworks/<App> Helper.app/Contents/MacOS/<exe>: three bundles up.
        if (OperatingSystem.IsMacOS())
            return Path.GetFullPath(Path.Combine(exeDirectory, "..", "..", "..", MacFramework, MacLibrary));
        if (OperatingSystem.IsLinux())
            return Path.Combine(exeDirectory, "libcef.so");
        if (OperatingSystem.IsWindows())
            return Path.Combine(exeDirectory, "libcef.dll");
        throw new PlatformNotSupportedException($"Oriel does not run on {Environment.OSVersion} yet.");
    }

    /// <summary>This app's own bundle, for <c>cef_settings_t.main_bundle_path</c>; null off macOS.</summary>
    /// <remarks>CEF's default is the top-level bundle, so an app shipped inside another (a helper app in
    /// <c>Contents/Helpers</c>) would look for its helpers and framework in the outer app's.</remarks>
    public static string? MacMainBundlePath()
        => OperatingSystem.IsMacOS()
            // <App>.app/Contents/MacOS/<exe>: two folders up.
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."))
            : null;

    /// <summary>The subprocess exe to name in <c>cef_settings_t.browser_subprocess_path</c>, or null
    /// where CEF finds it itself (macOS looks for the <c>&lt;App&gt; Helper</c> bundles).</summary>
    public static string? SubprocessPath()
        => OperatingSystem.IsMacOS() ? null
            : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? SubprocessName + ".exe" : SubprocessName);

    /// <summary>The process's original argc/argv.</summary>
    /// <exception cref="PlatformNotSupportedException">Not yet supported on this OS.</exception>
    public static cef_main_args_native_t MainArgs()
    {
        if (OperatingSystem.IsMacOS()) return new() { posix = ProcessArgs.Mac() };
        if (OperatingSystem.IsLinux()) return new() { posix = ProcessArgs.Linux() };
        if (OperatingSystem.IsWindows()) return new() { win = ProcessArgs.Windows() };
        throw new PlatformNotSupportedException($"Oriel does not run on {Environment.OSVersion} yet.");
    }
}
