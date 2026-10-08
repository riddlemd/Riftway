using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using System.Runtime.Versioning;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A file type choice in a file dialog; Electron's <c>FileFilter</c>.</summary>
/// <param name="Name">What the user sees, e.g. <c>Images</c>.</param>
/// <param name="Extensions">Extensions without the dot, e.g. <c>png</c>; <c>*</c> for any file.</param>
public sealed record FileFilter(string Name, params IReadOnlyList<string> Extensions);

/// <summary>What an open dialog may choose; Electron's <c>properties</c> for <c>showOpenDialog</c>.</summary>
/// <remarks>macOS honours every property Electron honours there; elsewhere CEF's dialog takes only
/// <see cref="OpenFile"/>, <see cref="OpenDirectory"/> and <see cref="MultiSelections"/>, and the others throw.</remarks>
[Flags]
public enum OpenDialogProperties
{
    /// <summary>Nothing set. On macOS, as Electron's empty <c>properties</c>, neither files nor directories are
    /// chooseable; elsewhere the same as <see cref="OpenFile"/>.</summary>
    None = 0,

    /// <summary>Files can be chosen.</summary>
    OpenFile = 1 << 0,

    /// <summary>Directories can be chosen; on macOS together with files when <see cref="OpenFile"/> is also set.</summary>
    OpenDirectory = 1 << 1,

    /// <summary>Several files can be chosen.</summary>
    MultiSelections = 1 << 2,

    /// <summary>Hidden files are shown. macOS only (throws elsewhere: CEF's dialog has no option for it).</summary>
    ShowHiddenFiles = 1 << 3,

    /// <summary>The dialog can create directories (macOS). Throws elsewhere: CEF's dialog has no option for it.</summary>
    CreateDirectory = 1 << 4,

    /// <summary>Windows only in Electron: ignored on macOS; throws elsewhere.</summary>
    PromptToCreate = 1 << 5,

    /// <summary>Aliases are returned as themselves, not resolved (macOS). Throws elsewhere.</summary>
    NoResolveAliases = 1 << 6,

    /// <summary>Packages such as <c>.app</c> are browsed as directories (macOS). Throws elsewhere.</summary>
    TreatPackageAsDirectory = 1 << 7,

    /// <summary>Windows only in Electron: ignored on macOS; throws elsewhere.</summary>
    DontAddToRecent = 1 << 8,
}

/// <summary>Save dialog options; Electron's <c>properties</c> for <c>showSaveDialog</c>.</summary>
/// <remarks>macOS honours the macOS ones; elsewhere CEF's dialog takes none, and any set throws.</remarks>
[Flags]
public enum SaveDialogProperties
{
    /// <summary>Nothing set.</summary>
    None = 0,

    /// <summary>The dialog can create directories (macOS).</summary>
    CreateDirectory = 1 << 0,

    /// <summary>Hidden files are shown.</summary>
    ShowHiddenFiles = 1 << 1,

    /// <summary>Packages such as <c>.app</c> are browsed as directories (macOS).</summary>
    TreatPackageAsDirectory = 1 << 2,

    /// <summary>Linux only in Electron: ignored on macOS.</summary>
    ShowOverwriteConfirmation = 1 << 3,

    /// <summary>Windows only in Electron: ignored on macOS.</summary>
    DontAddToRecent = 1 << 4,
}

/// <summary>Options for <see cref="Dialog.ShowOpenDialogAsync(BrowserWindow?, OpenDialogOptions)"/>; Electron's
/// <c>showOpenDialog</c> options.</summary>
public sealed record OpenDialogOptions
{
    /// <summary>The dialog's title. Most macOS versions do not show it.</summary>
    public string? Title { get; init; }

    /// <summary>The directory to start in, or a file to select there. On macOS, empty starts in Downloads (else
    /// the home directory), as Electron.</summary>
    public string? DefaultPath { get; init; }

    /// <summary>The file types offered; empty for any file. On macOS the first applies, and several add a Format popup.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>What may be chosen.</summary>
    public OpenDialogProperties Properties { get; init; } = OpenDialogProperties.OpenFile;

