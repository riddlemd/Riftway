using Riftway.Cef.Platform;

namespace Riftway;

public sealed unsafe partial class RiftwayApp
{
    private AppPathService? _pathService;

    // Built on first use: field initialisers cannot reach Name.
    private AppPathService Paths => _pathService ??= new AppPathService(() => Name, DefaultPath, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

    /// <summary>A directory or file by Electron's name: <c>home</c>, <c>appData</c>, <c>userData</c>,
    /// <c>sessionData</c>, <c>userCache</c>, <c>cache</c>, <c>temp</c>, <c>exe</c>, <c>module</c>, <c>assets</c>
    /// (Windows, Linux), <c>desktop</c>, <c>userDesktop</c>, <c>documents</c>, <c>downloads</c>, <c>music</c>,
    /// <c>pictures</c>, <c>videos</c>, <c>recent</c> (Windows), <c>logs</c>, <c>crashDumps</c>. <c>userData</c>,
    /// <c>userCache</c>, <c>logs</c>, <c>crashDumps</c> and <c>recent</c> are created when first asked for, and every answer
    /// is kept until the next <see cref="SetPath"/>, as Chromium's PathService does. Electron's <c>app.getPath()</c>.</summary>
    /// <exception cref="ArgumentException">An unknown name, one this OS does not have, or a directory that cannot be
    /// created ("Failed to get 'name' path").</exception>
    public string GetPath(string name) => Paths.Get(name);

    /// <summary>Overrides <see cref="GetPath"/>'s <paramref name="name"/>; the directory need not exist and is not made.
    /// Set <c>userData</c> or <c>sessionData</c> before <see cref="Run"/> to move the session data, as Electron requires
    /// before <c>ready</c>. Electron's <c>app.setPath()</c>.</summary>
    /// <exception cref="ArgumentException">A relative path ("Path must be absolute") or an unknown name ("Failed to set
    /// path").</exception>
    public void SetPath(string name, string path) => Paths.Set(name, path);

    /// <summary>Sets <c>logs</c> to <paramref name="path"/>, or with none to the default: <c>~/Library/Logs/</c> and the
    /// bundle's <c>CFBundleName</c> on macOS, <c>userData/logs</c> elsewhere; the directory is made. Electron's
    /// <c>app.setAppLogsPath()</c>.</summary>
    /// <exception cref="ArgumentException">A relative path ("Path must be absolute").</exception>
    public void SetAppLogsPath(string? path = null)
    {
        if (!string.IsNullOrEmpty(path) && !Path.IsPathFullyQualified(path)) throw new ArgumentException("Path must be absolute", nameof(path));
        if (string.IsNullOrEmpty(path))
            path = OperatingSystem.IsMacOS() ? MacLogsDirectory(MacApp.BundleName()) : Path.Combine(GetPath("userData"), "logs");
        Paths.Override("logs", path);
    }

    // base::MakeAbsoluteFilePath: realpath on POSIX, _wfullpath on Windows.
    internal static string? RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        var resolved = realpath(path, 0);
        if (resolved == 0) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(resolved); }
        finally { System.Runtime.InteropServices.NativeMemory.Free((void*)resolved); }
    }

    [System.Runtime.InteropServices.DllImport("libc", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint realpath(string path, nint resolved);

    // Electron's GetDefaultAppLogPath on macOS formats the name with %@, which prints a missing one as (null).
    internal static string MacLogsDirectory(string? bundleName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", bundleName ?? "(null)");

    // Chromium's PathService answers for the names that do not derive from another, as Electron reads them.
    internal static string? DefaultPath(string name, string appName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = AppDataDirectory();
        string UserData() => Path.Combine(appData, appName);
        return name switch
        {
            "home" => home,
            "appData" => appData,
            "userData" or "sessionData" => UserData(),
            "cache" => CacheDirectory(Environment.GetEnvironmentVariable, home),
            "userCache" => Path.Combine(CacheDirectory(Environment.GetEnvironmentVariable, home), appName),
            "temp" => Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            "exe" => Environment.ProcessPath,
            "module" => CefLayout.MainProcessLibrary(),
            "assets" => OperatingSystem.IsMacOS() ? null : AssetsDirectory(CefLayout.MainProcessLibrary(), Environment.ProcessPath, mac: false),
            "desktop" or "userDesktop" => UserDirectory("DESKTOP", "Desktop", Environment.SpecialFolder.DesktopDirectory),
            "documents" => UserDirectory("DOCUMENTS", "Documents", Environment.SpecialFolder.MyDocuments),
            "downloads" => OperatingSystem.IsWindows() ? WindowsFileDialog.DownloadsFolder() : UserDirectory("DOWNLOAD", "Downloads", null),
            "music" => UserDirectory("MUSIC", "Music", Environment.SpecialFolder.MyMusic),
            "pictures" => UserDirectory("PICTURES", "Pictures", Environment.SpecialFolder.MyPictures),
            "videos" => UserDirectory("VIDEOS", OperatingSystem.IsMacOS() ? "Movies" : "Videos", Environment.SpecialFolder.MyVideos),
            "recent" => OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Recent) : null,
            "logs" => OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Logs", appName) : Path.Combine(UserData(), "logs"),
            "crashDumps" => Path.Combine(UserData(), "Crashpad"),
            _ => null,
        };
    }

    // base::DIR_CACHE: ~/Library/Caches on macOS, $XDG_CACHE_HOME else ~/.cache on Linux; Electron's Windows "cache" is
    // the roaming AppData.
    internal static string CacheDirectory(Func<string, string?> environment, string home)
    {
        if (OperatingSystem.IsWindows()) return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Caches");
        return XdgDirectory(environment("XDG_CACHE_HOME"), home, ".cache");
    }

    // base::nix::GetXDGDirectory: any non-empty value, else ~/<fallback>, without trailing separators.
    internal static string XdgDirectory(string? value, string home, string fallback)
    {
        var path = string.IsNullOrEmpty(value) ? Path.Combine(home, fallback) : value;
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    // base::DIR_ASSETS as libcef, which holds Chromium's base, answers it: DIR_MODULE, except that a bundled macOS
    // app gets the framework's Resources (CEF overrides FrameworkBundlePath to its framework).
    internal static string AssetsDirectory(string library, string? exePath, bool mac)
    {
        var module = Path.GetDirectoryName(library)!;
        if (!mac || !IsInAppBundle(exePath)) return module;
        return Path.Combine(module, "Resources");
    }

    // base::apple::AmIBundled: the exe sits at <X>.app/Contents/MacOS/<exe>.
    private static bool IsInAppBundle(string? exePath)
    {
        var macOS = Path.GetDirectoryName(exePath);
        var contents = Path.GetDirectoryName(macOS);
        return Path.GetFileName(macOS) == "MacOS" && Path.GetFileName(contents) == "Contents"
            && Path.GetFileName(Path.GetDirectoryName(contents))?.EndsWith(".app", StringComparison.Ordinal) == true;
    }

    // ~/Library/Application Support, %APPDATA%, or $XDG_CONFIG_HOME with ~/.config.
    internal static string AppDataDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux()) return XdgDirectory(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), home, ".config");
        // Without Create, the folder is "" when it does not exist yet.
        return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
    }

    // Linux: the XDG user directory, as Chromium's xdg_user_dir_lookup; macOS: the home folder of that name; Windows:
    // the known folder.
    private static string UserDirectory(string xdgKey, string fallbackName, Environment.SpecialFolder? windowsFolder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return windowsFolder is { } folder ? Environment.GetFolderPath(folder) : Path.Combine(home, fallbackName);
        if (OperatingSystem.IsLinux() && XdgUserDirectory(xdgKey, home) is { } xdg) return xdg;
        return Path.Combine(home, fallbackName);
    }

    // user-dirs.dirs lines read XDG_<KEY>_DIR="$HOME/Name" or an absolute path; anything else is ignored, as by xdg-user-dir.
    internal static string? XdgUserDirectory(string key, string home)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        var file = Path.Combine(config, "user-dirs.dirs");
        if (!File.Exists(file)) return null;
        string? found = null;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            var prefix = $"XDG_{key}_DIR=";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var value = line[prefix.Length..];
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"') continue;
            value = value[1..^1];
            if (value.StartsWith("$HOME", StringComparison.Ordinal)) value = home + value[5..];
            else if (!value.StartsWith('/')) continue;
            found = value.TrimEnd('/');
            if (found.Length == 0) found = "/";
        }
        return found;
    }
}

