using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A file type choice in a file dialog; Electron's <c>FileFilter</c>.</summary>
/// <param name="Name">What the user sees, e.g. <c>Images</c>.</param>
/// <param name="Extensions">Extensions without the dot, e.g. <c>png</c>; <c>*</c> for any file.</param>
public sealed record FileFilter(string Name, params IReadOnlyList<string> Extensions);

/// <summary>What an open dialog may choose; Electron's <c>properties</c> for <c>showOpenDialog</c>.</summary>
[Flags]
public enum OpenDialogProperties
{
    /// <summary>Nothing set: the same as <see cref="OpenFile"/>.</summary>
    None = 0,

    /// <summary>Files can be chosen.</summary>
    OpenFile = 1 << 0,

    /// <summary>Directories can be chosen instead of files.</summary>
    OpenDirectory = 1 << 1,

    /// <summary>Several files can be chosen.</summary>
    MultiSelections = 1 << 2,

    /// <summary>Hidden files are listed. macOS and Linux: not supported (throws).</summary>
    ShowHiddenFiles = 1 << 3,

    /// <summary>macOS: not supported (throws). Ignored on Windows, as in Electron. Linux: not supported (throws).</summary>
    CreateDirectory = 1 << 4,

    /// <summary>Windows: offers to create a file name that does not exist (the path is returned, the file is not
    /// created). macOS and Linux: not supported (throws).</summary>
    PromptToCreate = 1 << 5,

    /// <summary>macOS: not supported (throws). Ignored on Windows, as in Electron. Linux: not supported (throws).</summary>
    NoResolveAliases = 1 << 6,

    /// <summary>macOS: not supported (throws). Ignored on Windows, as in Electron. Linux: not supported (throws).</summary>
    TreatPackageAsDirectory = 1 << 7,

    /// <summary>Windows: the chosen item is not added to recent documents. macOS and Linux: not supported (throws).</summary>
    DontAddToRecent = 1 << 8,
}

/// <summary>How a save dialog behaves; Electron's <c>properties</c> for <c>showSaveDialog</c>.</summary>
[Flags]
public enum SaveDialogProperties
{
    /// <summary>Nothing set.</summary>
    None = 0,

    /// <summary>macOS: not supported (throws). Ignored on Windows, as in Electron. Linux: not supported (throws).</summary>
    CreateDirectory = 1 << 0,

    /// <summary>Hidden files are listed. macOS and Linux: not supported (throws).</summary>
    ShowHiddenFiles = 1 << 1,

    /// <summary>macOS: not supported (throws). Ignored on Windows, as in Electron. Linux: not supported (throws).</summary>
    TreatPackageAsDirectory = 1 << 2,

    /// <summary>Linux: not supported (throws). Ignored on Windows, which always confirms an overwrite, as in Electron.
    /// macOS: not supported (throws).</summary>
    ShowOverwriteConfirmation = 1 << 3,

    /// <summary>Windows: the chosen item is not added to recent documents. macOS and Linux: not supported (throws).</summary>
    DontAddToRecent = 1 << 4,
}

/// <summary>Options for <see cref="Dialog.ShowOpenDialogAsync(BrowserWindow?, OpenDialogOptions)"/>; Electron's
/// <c>showOpenDialog</c> options.</summary>
public sealed record OpenDialogOptions
{
    /// <summary>The dialog's title. Most macOS versions do not show it.</summary>
    public string? Title { get; init; }

    /// <summary>The directory to start in, or a file to select there.</summary>
    public string? DefaultPath { get; init; }

    /// <summary>The file types offered; empty for any file.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>What may be chosen.</summary>
    public OpenDialogProperties Properties { get; init; } = OpenDialogProperties.OpenFile;

    /// <summary>The accept button's label. macOS and Linux: not supported (setting it throws).</summary>
    public string? ButtonLabel { get; init; }

    /// <summary>A message above the panel (macOS): not supported there (setting it throws). Ignored on Windows, as in
    /// Electron. Linux: not supported (throws).</summary>
    public string? Message { get; init; }
}

/// <summary>Options for <see cref="Dialog.ShowSaveDialogAsync(BrowserWindow?, SaveDialogOptions)"/>; Electron's
/// <c>showSaveDialog</c> options.</summary>
public sealed record SaveDialogOptions
{
    /// <summary>The dialog's title. Most macOS versions do not show it.</summary>
    public string? Title { get; init; }

