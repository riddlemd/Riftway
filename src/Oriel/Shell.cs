using System.Text;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Options for <see cref="Shell.OpenExternalAsync"/>; Electron's <c>options</c>.</summary>
public sealed record OpenExternalOptions
{
    /// <summary>macOS: bring the opened application to the front. Electron's <c>activate</c>.</summary>
    public bool Activate { get; init; } = true;

    /// <summary>Windows: the working directory the handler starts in. Electron's <c>workingDirectory</c>.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Windows: ask the shell to log the launch for its usage-based ranking (<c>SEE_MASK_FLAG_LOG_USAGE</c>).
    /// Electron's <c>logUsage</c>.</summary>
    public bool LogUsage { get; init; }
}

/// <summary>How <see cref="Shell.WriteShortcutLink(string, ShortcutLinkOperation, ShortcutDetails)"/> treats an existing file. Electron's <c>operation</c>.</summary>
public enum ShortcutLinkOperation
{
    /// <summary>Creates a new shortcut and its folder, overwriting any file there. Electron's <c>create</c>, which is also
    /// what an empty or unknown operation string means there.</summary>
    Create,

    /// <summary>Changes the given fields of an existing shortcut only. Electron's <c>update</c>.</summary>
    Update,

    /// <summary>Overwrites an existing shortcut, keeping its arguments unless <see cref="ShortcutDetails.Args"/> is given;
    /// fails if there is no shortcut to load. Electron's <c>replace</c>.</summary>
    Replace,
}

/// <summary>A Windows shortcut's fields; Electron's <c>ShortcutDetails</c>. Null fields are not written, so
/// <see cref="ShortcutLinkOperation.Update"/> leaves them alone. <see cref="Shell.ReadShortcutLink"/> fills every field,
/// unset text as "".</summary>
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

    /// <summary>The icon's file: a .ico, .exe or .dll; written only together with <see cref="IconIndex"/>. Electron's
    /// <c>icon</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>Which icon in <see cref="Icon"/>; written only together with it. Electron's <c>iconIndex</c>.</summary>
    public int? IconIndex { get; init; }

    /// <summary>The App User Model ID the shortcut starts the app with. Electron's <c>appUserModelId</c>.</summary>
    public string? AppUserModelId { get; init; }

    /// <summary>The CLSID that activates the app from a toast notification; reads as <see cref="Guid.Empty"/> when the
    /// shortcut has none. Electron's <c>toastActivatorClsid</c>.</summary>
    public Guid? ToastActivatorClsid { get; init; }
}

/// <summary>The desktop's own handling of files and URLs. Electron's <c>shell</c>.</summary>
public static class Shell
{
    private static readonly IShellBackend s_backend = OperatingSystem.IsWindows() ? new WindowsShellBackend()
        : OperatingSystem.IsLinux() ? new LinuxShellBackend()
        : OperatingSystem.IsMacOS() ? new MacShellBackend()
        : new UnsupportedShellBackend();

    /// <summary>Opens <paramref name="url"/> with the desktop's handler for its scheme. Electron's <c>shell.openExternal()</c>.</summary>
    /// <remarks>As in Electron, the URL is canonicalized and handed on with no scheme check. Windows escapes and quotes it
    /// for the shell, and a string that is not a URL goes as an empty one, which the shell refuses. Linux starts
    /// <c>xdg-email</c> for <c>mailto:</c>, else <c>xdg-open</c>, and does not wait for it, so only a failure to start
    /// rejects; <paramref name="options"/> do nothing there. macOS escapes what NSURL refuses and asks NSWorkspace, with
    /// <see cref="OpenExternalOptions.Activate"/>, and resolves once it has opened the URL.</remarks>
    /// <exception cref="InvalidOperationException">Windows: the shell refused, <c>Failed to open: </c> and the system's
    /// message. Linux: the handler could not be started, <c>Failed to open path</c>. macOS: <c>Invalid URL</c>, <c>No
    /// application found to open URL</c> or <c>Failed to open URL</c>. Electron's rejections.</exception>
    public static Task OpenExternalAsync(string url, OpenExternalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        return s_backend.OpenExternalAsync(url, options ?? new OpenExternalOptions());
    }

