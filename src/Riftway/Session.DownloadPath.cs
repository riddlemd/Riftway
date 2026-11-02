using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway;

public sealed unsafe partial class Session
{
    private const string DownloadDirectoryPreference = "download.default_directory";

    // Electron registers download.default_directory from DIR_DEFAULT_DOWNLOADS when the profile is made, so a later
    // app.setPath('downloads') leaves an existing session alone.
    private readonly string? _downloadsAtCreation;

    // ElectronDownloadManagerDelegate::last_saved_directory_: where this session's save dialog last saved.
    private string? _lastSavedDirectory;

    /// <summary>Sets the folder downloads save to when nothing else says where; kept in the profile, so a persistent
    /// session remembers it. Electron's <c>ses.setDownloadPath()</c>.</summary>
    public void SetDownloadPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        CefThread.InvokeOnUi(() =>
        {
            var value = CefNative.cef_value_create();
            cef_string_utf16_t text = default;
            CefString.Set(&text, path);
            value->set_string(value, &text);
            CefString.Clear(&text);
            SetPreference(_context, DownloadDirectoryPreference, value);
            return true;
        });
    }

    /// <summary>Where a download's save dialog starts, as Electron's CreateDownloadPath: the last folder saved to while
    /// it exists, else the profile's download folder, which is made if missing. UI thread.</summary>
    internal string DownloadDirectoryForDialog()
    {
        if (_lastSavedDirectory is { } last && Directory.Exists(last)) return last;
        var directory = UserDownloadDirectory() ?? _downloadsAtCreation ?? DownloadItem.DownloadsDirectory;
        try { Directory.CreateDirectory(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return directory;
    }

    internal void NoteSavedDirectory(string? directory)
    {
        if (!string.IsNullOrEmpty(directory)) _lastSavedDirectory = directory;
    }

    // The preference only when set for this profile: Chromium's own default is DIR_DEFAULT_DOWNLOADS, which
    // app.setPath('downloads') does not reach. UI thread.
    private string? UserDownloadDirectory()
    {
        var manager = (cef_preference_manager_t*)_context;
        var all = manager->get_all_preferences(manager, 0);
        if (all is null) return null;
        try
        {
            cef_string_utf16_t key = default;
            CefString.Set(&key, "download");
            var download = all->get_dictionary(all, &key);
            CefString.Clear(&key);
            if (download is null) return null;
            try
            {
                CefString.Set(&key, "default_directory");
                var set = download->has_key(download, &key) != 0 ? CefString.ReadUserFree(download->get_string(download, &key)) : null;
                CefString.Clear(&key);
                return set is { Length: > 0 } ? set : null;
            }
            finally { NativeObject.Release(download); }
        }
        finally { NativeObject.Release(all); }
    }
}
