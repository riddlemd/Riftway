using System.Runtime.Versioning;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>Chromium's <c>download::ResumeMode</c>.</summary>
internal enum DownloadResumeMode
{
    Invalid,
    ImmediateContinue,
    ImmediateRestart,
    UserContinue,
    UserRestart,
}

/// <summary>Chromium's and Electron's download rules that CEF's download item does not carry.</summary>
internal static class DownloadRules
{
    /// <summary>Chromium's <c>GetDownloadResumeMode</c> (download_utils.cc), desktop branch.</summary>
    internal static DownloadResumeMode ResumeModeOf(string url, cef_download_interrupt_reason_t reason, bool restartRequired, bool userActionRequired)
    {
        if (!IsHttpOrHttps(url)) return DownloadResumeMode.Invalid;
        switch (reason)
        {
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_TIMEOUT:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_TRANSIENT_ERROR:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_CONTENT_LENGTH_MISMATCH:
                break;
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_NO_RANGE:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_HASH_MISMATCH:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_TOO_SHORT:
                restartRequired = true;
                break;
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_DISCONNECTED:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_SERVER_DOWN:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_FAILED:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_UNREACHABLE:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_CRASH:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_NO_SPACE:
                userActionRequired = true;
                break;
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_FAILED:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_ACCESS_DENIED:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_NAME_TOO_LONG:
            case cef_download_interrupt_reason_t.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_TOO_LARGE:
                userActionRequired = true;
                restartRequired = true;
                break;
            default:
                return DownloadResumeMode.Invalid;
        }
        if (userActionRequired && restartRequired) return DownloadResumeMode.UserRestart;
        if (restartRequired) return DownloadResumeMode.ImmediateRestart;
        if (userActionRequired) return DownloadResumeMode.UserContinue;
        return DownloadResumeMode.ImmediateContinue;
    }

    // GURL lowercases the scheme, and CEF hands over the canonical spec.
    private static bool IsHttpOrHttps(string url)
        => url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal);

    /// <summary>Chromium's <c>IsDownloadDone</c>: an interruption Chromium could resume is not the end, so Electron
    /// raises <c>updated</c> for it rather than <c>done</c>.</summary>
    internal static bool IsDone(DownloadSnapshot snapshot) => DownloadItem.StateOf(snapshot) switch
    {
        DownloadState.Completed or DownloadState.Cancelled => true,
        DownloadState.Interrupted => ResumeModeOf(snapshot.Url, snapshot.InterruptReason, false, false) == DownloadResumeMode.Invalid,
        _ => false,
    };

    /// <summary><c>DownloadItemImpl::CanResume</c>: a running download while paused; an interrupted one when only the user
    /// may resume it. Chromium also counts its automatic attempts, which CEF does not report (DRIFT E14).</summary>
    internal static bool CanResume(DownloadSnapshot snapshot) => DownloadItem.StateOf(snapshot) switch
    {
        DownloadState.Progressing => snapshot.IsPaused,
        DownloadState.Interrupted => ResumeModeOf(snapshot.Url, snapshot.InterruptReason, false, snapshot.IsPaused)
            is DownloadResumeMode.UserContinue or DownloadResumeMode.UserRestart,
        _ => false,
    };

    /// <summary>The save dialog Electron's <c>OnDownloadPathGenerated</c> shows: the item's options, titled with the URL
    /// and starting at the generated path when they leave those empty; on Windows, with no filters, one for the
    /// path's extension and All Files.</summary>
    internal static SaveDialogOptions DialogOptionsFor(SaveDialogOptions options, string url, string generatedPath, bool windows, Func<string, string?> describe)
    {
        if (string.IsNullOrEmpty(options.Title)) options = options with { Title = url };
        if (string.IsNullOrEmpty(options.DefaultPath)) options = options with { DefaultPath = generatedPath };
        if (windows && options.Filters.Count == 0 && FinalExtension(options.DefaultPath!) is { Length: > 0 } extension)
            options = options with { Filters = FiltersFor(extension, describe) };
        return options;
    }

    /// <summary><c>base::FilePath::FinalExtension</c>: from the base name's last dot, the dot included.</summary>
    internal static string FinalExtension(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name is "." or "..") return "";
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..];
    }

    /// <summary>Electron's Windows <c>FormatFilterForExtensions({extension}, {""}, true, true)</c>, with Chromium's
    /// en-US strings.</summary>
    internal static IReadOnlyList<FileFilter> FiltersFor(string extension, Func<string, string?> describe)
    {
        var first = extension[extension.IndexOf('.')..];
        if (first.IndexOf(';') is var separator and >= 0) first = first[..separator];
        var name = first.TrimStart('.') is { Length: > 0 } trimmed ? trimmed : first;
        var description = describe(first) ?? $"{name.ToUpperInvariant()} File";
        if (description.Length == 0) description = "*." + name;
        var extensionName = extension.TrimStart('.') is { Length: > 0 } bare ? bare : extension;
        return [new FileFilter(description, extensionName), new FileFilter("All Files", "*")];
    }

    /// <summary>The registry's description of a file type (".txt" gives "Text Document"), as Electron's
    /// <c>GetRegistryDescriptionFromExtension</c>; null when it has none.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? RegistryDescription(string extension)
    {
        try
        {
            using var type = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(extension);
            if (type?.GetValue(null) is not string { Length: > 0 } progId) return null;
            using var link = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId);
            return link?.GetValue(null) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