    /// <summary>Opens a file or folder the way the desktop does; an empty string, or Electron's error text: Windows'
    /// <c>Failed to open path</c>, Linux's <c>Failed to launch xdg-open</c>, or macOS's <c>Failed to open path</c> (and
    /// <c>Invalid path</c> for ""). Electron's <c>shell.openPath()</c>.</summary>
    /// <remarks>On Linux <c>xdg-open</c> is not waited for, so a path it cannot open still gives "".</remarks>
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
    /// <remarks>On Linux this runs the tool Electron picks: <c>ELECTRON_TRASH</c>, <c>kioclient5</c> or <c>kioclient</c>
    /// under KDE, else <c>gio trash</c>. On macOS it is NSFileManager's <c>trashItemAtURL:</c>.</remarks>
    /// <exception cref="IOException">It could not be moved, with Electron's rejection message; on Windows that includes an
    /// item too big for the Recycle Bin (<c>Operation was aborted</c>), which is left in place rather than deleted; on
    /// Linux any failure of the tool is <c>Failed to move item to trash</c>; on macOS the message is the system's
    /// <c>localizedDescription</c>, or <c>Invalid file path: </c> for "".</exception>
    public static Task TrashItemAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return s_backend.TrashItemAsync(path);
    }

    /// <summary>Plays the system's alert sound. Electron's <c>shell.beep()</c>.</summary>
    public static void Beep() => s_backend.Beep();

    /// <summary>Writes a Windows shortcut (.lnk); false if it could not. Electron's <c>shell.writeShortcutLink()</c>.</summary>
    /// <exception cref="ArgumentException">Creating or replacing a shortcut with no <see cref="ShortcutDetails.Target"/>,
    /// where Chromium stops the process (<c>NOTREACHED</c>).</exception>
    /// <exception cref="PlatformNotSupportedException">Not Windows, where Electron has no such method.</exception>
    public static bool WriteShortcutLink(string shortcutPath, ShortcutLinkOperation operation, ShortcutDetails options)
    {
        ArgumentNullException.ThrowIfNull(shortcutPath);
        ArgumentNullException.ThrowIfNull(options);
        return s_backend.WriteShortcutLink(shortcutPath, operation, options);
    }

    /// <summary>Writes a Windows shortcut (.lnk), creating it. Electron's <c>shell.writeShortcutLink(path, options)</c>.</summary>
    public static bool WriteShortcutLink(string shortcutPath, ShortcutDetails options) => WriteShortcutLink(shortcutPath, ShortcutLinkOperation.Create, options);

    /// <summary>Reads a Windows shortcut (.lnk). Electron's <c>shell.readShortcutLink()</c>.</summary>
    /// <exception cref="IOException">The file is not a shortcut that can be read: Electron's <c>Failed to read shortcut
    /// link</c>.</exception>
    /// <exception cref="PlatformNotSupportedException">Not Windows, where Electron has no such method.</exception>
    public static ShortcutDetails ReadShortcutLink(string shortcutPath)
    {
        ArgumentNullException.ThrowIfNull(shortcutPath);
        return s_backend.ReadShortcutLink(shortcutPath);
    }
}