    /// <summary>The accept button's label (the panel's prompt on macOS). Not supported (setting it throws) off macOS:
    /// CEF's dialog has no custom button label.</summary>
    public string? ButtonLabel { get; init; }

    /// <summary>A message above the panel (macOS). Not supported (setting it throws) elsewhere.</summary>
    public string? Message { get; init; }

    /// <summary>Also return a security-scoped bookmark per path, for a sandboxed app (macOS). Electron returns them in
    /// Mac App Store builds only; Oriel returns them on any macOS build. Ignored elsewhere, as Electron.</summary>
    public bool SecurityScopedBookmarks { get; init; }
}

/// <summary>Options for <see cref="Dialog.ShowSaveDialogAsync(BrowserWindow?, SaveDialogOptions)"/>; Electron's
/// <c>showSaveDialog</c> options.</summary>
public sealed record SaveDialogOptions
{
    /// <summary>The dialog's title. Most macOS versions do not show it.</summary>
    public string? Title { get; init; }

    /// <summary>The directory to start in, the file name to suggest, or both as a path. On macOS, empty starts in
    /// Downloads (else the home directory), as Electron.</summary>
    public string? DefaultPath { get; init; }

    /// <summary>The file types offered; empty for any file. On macOS the first applies, and several add a Format popup.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>Electron's save <c>properties</c>.</summary>
    public SaveDialogProperties Properties { get; init; }

    /// <summary>The accept button's label (the panel's prompt on macOS). Not supported (setting it throws) off macOS:
    /// CEF's dialog has no custom button label.</summary>
    public string? ButtonLabel { get; init; }

    /// <summary>A message above the panel (macOS). Not supported (setting it throws) elsewhere.</summary>
    public string? Message { get; init; }

    /// <summary>The label in front of the file name field (macOS). Not supported (setting it throws) elsewhere.</summary>
    public string? NameFieldLabel { get; init; }

    /// <summary>Whether the panel shows its tags field (macOS); true by default, as Electron. Ignored elsewhere.</summary>
    public bool ShowsTagField { get; init; } = true;

    /// <summary>Also return a security-scoped bookmark for the path, creating the file, for a sandboxed app (macOS).
    /// Electron returns it in Mac App Store builds only; Oriel on any macOS build. Ignored elsewhere, as Electron.</summary>
    public bool SecurityScopedBookmarks { get; init; }
}

/// <summary>What an open dialog returned; Electron's <c>showOpenDialog</c> result.</summary>
/// <param name="Canceled">The user dismissed the dialog.</param>
/// <param name="FilePaths">The chosen paths; empty when cancelled.</param>
public sealed record OpenDialogReturnValue(bool Canceled, IReadOnlyList<string> FilePaths)
{
    /// <summary>Base64 security-scoped bookmarks, one per path ("" where AppKit refused one); empty when cancelled.
    /// Null unless <see cref="OpenDialogOptions.SecurityScopedBookmarks"/> was set on macOS.</summary>
    public IReadOnlyList<string>? Bookmarks { get; init; }
}

/// <summary>What a save dialog returned; Electron's <c>showSaveDialog</c> result.</summary>
/// <param name="Canceled">The user dismissed the dialog.</param>
/// <param name="FilePath">The chosen path; empty when cancelled.</param>
public sealed record SaveDialogReturnValue(bool Canceled, string FilePath)
{
    /// <summary>The base64 security-scoped bookmark ("" when cancelled or AppKit refused one). Null unless
    /// <see cref="SaveDialogOptions.SecurityScopedBookmarks"/> was set on macOS.</summary>
    public string? Bookmark { get; init; }
}

