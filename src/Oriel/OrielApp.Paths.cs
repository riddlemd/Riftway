using Oriel.Cef.Platform;

namespace Oriel;

public sealed unsafe partial class OrielApp
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

    /// <summary>A directory or file by Electron's name: <c>home</c>, <c>appData</c>, <c>userData</c>,
    /// <c>sessionData</c>, <c>temp</c>, <c>exe</c>, <c>module</c>, <c>assets</c>, <c>desktop</c>, <c>documents</c>, <c>downloads</c>,
    /// <c>music</c>, <c>pictures</c>, <c>videos</c>, <c>recent</c> (Windows), <c>logs</c>, <c>crashDumps</c>. Electron's
    /// <c>app.getPath()</c>.</summary>
    /// <exception cref="ArgumentException">An unknown name, or one this OS does not have.</exception>
    public string GetPath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_paths)
            if (_paths.TryGetValue(name, out var set)) return set;
        // Session data lives in userData unless set apart, wherever userData was moved.
        if (name == "sessionData") return GetPath("userData");
        return DefaultPath(name, Name) ?? throw new ArgumentException($"Failed to get '{name}' path", nameof(name));
    }

    /// <summary>Overrides <see cref="GetPath"/>'s <paramref name="name"/>. Set <c>userData</c> or <c>sessionData</c> before
    /// <see cref="Run"/> to move the session data, as Electron requires before <c>ready</c>. Electron's <c>app.setPath()</c>.</summary>
    /// <exception cref="ArgumentException">An unknown name, a relative path, or a directory that does not exist.</exception>
    public void SetPath(string name, string path)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        if (DefaultPath(name, Name) is null) throw new ArgumentException($"Failed to set path: unknown name '{name}'", nameof(name));
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Path must be absolute", nameof(path));
        if (name != "exe" && name != "module" && !Directory.Exists(path))
            throw new ArgumentException($"Failed to set path: the directory {path} does not exist", nameof(path));
        lock (_paths) _paths[name] = path;
    }

    // Chromium's PathService answers, as Electron reads them.
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
            "temp" => Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            "exe" => Environment.ProcessPath,
            "module" => CefLayout.MainProcessLibrary(),
            "assets" => AssetsDirectory(CefLayout.MainProcessLibrary(), Environment.ProcessPath, OperatingSystem.IsMacOS()),
            "desktop" => UserDirectory("DESKTOP", "Desktop", Environment.SpecialFolder.DesktopDirectory),
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
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config))
            return config;
        // Without Create, Linux answers "" when ~/.config does not exist yet.
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
