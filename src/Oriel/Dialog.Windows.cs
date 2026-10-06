using System.Runtime.Versioning;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

// Windows file dialogs: the shell's IFileDialog, set up as Electron's shell/browser/ui/file_dialog_win.cc sets it up.
public static partial class Dialog
{
    [SupportedOSPlatform("windows")]
    private static Task<OpenDialogReturnValue> ShowOpenOnWindows(BrowserWindow? window, OpenDialogOptions options)
    {
        RequireReady();
        var settings = SettingsOf(OpenOptionsOf(options.Properties), options.Title, options.ButtonLabel, options.DefaultPath, options.Filters);
        var owner = OwnerOf(window);
        return WindowsFileDialog.RunOnDialogThread(() =>
        {
            var (ok, paths) = WindowsFileDialog.ShowOpen(owner, settings);
            return new OpenDialogReturnValue(!ok, paths);
        });
    }

    [SupportedOSPlatform("windows")]
    private static Task<SaveDialogReturnValue> ShowSaveOnWindows(BrowserWindow? window, SaveDialogOptions options)
    {
        RequireReady();
        var settings = SettingsOf(SaveOptionsOf(options.Properties), options.Title, options.ButtonLabel, options.DefaultPath, options.Filters);
        var owner = OwnerOf(window);
        return WindowsFileDialog.RunOnDialogThread(() =>
        {
            var path = WindowsFileDialog.ShowSave(owner, settings);
            return new SaveDialogReturnValue(path is null, path ?? string.Empty);
        });
    }

    // Electron's sync dialogs run modally on the main thread, which keeps dispatching its messages. Oriel's UI thread
    // is in the MTA, so the dialog runs on its own STA thread and the UI thread dispatches messages while it waits.
    [SupportedOSPlatform("windows")]
    private static T WaitOnWindows<T>(Task<T> dialog)
    {
        if (CefThread.IsOnUiThread) WindowsFileDialog.WaitPumping(dialog);
        return dialog.GetAwaiter().GetResult();
    }

    // Electron resolves the owner's HWND on the UI thread and passes none when no window is given.
    private static nint OwnerOf(BrowserWindow? window) => window?.GetNativeWindowHandle() ?? 0;

    /// <summary>The <c>FOS_</c> flags Electron's <c>ShowOpenDialogSyncWithParent</c> sets; properties it does not
    /// read on Windows add nothing.</summary>
    internal static uint OpenOptionsOf(OpenDialogProperties properties)
    {
        var options = Fos.ForceFileSystem | Fos.FileMustExist;
        if (properties.HasFlag(OpenDialogProperties.OpenDirectory)) options |= Fos.PickFolders;
        if (properties.HasFlag(OpenDialogProperties.MultiSelections)) options |= Fos.AllowMultiSelect;
        if (properties.HasFlag(OpenDialogProperties.ShowHiddenFiles)) options |= Fos.ForceShowHidden;
        if (properties.HasFlag(OpenDialogProperties.PromptToCreate)) options |= Fos.CreatePrompt;
        if (properties.HasFlag(OpenDialogProperties.DontAddToRecent)) options |= Fos.DontAddToRecent;
        return options;
    }

    /// <summary>The <c>FOS_</c> flags Electron's <c>ShowSaveDialogSyncWithParent</c> sets.</summary>
    internal static uint SaveOptionsOf(SaveDialogProperties properties)
    {
        var options = Fos.ForceFileSystem | Fos.PathMustExist | Fos.OverwritePrompt;
        if (properties.HasFlag(SaveDialogProperties.ShowHiddenFiles)) options |= Fos.ForceShowHidden;
        if (properties.HasFlag(SaveDialogProperties.DontAddToRecent)) options |= Fos.DontAddToRecent;
        return options;
    }

    /// <summary>Electron's <c>ConvertFilters</c>: each extension becomes <c>*.ext</c> as given, joined by <c>;</c>;
    /// a filter without extensions is <c>*.*</c>; no filters at all is one "All Files (*.*)" choice.</summary>
    internal static IReadOnlyList<(string Name, string Spec)> FileTypesOf(IReadOnlyList<FileFilter> filters)
        => filters.Count == 0
            ? [("All Files (*.*)", "*.*")]
            : [.. filters.Select(f => (f.Name, f.Extensions.Count == 0 ? "*.*" : string.Join(';', f.Extensions.Select(e => "*." + e))))];

    /// <summary>The one-based index of the first file type whose spec is not exactly <c>*.*</c>, and the extension
    /// after <c>*.</c> in that spec's first pattern; (0, null) when every type is <c>*.*</c>.</summary>
    internal static (int Index, string? Extension) DefaultExtensionOf(IReadOnlyList<(string Name, string Spec)> fileTypes)
    {
        for (var i = 0; i < fileTypes.Count; i++)
        {
            var spec = fileTypes[i].Spec;
            if (spec == "*.*") continue;
            var first = spec.IndexOf(';', StringComparison.Ordinal) is var semicolon and >= 0 ? spec[..semicolon] : spec;
            return (i + 1, first[2..]);
        }
        return (0, null);
    }