/// <summary>File open and save dialogs; Electron's <c>dialog</c> (message boxes come later).</summary>
/// <remarks>On macOS these are AppKit's NSOpenPanel and NSSavePanel, set up as Electron's: a sheet on the window
/// given, else a free-standing panel. Elsewhere CEF shows its dialogs through a window's browser, so a dialog needs an
/// open window: the one given, else the focused window, else any. Use after <see cref="OrielApp.Ready"/>, from any thread.</remarks>
public static unsafe partial class Dialog
{
    /// <summary>Asks the user for files or a directory. Electron's <c>dialog.showOpenDialog()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>; or, off macOS, no window is open.</exception>
    /// <exception cref="NotSupportedException">Off macOS, an option CEF's dialog cannot honour, such as
    /// <see cref="OpenDialogOptions.ButtonLabel"/>, or directories with <see cref="OpenDialogProperties.MultiSelections"/>
    /// or with <see cref="OpenDialogProperties.OpenFile"/>.</exception>
    public static Task<OpenDialogReturnValue> ShowOpenDialogAsync(BrowserWindow? window, OpenDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsMacOS()) return MacOpenAsync(window, options);
        var mode = OpenModeOf(options);
        return Run(window, mode, options.Title, options.DefaultPath, options.Filters)
            .ContinueWith(t => new OpenDialogReturnValue(t.Result.Count == 0, t.Result), TaskScheduler.Default);
    }

    /// <inheritdoc cref="ShowOpenDialogAsync(BrowserWindow?, OpenDialogOptions)"/>
    public static Task<OpenDialogReturnValue> ShowOpenDialogAsync(OpenDialogOptions options) => ShowOpenDialogAsync(null, options);

    /// <summary>Asks the user where to save a file. Electron's <c>dialog.showSaveDialog()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>; or, off macOS, no window is open.</exception>
    /// <exception cref="NotSupportedException">Off macOS, an option CEF's dialog cannot honour, such as
    /// <see cref="SaveDialogOptions.ButtonLabel"/>.</exception>
    public static Task<SaveDialogReturnValue> ShowSaveDialogAsync(BrowserWindow? window, SaveDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsMacOS()) return MacSaveAsync(window, options);
        RejectCefUnsupported(options);
        return Run(window, cef_file_dialog_mode_t.FILE_DIALOG_SAVE, options.Title, options.DefaultPath, options.Filters)
            .ContinueWith(t => new SaveDialogReturnValue(t.Result.Count == 0, t.Result.Count == 0 ? string.Empty : t.Result[0]), TaskScheduler.Default);
    }

    /// <inheritdoc cref="ShowSaveDialogAsync(BrowserWindow?, SaveDialogOptions)"/>
    public static Task<SaveDialogReturnValue> ShowSaveDialogAsync(SaveDialogOptions options) => ShowSaveDialogAsync(null, options);

    /// <summary>Asks for files or a directory and waits for the answer: the chosen paths, or null when cancelled.
    /// Electron's <c>dialog.showOpenDialogSync()</c>.</summary>
    /// <remarks>On macOS it runs modally on the main (UI) thread, as Electron: a sheet with its window run modally,
    /// else an app-modal panel; CEF's own UI tasks wait until it closes, as Chromium's do under Electron. Called from
    /// another thread, that thread waits.</remarks>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>; or, off macOS, called on the
    /// UI thread, which must stay free to run the dialog, or no window is open.</exception>
    /// <exception cref="NotSupportedException">See <see cref="ShowOpenDialogAsync(BrowserWindow?, OpenDialogOptions)"/>.</exception>
    public static IReadOnlyList<string>? ShowOpenDialogSync(BrowserWindow? window, OpenDialogOptions options)
    {
        if (OperatingSystem.IsMacOS()) return MacOpenSync(window, options);
        RequireOffUiThread();
        var result = ShowOpenDialogAsync(window, options).GetAwaiter().GetResult();
        return result.Canceled ? null : result.FilePaths;
    }

    /// <summary>Asks where to save and waits for the answer: the path, or null when cancelled.
    /// Electron's <c>dialog.showSaveDialogSync()</c>.</summary>
    /// <remarks>See <see cref="ShowOpenDialogSync(BrowserWindow?, OpenDialogOptions)"/>.</remarks>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>; or, off macOS, called on the
    /// UI thread, which must stay free to run the dialog, or no window is open.</exception>
    /// <exception cref="NotSupportedException">See <see cref="ShowSaveDialogAsync(BrowserWindow?, SaveDialogOptions)"/>.</exception>
    public static string? ShowSaveDialogSync(BrowserWindow? window, SaveDialogOptions options)
    {
        if (OperatingSystem.IsMacOS()) return MacSaveSync(window, options);
        RequireOffUiThread();
        var result = ShowSaveDialogAsync(window, options).GetAwaiter().GetResult();
        return result.Canceled ? null : result.FilePath;
    }

    /// <summary>The panel setup for an open dialog, as Electron's <c>SetupDialog</c> and
    /// <c>SetupOpenDialogForProperties</c>. Electron always leaves the tags field on for open panels.</summary>
    internal static MacPanelSettings MacOpenSettingsOf(OpenDialogOptions options, Func<string, bool> directoryExists, Func<string> fallbackPath)
    {
        var (directory, fileName) = MacDefaultPathOf(options.DefaultPath, directoryExists, fallbackPath);
        var properties = options.Properties;
        return new MacPanelSettings
        {
            Title = options.Title ?? "",
            Message = options.Message ?? "",
            ButtonLabel = options.ButtonLabel ?? "",
            Directory = directory,
            FileName = fileName,
            Filters = MacFiltersOf(options.Filters),
            CanChooseFiles = properties.HasFlag(OpenDialogProperties.OpenFile),
            CanChooseDirectories = properties.HasFlag(OpenDialogProperties.OpenDirectory),
            CanCreateDirectories = properties.HasFlag(OpenDialogProperties.CreateDirectory),
            AllowsMultipleSelection = properties.HasFlag(OpenDialogProperties.MultiSelections),
            ShowsHiddenFiles = properties.HasFlag(OpenDialogProperties.ShowHiddenFiles),
            NoResolveAliases = properties.HasFlag(OpenDialogProperties.NoResolveAliases),
            TreatsFilePackagesAsDirectories = properties.HasFlag(OpenDialogProperties.TreatPackageAsDirectory),
        };
    }

    /// <summary>The panel setup for a save dialog, as Electron's <c>SetupDialog</c> and <c>SetupSaveDialogForProperties</c>;
    /// only the async form lets the user hide the extension, as Electron's.</summary>
    internal static MacPanelSettings MacSaveSettingsOf(SaveDialogOptions options, Func<string, bool> directoryExists, Func<string> fallbackPath, bool async)
    {
        var (directory, fileName) = MacDefaultPathOf(options.DefaultPath, directoryExists, fallbackPath);
        var properties = options.Properties;
        return new MacPanelSettings
        {
            Title = options.Title ?? "",
            Message = options.Message ?? "",
            ButtonLabel = options.ButtonLabel ?? "",
            NameFieldLabel = options.NameFieldLabel ?? "",
            ShowsTagField = options.ShowsTagField,
            Directory = directory,
            FileName = fileName,
            Filters = MacFiltersOf(options.Filters),
            CanCreateDirectories = properties.HasFlag(SaveDialogProperties.CreateDirectory),
            ShowsHiddenFiles = properties.HasFlag(SaveDialogProperties.ShowHiddenFiles),
            TreatsFilePackagesAsDirectories = properties.HasFlag(SaveDialogProperties.TreatPackageAsDirectory),
            CanSelectHiddenExtension = async,
        };
    }

    /// <summary>Electron's split of <c>defaultPath</c> on macOS: an existing directory is where the panel starts; else
    /// an absolute path's parent is, and its last component is the suggested name. An empty path falls back to
    /// <paramref name="fallbackPath"/> (Electron's <c>GetDefaultPath</c>), but the split still reads the given one.</summary>
    internal static (string? Directory, string? FileName) MacDefaultPathOf(string? defaultPath, Func<string, bool> directoryExists, Func<string> fallbackPath)
    {
        var given = defaultPath ?? "";
        var path = given.Length == 0 ? fallbackPath() : given;
        if (path.Length == 0) return (null, null);
        if (directoryExists(path)) return (path, null);
        return (path.StartsWith('/') ? DirName(given) : null, BaseName(given));
    }

    /// <summary>Electron's macOS filter types: each extension reduced to its last part (<c>tar.gz</c> to <c>gz</c>,
    /// <c>.png</c> to <c>png</c>), duplicates dropped in order.</summary>
    internal static IReadOnlyList<(string Name, IReadOnlyList<string> Types)> MacFiltersOf(IReadOnlyList<FileFilter> filters)
        => [.. filters.Select(f => (f.Name, (IReadOnlyList<string>)[.. f.Extensions.Select(e => e[(e.LastIndexOf('.') + 1)..]).Distinct(StringComparer.Ordinal)]))];

    // Chromium's base::FilePath on POSIX: trailing separators do not count, and the root is its own name and parent.
    private static string StripTrailingSeparators(string path)
    {
        var end = path.Length;
        while (end > 1 && path[end - 1] == '/') end--;
        return path[..end];
    }

    private static string BaseName(string path)
    {
        var stripped = StripTrailingSeparators(path);
        var slash = stripped.LastIndexOf('/');
        return slash < 0 || stripped == "/" ? stripped : stripped[(slash + 1)..];
    }

    private static string DirName(string path)
    {
        var stripped = StripTrailingSeparators(path);
        var slash = stripped.LastIndexOf('/');
        return slash < 0 ? "." : slash == 0 ? "/" : StripTrailingSeparators(stripped[..slash]);
    }

    // Electron's GetDefaultPath: the downloads directory when it exists, else home.
    private static string MacFallbackPath()
        => Directory.Exists(DownloadItem.DownloadsDirectory) ? DownloadItem.DownloadsDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static nint ContentViewOf(BrowserWindow? window) => window is null || window.IsDestroyed ? 0 : window.GetNativeWindowHandle();

    [SupportedOSPlatform("macos")]
    private static IReadOnlyList<string>? MacOpenSync(BrowserWindow? window, OpenDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireReady();
        var settings = MacOpenSettingsOf(options, Directory.Exists, MacFallbackPath);
        return CefThread.InvokeOnUi(() =>
        {
            var panel = MacFileDialog.CreateOpenPanel(settings);
            try { return MacFileDialog.RunModal(panel, ContentViewOf(window)) ? MacFileDialog.Paths(panel) : null; }
            finally { MacFileDialog.Release(panel); }
        });
    }

    [SupportedOSPlatform("macos")]
    private static string? MacSaveSync(BrowserWindow? window, SaveDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireReady();
        var settings = MacSaveSettingsOf(options, Directory.Exists, MacFallbackPath, async: false);
        return CefThread.InvokeOnUi(() =>
        {
            var panel = MacFileDialog.CreateSavePanel(settings);
            try { return MacFileDialog.RunModal(panel, ContentViewOf(window)) ? MacFileDialog.SavePath(panel) : null; }
            finally { MacFileDialog.Release(panel); }
        });
    }

    [SupportedOSPlatform("macos")]
    private static Task<OpenDialogReturnValue> MacOpenAsync(BrowserWindow? window, OpenDialogOptions options)
    {
        RequireReady();
        var settings = MacOpenSettingsOf(options, Directory.Exists, MacFallbackPath);
        var bookmarks = options.SecurityScopedBookmarks;
        return MacBegin(window, () => MacFileDialog.CreateOpenPanel(settings), (panel, accepted) =>
        {
            if (!accepted) return new OpenDialogReturnValue(true, []) { Bookmarks = bookmarks ? [] : null };
            List<string>? marks = bookmarks ? [] : null;
            return new OpenDialogReturnValue(false, MacFileDialog.Paths(panel, marks)) { Bookmarks = marks };
        });
    }

    [SupportedOSPlatform("macos")]
    private static Task<SaveDialogReturnValue> MacSaveAsync(BrowserWindow? window, SaveDialogOptions options)
    {
        RequireReady();
        var settings = MacSaveSettingsOf(options, Directory.Exists, MacFallbackPath, async: true);
        var bookmark = options.SecurityScopedBookmarks;
        return MacBegin(window, () => MacFileDialog.CreateSavePanel(settings), (panel, accepted) => accepted
            ? new SaveDialogReturnValue(false, MacFileDialog.UrlPath(panel) ?? "") { Bookmark = bookmark ? MacFileDialog.SaveBookmark(panel) : null }
            : new SaveDialogReturnValue(true, "") { Bookmark = bookmark ? "" : null });
    }

    [SupportedOSPlatform("macos")]
    private static Task<T> MacBegin<T>(BrowserWindow? window, Func<nint> create, Func<nint, bool, T> read)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            try
            {
                var panel = create();
                MacFileDialog.Begin(panel, ContentViewOf(window), accepted =>
                {
                    try { done.TrySetResult(read(panel, accepted)); }
                    catch (Exception e) { done.TrySetException(e); }
                    // AppKit is still inside the panel's completion; release it after.
                    CefThread.PostUi(() => MacFileDialog.Release(panel));
                });
            }
            catch (Exception e) { done.TrySetException(e); }
        });
        return done.Task;
    }

    /// <summary>CEF's mode for an open dialog.</summary>
    /// <exception cref="NotSupportedException">A combination CEF has no mode for, or an option it cannot honour.</exception>
    internal static cef_file_dialog_mode_t OpenModeOf(OpenDialogOptions options)
    {
        Unsupported(options.ButtonLabel, nameof(OpenDialogOptions.ButtonLabel));
        Unsupported(options.Message, nameof(OpenDialogOptions.Message));
        const OpenDialogProperties handled = OpenDialogProperties.OpenFile | OpenDialogProperties.OpenDirectory | OpenDialogProperties.MultiSelections;
        if ((options.Properties & ~handled) is var other and not OpenDialogProperties.None)
            throw new NotSupportedException($"{other} is not supported: CEF's file dialog has no option for it.");

        var properties = options.Properties;
        if (properties.HasFlag(OpenDialogProperties.OpenDirectory))
        {
            if (properties.HasFlag(OpenDialogProperties.OpenFile))
                throw new NotSupportedException("Choosing files and directories in one dialog is not supported: CEF has no such mode.");
            if (properties.HasFlag(OpenDialogProperties.MultiSelections))
                throw new NotSupportedException("Choosing several directories is not supported: CEF has no such mode.");
            return cef_file_dialog_mode_t.FILE_DIALOG_OPEN_FOLDER;
        }
        return properties.HasFlag(OpenDialogProperties.MultiSelections)
            ? cef_file_dialog_mode_t.FILE_DIALOG_OPEN_MULTIPLE
            : cef_file_dialog_mode_t.FILE_DIALOG_OPEN;
    }

    /// <summary>Throws for a save option CEF's dialog cannot honour.</summary>
    internal static void RejectCefUnsupported(SaveDialogOptions options)
    {
        if (CefUnsupportedOf(options) is [var first, ..])
            throw new NotSupportedException($"{first} is not supported: CEF's file dialog has no option for it.");
    }

    /// <summary>The save options set that CEF's dialog cannot honour. <c>showsTagField</c> and
    /// <c>securityScopedBookmarks</c> are macOS-only in Electron, so ignoring them elsewhere matches it.</summary>
    internal static IReadOnlyList<string> CefUnsupportedOf(SaveDialogOptions options)
    {
        var names = new List<string>();
        if (options.ButtonLabel is not null) names.Add(nameof(SaveDialogOptions.ButtonLabel));
        if (options.Message is not null) names.Add(nameof(SaveDialogOptions.Message));
        if (options.NameFieldLabel is not null) names.Add(nameof(SaveDialogOptions.NameFieldLabel));
        if (options.Properties != SaveDialogProperties.None) names.Add(options.Properties.ToString());
        return names;
    }

    /// <summary>CEF's accept filters: <c>Name|.ext;.ext</c> per filter. A filter for any file (<c>*</c>) adds none:
    /// CEF has no form for it.</summary>
    internal static IReadOnlyList<string> AcceptFiltersOf(IReadOnlyList<FileFilter> filters)
        => [.. filters
            .Where(f => f.Extensions.Count > 0 && !f.Extensions.Contains("*"))
            .Select(f => $"{f.Name}|{string.Join(';', f.Extensions.Select(e => "." + e.TrimStart('.').ToLowerInvariant()))}")];

    private static Task<IReadOnlyList<string>> Run(BrowserWindow? window, cef_file_dialog_mode_t mode, string? title, string? defaultPath,
        IReadOnlyList<FileFilter> filters)
    {
        RequireReady();
        var owner = window ?? BrowserWindow.GetFocusedWindow() ?? BrowserWindow.GetAllWindows().FirstOrDefault()
            ?? throw new InvalidOperationException("A file dialog needs an open window: CEF shows it through a window's browser.");
        var accept = AcceptFiltersOf(filters);
        var done = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            var browser = owner.WebContents.Browser;
            if (browser is null)
            {
                done.TrySetException(new InvalidOperationException("The window closed before its file dialog opened."));
                return;
            }
            StartIn(defaultPath);
            var callback = new RunFileDialogCallback(paths => done.TrySetResult(paths));
            cef_string_utf16_t titleString = default, pathString = default;
            CefString.Set(&titleString, title);
            CefString.Set(&pathString, defaultPath);
            var list = CefNative.cef_string_list_alloc();
            foreach (var filter in accept)
            {
                cef_string_utf16_t value = default;
                CefString.Set(&value, filter);
                CefNative.cef_string_list_append(list, &value);
                CefString.Clear(&value);
            }
            var host = browser->get_host(browser);
            // The callback reference passes to CEF, which releases it after the dialog closes.
            host->run_file_dialog(host, mode, &titleString, &pathString, list, callback.Native);
            NativeObject.Release(host);
            CefNative.cef_string_list_free(list);
            CefString.Clear(&titleString);
            CefString.Clear(&pathString);
        });
        return done.Task;
    }

    // Chrome-style CEF runs the dialog as a page's file chooser, which keeps only the file name of the default path
    // and starts in the profile's last-used directory; pointing that at the default path's directory restores it.
    private static void StartIn(string? defaultPath)
    {
        if (string.IsNullOrEmpty(defaultPath)) return;
        var directory = Directory.Exists(defaultPath) ? defaultPath : Path.GetDirectoryName(defaultPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        var context = CefNative.cef_request_context_get_global_context();
        var value = CefNative.cef_value_create();
        cef_string_utf16_t name = default, text = default, error = default;
        CefString.Set(&name, "selectfile.last_directory");
        CefString.Set(&text, Path.GetFullPath(directory));
        value->set_string(value, &text);
        var manager = (cef_preference_manager_t*)context;
        if (manager->set_preference(manager, &name, value, &error) == 0)
            Console.Error.WriteLine($"[Oriel] Could not set the file dialog's start directory: {CefString.Read(&error)}");
        CefString.Clear(&name);
        CefString.Clear(&text);
        CefString.Clear(&error);
        NativeObject.Release(context);
    }

    private static void Unsupported(string? value, string name)
    {
        if (value is not null) throw new NotSupportedException($"{name} is not supported: CEF's file dialog has no option for it.");
    }

    private static void RequireReady()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true }) throw new InvalidOperationException("Use Dialog after OrielApp.Ready.");
    }

    private static void RequireOffUiThread()
    {
        if (OrielApp.CurrentOrNull is { IsReady: true } && CefThread.IsOnUiThread)
            throw new InvalidOperationException("Call the Sync dialogs off the UI thread (or use the Async ones): the UI thread runs the dialog.");
    }
}
