using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Options for <see cref="Shell.OpenExternalAsync"/>; Electron's <c>options</c>.</summary>
public sealed record OpenExternalOptions
{
    /// <summary>macOS: bring the opened application to the front. Electron's <c>activate</c>.</summary>
    public bool Activate { get; init; } = true;

    /// <summary>Windows: the working directory the handler starts in. Electron's <c>workingDirectory</c>.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>How <see cref="Shell.WriteShortcutLink(string, ShortcutLinkOperation, ShortcutDetails)"/> treats an existing file. Electron's <c>operation</c>.</summary>
public enum ShortcutLinkOperation
{
    /// <summary>Creates a new shortcut, overwriting one already there. Electron's <c>create</c>.</summary>
    Create,

    /// <summary>Changes the given fields of an existing shortcut only. Electron's <c>update</c>.</summary>
    Update,

    /// <summary>Overwrites an existing shortcut; fails if there is none. Electron's <c>replace</c>.</summary>
    Replace,
}

/// <summary>A Windows shortcut's fields; Electron's <c>ShortcutDetails</c>. Null fields are unset, or left alone by
/// <see cref="ShortcutLinkOperation.Update"/>.</summary>
public sealed record ShortcutDetails
{
    /// <summary>What the shortcut starts. Electron's <c>target</c>.</summary>
    public string? Target { get; init; }

    /// <summary>The working directory. Electron's <c>cwd</c>.</summary>
    public string? Cwd { get; init; }

    /// <summary>The arguments passed to the target. Electron's <c>args</c>.</summary>
    public string? Args { get; init; }

    /// <summary>The tooltip. Electron's <c>description</c>.</summary>
    public string? Description { get; init; }

    /// <summary>The icon's file: a .ico, .exe or .dll. Electron's <c>icon</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>Which icon in <see cref="Icon"/>. Electron's <c>iconIndex</c>.</summary>
    public int? IconIndex { get; init; }

    /// <summary>The App User Model ID the shortcut starts the app with. Electron's <c>appUserModelId</c>.</summary>
    public string? AppUserModelId { get; init; }

    /// <summary>The CLSID that activates the app from a toast notification. Electron's <c>toastActivatorClsid</c>.</summary>
    public Guid? ToastActivatorClsid { get; init; }
}

/// <summary>The desktop's own handling of files and URLs. Electron's <c>shell</c>.</summary>
/// <remarks>Windows only for now: macOS and Linux throw <see cref="PlatformNotSupportedException"/>.</remarks>
public static class Shell
{
    private static readonly IShellBackend s_backend = OperatingSystem.IsWindows() ? new WindowsShellBackend() : new UnsupportedShellBackend();

    /// <summary>Opens <paramref name="url"/> with the desktop's handler for its scheme. Electron's <c>shell.openExternal()</c>.</summary>
    /// <exception cref="InvalidOperationException">The URL is invalid, its scheme has no handler, or the handler failed;
    /// Electron rejects its promise.</exception>
    public static Task OpenExternalAsync(string url, OpenExternalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        return s_backend.OpenExternalAsync(url, options ?? new OpenExternalOptions());
    }

    /// <summary>Opens a file or folder the way the desktop does; the reason it failed, or an empty string.
    /// Electron's <c>shell.openPath()</c>.</summary>
    public static Task<string> OpenPathAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return s_backend.OpenPathAsync(path);
    }

    /// <summary>Shows <paramref name="fullPath"/> selected in its folder. Electron's <c>shell.showItemInFolder()</c>.</summary>
    public static void ShowItemInFolder(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        s_backend.ShowItemInFolder(fullPath);
    }

    /// <summary>Moves a file or folder to the trash. Electron's <c>shell.trashItem()</c>.</summary>
    /// <exception cref="IOException">It could not be moved; on Windows that includes an item too big for the Recycle Bin,
    /// which is left in place rather than deleted.</exception>
    public static Task TrashItemAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return s_backend.TrashItemAsync(path);
    }

    /// <summary>Plays the system's alert sound. Electron's <c>shell.beep()</c>.</summary>
    public static void Beep() => s_backend.Beep();

    /// <summary>Writes a Windows shortcut (.lnk); false if it could not. Electron's <c>shell.writeShortcutLink()</c>.</summary>
    /// <exception cref="ArgumentException">Creating or replacing a shortcut without a <see cref="ShortcutDetails.Target"/>.</exception>
    public static bool WriteShortcutLink(string shortcutPath, ShortcutLinkOperation operation, ShortcutDetails options)
    {
        ArgumentNullException.ThrowIfNull(shortcutPath);
        ArgumentNullException.ThrowIfNull(options);
        if (operation != ShortcutLinkOperation.Update && string.IsNullOrEmpty(options.Target))
            throw new ArgumentException("Creating or replacing a shortcut needs a Target.", nameof(options));
        return ShortcutRules.MayWrite(operation, File.Exists(shortcutPath)) && s_backend.WriteShortcutLink(shortcutPath, operation, options);
    }

    /// <summary>Writes a Windows shortcut (.lnk), creating it. Electron's <c>shell.writeShortcutLink(path, options)</c>.</summary>
    public static bool WriteShortcutLink(string shortcutPath, ShortcutDetails options) => WriteShortcutLink(shortcutPath, ShortcutLinkOperation.Create, options);

    /// <summary>Reads a Windows shortcut (.lnk). Electron's <c>shell.readShortcutLink()</c>.</summary>
    /// <exception cref="IOException">The file is not a shortcut that can be read; Electron throws.</exception>
    public static ShortcutDetails ReadShortcutLink(string shortcutPath)
    {
        ArgumentNullException.ThrowIfNull(shortcutPath);
        return s_backend.ReadShortcutLink(shortcutPath);
    }
}