    /// <summary>Electron's split of <c>defaultPath</c>: the name box gets the base name unless the path is a
    /// directory, and an absolute path opens in itself (a directory) or its parent.</summary>
    /// <param name="defaultPath">The option; null or empty uses <paramref name="defaultFolder"/>.</param>
    /// <param name="defaultFolder">Electron's <c>GetDefaultPath()</c>.</param>
    internal static (string FileName, string? Folder) DefaultPathOf(string? defaultPath, Func<string> defaultFolder)
    {
        var path = string.IsNullOrEmpty(defaultPath) ? defaultFolder() : defaultPath;
        var isDirectory = ChromiumPath.IsDirectory(path);
        var fileName = isDirectory ? string.Empty : ChromiumPath.BaseName(path);
        var folder = ChromiumPath.IsAbsolute(path) ? (isDirectory ? path : ChromiumPath.DirName(path)) : null;
        return (fileName, folder);
    }

    // Electron's GetDefaultPath: the downloads directory if it exists, else home.
    [SupportedOSPlatform("windows")]
    private static string DefaultFolder()
        => WindowsFileDialog.DownloadsFolder() is var downloads && Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [SupportedOSPlatform("windows")]
    private static WindowsFileDialogSettings SettingsOf(uint options, string? title, string? buttonLabel, string? defaultPath, IReadOnlyList<FileFilter> filters)
    {
        var (fileName, folder) = DefaultPathOf(defaultPath, DefaultFolder);
        var fileTypes = FileTypesOf(filters);
        var (index, extension) = DefaultExtensionOf(fileTypes);
        return new WindowsFileDialogSettings(options, fileName, title, buttonLabel, fileTypes, index, extension, folder);
    }

}

/// <summary>The <c>FOS_</c> flags Electron sets on Windows file dialogs; ShObjIdl_core.h's values.</summary>
internal static class Fos
{
    internal const uint OverwritePrompt = 0x2;
    internal const uint PickFolders = 0x20;
    internal const uint ForceFileSystem = 0x40;
    internal const uint AllowMultiSelect = 0x200;
    internal const uint PathMustExist = 0x800;
    internal const uint FileMustExist = 0x1000;
    internal const uint CreatePrompt = 0x2000;
    internal const uint DontAddToRecent = 0x2000000;
    internal const uint ForceShowHidden = 0x10000000;
}

/// <summary>Chromium's <c>base::FilePath</c> on Windows, for the parts Electron's file dialog uses: both <c>\</c> and
/// <c>/</c> separate, and a drive letter is <c>X:</c>.</summary>
internal static class ChromiumPath
{
    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static int DriveLetter(string path) => path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? 1 : -1;

    /// <summary><c>FilePath::IsAbsolute</c>: a drive letter and a separator, or two leading separators.</summary>
    internal static bool IsAbsolute(string path)
        => DriveLetter(path) is var letter && letter >= 0
            ? path.Length > letter + 1 && IsSeparator(path[letter + 1])
            : path.Length > 1 && IsSeparator(path[0]) && IsSeparator(path[1]);

    /// <summary><c>FilePath::EndsWithSeparator</c>.</summary>
    internal static bool EndsWithSeparator(string path) => path.Length > 0 && IsSeparator(path[^1]);

    /// <summary>Electron's <c>IsDirectory</c>: what the file system says, or a trailing separator when nothing is there.</summary>
    internal static bool IsDirectory(string path)
        => Directory.Exists(path) || (!File.Exists(path) && EndsWithSeparator(path));

    /// <summary><c>FilePath::BaseName</c>.</summary>
    internal static string BaseName(string path)
    {
        path = StripTrailingSeparators(path);
        if (DriveLetter(path) is var letter && letter >= 0) path = path[(letter + 1)..];
        var last = path.LastIndexOfAny(['\\', '/']);
        return last >= 0 && last < path.Length - 1 ? path[(last + 1)..] : path;
    }

    /// <summary><c>FilePath::DirName</c>.</summary>
    internal static string DirName(string path)
    {
        path = StripTrailingSeparators(path);
        var last = path.LastIndexOfAny(['\\', '/']);
        if (last < 0) return DriveLetter(path) is var letter && letter >= 0 ? path[..(letter + 1)] : ".";
        return StripTrailingSeparators(path[..(last + 1)]);
    }

    // FilePath::StripTrailingSeparatorsInternal: keeps the root separator, and a leading pair unless there were more.
    private static string StripTrailingSeparators(string path)
    {
        var start = DriveLetter(path) + 2;
        var lastStripped = -1;
        for (var pos = path.Length; pos > start && IsSeparator(path[pos - 1]); pos--)
        {
            if (pos != start + 1 || lastStripped == start + 2 || !IsSeparator(path[start - 1]))
            {
                path = path[..(pos - 1)];
                lastStripped = pos;
            }
        }
        return path;
    }
}