internal static class ShortcutRules
{
    internal static WindowsShortcutOperation ToWindows(ShortcutLinkOperation operation) => operation switch
    {
        ShortcutLinkOperation.Update => WindowsShortcutOperation.UpdateExisting,
        ShortcutLinkOperation.Replace => WindowsShortcutOperation.ReplaceExisting,
        _ => WindowsShortcutOperation.CreateAlways,
    };

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
        var file = ShellRules.ExternalFile(url);
        if (await WindowsSta.Run(() => WindowsShellOps.OpenExternal(file, options.WorkingDirectory, options.LogUsage)) is { } error)
            throw new InvalidOperationException(error);
    }

    public Task<string> OpenPathAsync(string path) => WindowsSta.Run(() => WindowsShellOps.OpenPath(path));

    public void ShowItemInFolder(string fullPath) => _ = WindowsSta.Run(() => { WindowsShellOps.ShowItemInFolder(fullPath); return true; });

    public async Task TrashItemAsync(string path)
    {
        if (await WindowsSta.Run(() => WindowsShellOps.Trash(path)) is { } error) throw new IOException(error);
    }

    public void Beep() => WindowsShellOps.Beep();

    public bool WriteShortcutLink(string shortcutPath, ShortcutLinkOperation operation, ShortcutDetails options)
    {
        if (operation != ShortcutLinkOperation.Update && options.Target is null)
            throw new ArgumentException("Creating or replacing a shortcut needs a Target.", nameof(options));
        return WindowsSta.Run(() => WindowsShellOps.WriteShortcut(shortcutPath, ShortcutRules.ToWindows(options), ShortcutRules.ToWindows(operation))).GetAwaiter().GetResult();
    }

    public ShortcutDetails ReadShortcutLink(string shortcutPath)
        => ShortcutRules.FromWindows(WindowsSta.Run(() => WindowsShellOps.ReadShortcut(shortcutPath)).GetAwaiter().GetResult());
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class LinuxShellBackend : IShellBackend
{
    public Task OpenExternalAsync(string url, OpenExternalOptions options)
    {
        var spec = Gurl.Spec(url);
        return Task.Run(() =>
        {
            if (LinuxShellOps.OpenExternal(spec) is { } error) throw new InvalidOperationException(error);
        });
    }

    public Task<string> OpenPathAsync(string path) => Task.Run(() => LinuxShellOps.OpenPath(path));

    public void ShowItemInFolder(string fullPath) => LinuxShowItemHelper.Instance.ShowItemInFolder(fullPath);

    public Task TrashItemAsync(string path) => Task.Run(() =>
    {
        if (!LinuxShellOps.MoveItemToTrash(path)) throw new IOException("Failed to move item to trash");
    });

    // GDK belongs to CEF's UI thread. Before Ready GTK has no display, where Electron's beep does nothing too.
    public void Beep()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true }) return;
        try { CefThread.RunOnUi(LinuxShellOps.Beep); }
        catch (InvalidOperationException) { }
    }

    public bool WriteShortcutLink(string shortcutPath, ShortcutLinkOperation operation, ShortcutDetails options) => throw WindowsOnly();

    public ShortcutDetails ReadShortcutLink(string shortcutPath) => throw WindowsOnly();

    private static PlatformNotSupportedException WindowsOnly() => new("Shortcut links are Windows only, as in Electron.");
}

/// <summary>Electron's <c>platform_util_mac.mm</c>. The NSWorkspace calls run on the main thread, which is CEF's UI
/// thread; before the app is ready they run on the caller's thread (DRIFT H9).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class MacShellBackend : IShellBackend
{
    public Task OpenExternalAsync(string url, OpenExternalOptions options)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(string error)
        {
            if (error.Length == 0) done.TrySetResult();
            else done.TrySetException(new InvalidOperationException(error));
        }
        if (ShellRules.NSURLString(Gurl.Spec(url)) is not { } escaped) Finish("Invalid URL");
        else OnMain(() => MacShellOps.OpenExternal(escaped, options.Activate, Finish), done);
        return done.Task;
    }

    public Task<string> OpenPathAsync(string path)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnMain(() => done.TrySetResult(MacShellOps.OpenPath(path)), done);
        return done.Task;
    }

    public void ShowItemInFolder(string fullPath)
    {
        var path = ShellRules.MacAbsolutePath(fullPath, MacShellOps.RealPath);
        OnMain(() =>
        {
            if (!MacShellOps.ShowItemInFolder(path)) Console.Error.WriteLine($"[Oriel] NSWorkspace failed to select file {path}");
        }, null);
    }

    // Electron trashes on a thread-pool thread too.
    public Task TrashItemAsync(string path) => Task.Run(() =>
    {
        if (MacShellOps.Trash(path) is { } error) throw new IOException(error);
    });

    public void Beep() => OnMain(MacShellOps.Beep, null);

    public bool WriteShortcutLink(string shortcutPath, ShortcutLinkOperation operation, ShortcutDetails options) => throw WindowsOnly();

    public ShortcutDetails ReadShortcutLink(string shortcutPath) => throw WindowsOnly();

    private static PlatformNotSupportedException WindowsOnly() => new("Shortcut links are Windows only, as in Electron.");

    private static void OnMain(Action action, object? completion)
    {
        void Run()
        {
            try { action(); }
            catch (Exception e) when (completion is not null)
            {
                switch (completion)
                {
                    case TaskCompletionSource t: t.TrySetException(e); break;
                    case TaskCompletionSource<string> t: t.TrySetException(e); break;
                }
            }
        }
        if (OrielApp.CurrentOrNull is { IsReady: true }) CefThread.RunOnUi(Run);
        else Run();
    }
}