/// <summary>Chromium's PathService as Electron registers its providers: overrides, a cache of answers that any
/// override empties, and the directories its providers make.</summary>
internal sealed class AppPathService(Func<string> appName, Func<string, string, string?> basePath, bool mac, bool windows)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public string Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
            return IsPathName(name, mac, windows) && LookUp(name) is { } path ? path : throw new ArgumentException($"Failed to get '{name}' path", nameof(name));
    }

    // App::SetPath: OverrideAndCreateIfNeeded with is_absolute and no create, so a missing directory is accepted.
    public void Set(string name, string path)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Path must be absolute", nameof(path));
        if (!IsPathName(name, mac, windows)) throw new ArgumentException("Failed to set path", nameof(name));
        lock (_gate)
        {
            _cache.Clear();
            _overrides[name] = path;
        }
    }

    // PathService::Override: the directory is made and resolved to a real path; a failure leaves the old answer.
    public bool Override(string name, string path)
    {
        if (!CreateDirectory(path) || RiftwayApp.RealPath(path) is not { } real) return false;
        lock (_gate)
        {
            _cache.Clear();
            _overrides[name] = real;
        }
        return true;
    }

    // Electron's GetPathConstant: "assets" is not on macOS, "recent" only on Windows.
    internal static bool IsPathName(string name, bool mac, bool windows) => name switch
    {
        "appData" or "cache" or "crashDumps" or "desktop" or "documents" or "downloads" or "exe" or "home" or "logs" or "module"
            or "music" or "pictures" or "sessionData" or "temp" or "userCache" or "userData" or "userDesktop" or "videos" => true,
        "assets" => !mac,
        "recent" => windows,
        _ => false,
    };

    // PathService::Get: the cache, then overrides, then the providers.
    private string? LookUp(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        var path = _overrides.TryGetValue(name, out var set) ? set : Provide(name);
        if (path is not null) _cache[name] = path;
        return path;
    }

    // ElectronPathProvider over Chromium's base and chrome providers; null when the path cannot be had.
    private string? Provide(string name)
    {
        string? Under(string parent, params string[] parts) => LookUp(parent) is { } p ? Path.Combine([p, .. parts]) : null;
        var (path, create) = name switch
        {
            "userData" => (Under("appData", appName()), true),
            "crashDumps" => (Under("userData", "Crashpad"), true),
            "sessionData" => (LookUp("userData"), false),
            "userCache" => (Under("cache", appName()), true),
            "logs" => (mac ? Under("home", "Library", "Logs", appName()) : Under("userData", "logs"), true),
            "recent" => (basePath(name, appName()), true),
            "userDesktop" => (LookUp("desktop"), false),
            _ => (basePath(name, appName()), false),
        };
        return path is not null && create && !CreateDirectory(path) ? null : path;
    }

    private static bool CreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