    /// <summary>The directory to start in, the file name to suggest, or both as a path.</summary>
    public string? DefaultPath { get; init; }

    /// <summary>The file types offered; empty for any file.</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>The accept button's label. macOS and Linux: not supported (setting it throws).</summary>
    public string? ButtonLabel { get; init; }

    /// <summary>A message above the panel (macOS): not supported there (setting it throws). Ignored on Windows, as in
    /// Electron. Linux: not supported (throws).</summary>
    public string? Message { get; init; }

    /// <summary>The name field's label (macOS): not supported there (setting it throws). Ignored on Windows, as in
    /// Electron. Linux: not supported (throws).</summary>
    public string? NameFieldLabel { get; init; }

    /// <summary>A tag field (macOS): not supported there (setting it true throws). Ignored on Windows, as in Electron.
    /// Linux: not supported (throws).</summary>
    public bool ShowsTagField { get; init; }

    /// <summary>How the dialog behaves.</summary>
    public SaveDialogProperties Properties { get; init; }
}

/// <summary>What an open dialog returned; Electron's <c>showOpenDialog</c> result.</summary>
/// <param name="Canceled">The user dismissed the dialog.</param>
/// <param name="FilePaths">The chosen paths; empty when cancelled.</param>
public sealed record OpenDialogReturnValue(bool Canceled, IReadOnlyList<string> FilePaths);

/// <summary>What a save dialog returned; Electron's <c>showSaveDialog</c> result.</summary>
/// <param name="Canceled">The user dismissed the dialog.</param>
/// <param name="FilePath">The chosen path; empty when cancelled.</param>
public sealed record SaveDialogReturnValue(bool Canceled, string FilePath);