internal static class ShortcutRules
{
    /// <summary>Electron's rule: update and replace need an existing shortcut; create writes either way.</summary>
    internal static bool MayWrite(ShortcutLinkOperation operation, bool exists) => operation == ShortcutLinkOperation.Create || exists;

    /// <summary>Update starts from the existing shortcut; create and replace start empty.</summary>
    internal static bool LoadsExisting(ShortcutLinkOperation operation) => operation == ShortcutLinkOperation.Update;

    internal static WindowsShortcut ToWindows(ShortcutDetails d) => new()
    {
        Target = d.Target,
        WorkingDirectory = d.Cwd,
        Arguments = d.Args,
        Description = d.Description,
        Icon = d.Icon,
        IconIndex = d.IconIndex,
        AppUserModelId = d.AppUserModelId,
        ToastActivatorClsid = d.ToastActivatorClsid,
    };

    internal static ShortcutDetails FromWindows(WindowsShortcut w) => new()
    {
        Target = w.Target,
        Cwd = w.WorkingDirectory,
        Args = w.Arguments,
        Description = w.Description,
        Icon = w.Icon,
        IconIndex = w.IconIndex,
        AppUserModelId = w.AppUserModelId,
        ToastActivatorClsid = w.ToastActivatorClsid,
    };
}

/// <summary>One OS's implementation of <see cref="Shell"/>.</summary>
internal interface IShellBackend
{
    Task OpenExternalAsync(string url, OpenExternalOptions options);
    Task<string> OpenPathAsync(string path);
    void ShowItemInFolder(string fullPath);
    Task TrashItemAsync(string path);
    void Beep();
    bool WriteShortcutLink(string shortcutPath, ShortcutLinkOperation operation, ShortcutDetails options);
    ShortcutDetails ReadShortcutLink(string shortcutPath);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsShellBackend : IShellBackend
{
    public async Task OpenExternalAsync(string url, OpenExternalOptions options)
    {
        if (await WindowsSta.Run(() => WindowsShellOps.OpenExternal(url, options.WorkingDirectory)) is { } error)
            throw new InvalidOperationException(error);
    }

    public Task<string> OpenPathAsync(string path) => WindowsSta.Run(() => WindowsShellOps.OpenPath(path));

    public void ShowItemInFolder(string fullPath) => _ = WindowsSta.Run(() => { WindowsShellOps.ShowItemInFolder(fullPath); return true; });

    public async Task TrashItemAsync(string path)
    {
        if (await WindowsSta.Run(() => WindowsShellOps.Trash(path)) is { } error) throw new IOException($"{error}: '{path}'.");
    }

    public void Beep() => WindowsShellOps.Beep();

    public bool WriteShortcutLink(string shortcutPath, ShortcutLinkOperation operation, ShortcutDetails options)
        => WindowsSta.Run(() => WindowsShellOps.WriteShortcut(shortcutPath, ShortcutRules.ToWindows(options), ShortcutRules.LoadsExisting(operation))).GetAwaiter().GetResult();

    public ShortcutDetails ReadShortcutLink(string shortcutPath)
        => ShortcutRules.FromWindows(WindowsSta.Run(() => WindowsShellOps.ReadShortcut(shortcutPath)).GetAwaiter().GetResult());
}

internal sealed class UnsupportedShellBackend : IShellBackend
{
    private static PlatformNotSupportedException NotYet() => new("The shell module is not implemented on this OS yet.");

    public Task OpenExternalAsync(string url, OpenExternalOptions options) => throw NotYet();
    public Task<string> OpenPathAsync(string path) => throw NotYet();
    public void ShowItemInFolder(string fullPath) => throw NotYet();
    public Task TrashItemAsync(string path) => throw NotYet();
    public void Beep() => throw NotYet();
    public bool WriteShortcutLink(string shortcutPath, ShortcutLinkOperation operation, ShortcutDetails options) => throw NotYet();
    public ShortcutDetails ReadShortcutLink(string shortcutPath) => throw NotYet();
}