/// <summary>Electron's string handling around the shell calls: pure code.</summary>
internal static class ShellRules
{
    /// <summary>The string Chromium's <c>net::NSURLWithGURL</c> hands <c>URLWithString:</c> for a canonical
    /// <paramref name="spec"/>: path, query and ref escaped by <c>base::EscapeNSURLPrecursor</c>; null for "", an invalid
    /// GURL.</summary>
    internal static string? NSURLString(string spec)
    {
        if (spec.Length == 0) return null;
        var hash = spec.IndexOf('#');
        var beforeRef = hash < 0 ? spec : spec[..hash];
        var question = beforeRef.IndexOf('?');
        var beforeQuery = question < 0 ? beforeRef : beforeRef[..question];
        var colon = beforeQuery.IndexOf(':');
        var pathStart = colon + 1;
        // An authority is never part of the path; GURL parses one after "//" for every scheme.
        if (string.CompareOrdinal(beforeQuery, pathStart, "//", 0, 2) == 0)
        {
            var slash = beforeQuery.IndexOf('/', pathStart + 2);
            pathStart = slash < 0 ? beforeQuery.Length : slash;
        }
        var escaped = new StringBuilder(beforeQuery[..pathStart]).Append(EscapeNSURLPrecursor(beforeQuery[pathStart..]));
        if (question >= 0) escaped.Append('?').Append(EscapeNSURLPrecursor(beforeRef[(question + 1)..]));
        if (hash >= 0) escaped.Append('#').Append(EscapeNSURLPrecursor(spec[(hash + 1)..]));
        return escaped.ToString();
    }

    /// <summary>Chromium's <c>EscapeNSURLPrecursor</c>: UTF-8 bytes percent-encoded in upper-case hex when they are
    /// controls, space, non-ASCII or one of <c>"#%&lt;&gt;[\]^`{|}</c> and DEL, except an existing valid <c>%XX</c>.</summary>
    internal static string EscapeNSURLPrecursor(string text)
    {
        const string Escaped = "\"#%<>[\\]^`{|}";
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == '%' && i + 2 < bytes.Length && char.IsAsciiHexDigit((char)bytes[i + 1]) && char.IsAsciiHexDigit((char)bytes[i + 2]))
                result.Append('%');
            else if (b <= 0x20 || b >= 0x7F || Escaped.Contains((char)b))
                result.Append('%').Append(b.ToString("X2"));
            else
                result.Append((char)b);
        }
        return result.ToString();
    }

    /// <summary>The path Electron's Mac <c>ShowItemInFolder</c> selects: <paramref name="path"/> when absolute, else
    /// <c>base::MakeAbsoluteFilePath</c> (<paramref name="realPath"/>), which is "" when the path does not resolve.</summary>
    internal static string MacAbsolutePath(string path, Func<string, string?> realPath)
        => path.StartsWith('/') ? path : realPath(path) ?? "";

    /// <summary>What Electron hands <c>ShellExecuteEx</c> for <c>openExternal</c>: the URL's canonical spec, escaped by
    /// <c>base::EscapeExternalHandlerValue</c>, in double quotes.</summary>
    internal static string ExternalFile(string url) => "\"" + EscapeExternalHandlerValue(Gurl.Spec(url)) + "\"";

    /// <summary>Chromium's <c>EscapeExternalHandlerValue</c>: UTF-8 bytes percent-encoded in upper-case hex, except
    /// alphanumerics, <c>;/?:@&amp;=+$,!'()*-._~#[]</c> and an existing valid <c>%XX</c>.</summary>
    internal static string EscapeExternalHandlerValue(string text)
    {
        const string Kept = ";/?:@&=+$,!'()*-._~#[]";
        var bytes = Encoding.UTF8.GetBytes(text);
        var escaped = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == '%' && i + 2 < bytes.Length && char.IsAsciiHexDigit((char)bytes[i + 1]) && char.IsAsciiHexDigit((char)bytes[i + 2]))
                escaped.Append('%');
            else if (char.IsAsciiLetterOrDigit((char)b) || Kept.Contains((char)b))
                escaped.Append((char)b);
            else
                escaped.Append('%').Append(b.ToString("X2"));
        }
        return escaped.ToString();
    }
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