/// <summary>File open and save dialogs; Electron's <c>dialog</c> (message boxes come later).</summary>
/// <remarks>Use after <see cref="OrielApp.Ready"/>, from any thread. Windows shows the shell's file dialog on a thread of
/// its own, owned by the window given or by none, as Electron does. macOS and Linux show CEF's dialog through a window's
/// browser, so a dialog there needs an open window: the one given, else the focused window, else any.</remarks>
public static unsafe partial class Dialog
{
    /// <summary>Asks the user for files or a directory. Electron's <c>dialog.showOpenDialog()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>; macOS and Linux: no window is open.</exception>
    /// <exception cref="NotSupportedException">macOS and Linux: an option CEF's dialog cannot honour, such as
    /// <see cref="OpenDialogOptions.ButtonLabel"/>, or directories with <see cref="OpenDialogProperties.MultiSelections"/>
    /// or with <see cref="OpenDialogProperties.OpenFile"/>.</exception>
    public static Task<OpenDialogReturnValue> ShowOpenDialogAsync(BrowserWindow? window, OpenDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsWindows()) return ShowOpenOnWindows(window, options);
        var mode = OpenModeOf(options);
        return Run(window, mode, options.Title, options.DefaultPath, options.Filters)
            .ContinueWith(t => new OpenDialogReturnValue(t.Result.Count == 0, t.Result), TaskScheduler.Default);
    }

    /// <inheritdoc cref="ShowOpenDialogAsync(BrowserWindow?, OpenDialogOptions)"/>
    public static Task<OpenDialogReturnValue> ShowOpenDialogAsync(OpenDialogOptions options) => ShowOpenDialogAsync(null, options);

    /// <summary>Asks the user where to save a file. Electron's <c>dialog.showSaveDialog()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>; macOS and Linux: no window is open.</exception>
    /// <exception cref="NotSupportedException">macOS and Linux: an option CEF's dialog cannot honour, such as
    /// <see cref="SaveDialogOptions.ButtonLabel"/>.</exception>
    public static Task<SaveDialogReturnValue> ShowSaveDialogAsync(BrowserWindow? window, SaveDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsWindows()) return ShowSaveOnWindows(window, options);
        CheckCefSaveOptions(options);
        return Run(window, cef_file_dialog_mode_t.FILE_DIALOG_SAVE, options.Title, options.DefaultPath, options.Filters)
            .ContinueWith(t => new SaveDialogReturnValue(t.Result.Count == 0, t.Result.Count == 0 ? string.Empty : t.Result[0]), TaskScheduler.Default);
    }

    /// <inheritdoc cref="ShowSaveDialogAsync(BrowserWindow?, SaveDialogOptions)"/>
    public static Task<SaveDialogReturnValue> ShowSaveDialogAsync(SaveDialogOptions options) => ShowSaveDialogAsync(null, options);

    /// <summary>Asks for files or a directory and waits for the answer: the chosen paths, or null when cancelled.
    /// Electron's <c>dialog.showOpenDialogSync()</c>.</summary>
    /// <remarks>Windows: on the UI thread, window messages keep being dispatched while the dialog is up, as in a modal
    /// dialog's loop.</remarks>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>. macOS and Linux: called on the UI
    /// thread, which must stay free to run the dialog; or no window is open.</exception>
    /// <exception cref="NotSupportedException">See <see cref="ShowOpenDialogAsync(BrowserWindow?, OpenDialogOptions)"/>.</exception>
    public static IReadOnlyList<string>? ShowOpenDialogSync(BrowserWindow? window, OpenDialogOptions options)
    {
        if (!OperatingSystem.IsWindows()) RequireOffUiThread();
        var dialog = ShowOpenDialogAsync(window, options);
        var result = OperatingSystem.IsWindows() ? WaitOnWindows(dialog) : dialog.GetAwaiter().GetResult();
        return result.Canceled ? null : result.FilePaths;
    }

    /// <summary>Asks where to save and waits for the answer: the path, or null when cancelled.
    /// Electron's <c>dialog.showSaveDialogSync()</c>.</summary>
    /// <remarks>See <see cref="ShowOpenDialogSync(BrowserWindow?, OpenDialogOptions)"/>.</remarks>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>. macOS and Linux: called on the UI
    /// thread, which must stay free to run the dialog; or no window is open.</exception>
    /// <exception cref="NotSupportedException">See <see cref="ShowSaveDialogAsync(BrowserWindow?, SaveDialogOptions)"/>.</exception>
    public static string? ShowSaveDialogSync(BrowserWindow? window, SaveDialogOptions options)
    {
        if (!OperatingSystem.IsWindows()) RequireOffUiThread();
        var dialog = ShowSaveDialogAsync(window, options);
        var result = OperatingSystem.IsWindows() ? WaitOnWindows(dialog) : dialog.GetAwaiter().GetResult();
        return result.Canceled ? null : result.FilePath;
    }

    /// <summary>Throws for a save option CEF's dialog cannot honour.</summary>
    /// <exception cref="NotSupportedException">The option.</exception>
    internal static void CheckCefSaveOptions(SaveDialogOptions options)
    {
        Unsupported(options.ButtonLabel, nameof(SaveDialogOptions.ButtonLabel));
        Unsupported(options.Message, nameof(SaveDialogOptions.Message));
        Unsupported(options.NameFieldLabel, nameof(SaveDialogOptions.NameFieldLabel));
        if (options.ShowsTagField) throw new NotSupportedException("ShowsTagField is not supported: CEF's file dialog has no tag field.");
        if (options.Properties != SaveDialogProperties.None)
            throw new NotSupportedException($"{options.Properties} is not supported: CEF's file dialog has no option for it.");
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

    /// <summary>CEF's accept filters: <c>Name|.ext;.ext</c> per filter. A filter for any file (<c>*</c>) adds none:
    /// CEF has no form for it.</summary>
    internal static IReadOnlyList<string> AcceptFiltersOf(IReadOnlyList<FileFilter> filters)
        => [.. filters
            .Where(f => f.Extensions.Count > 0 && !f.Extensions.Contains("*"))
            .Select(f => $"{f.Name}|{string.Join(';', f.Extensions.Select(e => "." + e.TrimStart('.').ToLowerInvariant()))}")];

    private static Task<IReadOnlyList<string>> Run(BrowserWindow? window, cef_file_dialog_mode_t mode, string? title, string? defaultPath,
        IReadOnlyList<FileFilter> filters)
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true }) throw new InvalidOperationException("Use Dialog after OrielApp.Ready.");
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

    private static void RequireOffUiThread()
    {
        if (OrielApp.CurrentOrNull is { IsReady: true } && CefThread.IsOnUiThread)
            throw new InvalidOperationException("Call the Sync dialogs off the UI thread (or use the Async ones): the UI thread runs the dialog.");
    }
}
