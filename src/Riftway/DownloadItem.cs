using System.ComponentModel;
using Riftway.Cef;
using Riftway.Cef.Handlers;

namespace Riftway;

/// <summary>A download's state; Electron's <c>downloadItem.getState()</c> values.</summary>
public enum DownloadState
{
    /// <summary>In progress, or paused.</summary>
    Progressing,
    /// <summary>Saved.</summary>
    Completed,
    /// <summary>Cancelled by the app, the user or the save dialog.</summary>
    Cancelled,
    /// <summary>Stopped by an error.</summary>
    Interrupted,
}

/// <summary>Arguments for <see cref="DownloadItem.Updated"/> and <see cref="DownloadItem.Done"/>.</summary>
public sealed class DownloadStateEventArgs(DownloadState state) : EventArgs
{
    /// <summary>The download's state.</summary>
    public DownloadState State { get; } = state;
}

/// <summary>Arguments for <see cref="Session.WillDownload"/>; set <see cref="CancelEventArgs.Cancel"/> to cancel the
/// download, Electron's <c>event.preventDefault()</c>.</summary>
public sealed class WillDownloadEventArgs(DownloadItem item, WebContents? webContents, WebFrameMain? frame = null) : CancelEventArgs
{
    /// <summary>The download.</summary>
    public DownloadItem Item { get; } = item;

    /// <summary>The page that started it; null for <see cref="Session.DownloadUrl"/>.</summary>
    public WebContents? WebContents { get; } = webContents;

    /// <summary>The frame that started it; null when it is unknown or gone, and always with stock CEF (DRIFT E40).
    /// Electron's <c>will-download</c> <c>frame</c>.</summary>
    public WebFrameMain? Frame { get; } = frame;
}

/// <summary>A download; Electron's <c>DownloadItem</c>. Its events are raised on the UI thread.</summary>
public sealed class DownloadItem
{
    private DownloadSnapshot _snapshot;
    private BeforeDownload? _before;
    private DownloadControl? _control;
    private string? _savePath;
    private bool _cancelRequested;
    // A pause (true) or resume (false) asked before CEF gave a control or while the target is still pending.
    private bool? _pauseRequested;
    // CEF's net::GenerateFileName result; later reports carry only the raw suggested name.
    private readonly string _filename;

    internal DownloadItem(DownloadSnapshot snapshot, BeforeDownload before)
    {
        _snapshot = snapshot;
        _before = before;
        _filename = snapshot.SuggestedFileName;
    }

    /// <summary>The download progressed, or was interrupted in a way it can resume from. Electron's <c>updated</c> event.</summary>
    public event EventHandler<DownloadStateEventArgs>? Updated;

    /// <summary>The download completed, was cancelled or was interrupted in a way it cannot resume from. Electron's
    /// <c>done</c> event.</summary>
    public event EventHandler<DownloadStateEventArgs>? Done;

    /// <summary>Where to save the file, skipping the save dialog; only honoured during <see cref="Session.WillDownload"/>,
    /// though <see cref="GetSavePath"/> reports it whenever it is set. Chromium creates missing directories. Electron's
    /// <c>downloadItem.setSavePath()</c>.</summary>
    public void SetSavePath(string path) => _savePath = path;

    /// <summary>The path set, or the one chosen in the dialog; empty until then. Electron's
    /// <c>downloadItem.getSavePath()</c>.</summary>
    public string GetSavePath() => _savePath ?? "";

    /// <summary>Electron's <c>downloadItem.savePath</c> property.</summary>
    public string SavePath
    {
        get => GetSavePath();
        set => SetSavePath(value);
    }

    /// <summary>The save dialog's options when no save path is set. Electron's <c>downloadItem.setSaveDialogOptions()</c>.</summary>
    public void SetSaveDialogOptions(SaveDialogOptions options) => SaveDialogOptions = options;

    /// <summary>Electron's <c>downloadItem.getSaveDialogOptions()</c>.</summary>
    public SaveDialogOptions GetSaveDialogOptions() => SaveDialogOptions;

    internal SaveDialogOptions SaveDialogOptions { get; private set; } = new();

    /// <summary>Electron's <c>downloadItem.pause()</c>.</summary>
    public void Pause() => CefThread.RunOnUi(() => RequestPause(true));

    /// <summary>Electron's <c>downloadItem.isPaused()</c>.</summary>
    public bool IsPaused() => _snapshot.IsPaused;

    /// <summary>Continues a paused download, or one interrupted in a way it can resume from. Electron's
    /// <c>downloadItem.resume()</c>.</summary>
    public void Resume() => CefThread.RunOnUi(() => RequestPause(false));

    /// <summary>True while paused, or when interrupted and only the user may resume it. Electron's
    /// <c>downloadItem.canResume()</c>.</summary>
    public bool CanResume() => DownloadRules.CanResume(_snapshot);

    /// <summary>Electron's <c>downloadItem.cancel()</c>.</summary>
    public void Cancel() => CefThread.RunOnUi(RequestCancel);

    // UI thread. Until the save path is decided, CEF's control would spin re-posting the pause.
    internal void RequestPause(bool pause)
    {
        if (_before is null && _control is { } control)
        {
            if (pause) control.Pause();
            else control.Resume();
        }
        else _pauseRequested = pause;
    }

    // UI thread.
    internal void RequestCancel()
    {
        if (_before is null && _control is { } control) control.Cancel();
        else _cancelRequested = true;
    }

    /// <summary>Electron's <c>downloadItem.getURL()</c>.</summary>
    public string GetUrl() => _snapshot.Url;

    /// <summary>Electron's <c>downloadItem.getURLChain()</c>: the original URL, then the final one after redirects.</summary>
    public IReadOnlyList<string> GetUrlChain()
        => _snapshot.OriginalUrl.Length == 0 || _snapshot.OriginalUrl == _snapshot.Url ? [_snapshot.Url] : [_snapshot.OriginalUrl, _snapshot.Url];

    /// <summary>Electron's <c>downloadItem.getMimeType()</c>.</summary>
    public string GetMimeType() => _snapshot.MimeType;

    /// <summary>The name made from the URL, Content-Disposition, suggested name and MIME type; a name chosen in the save
    /// dialog does not change it. Electron's <c>downloadItem.getFilename()</c>.</summary>
    public string GetFilename() => _filename;

    /// <summary>Whether a user gesture started the download; always false with stock CEF (DRIFT E13). Electron's
    /// <c>downloadItem.hasUserGesture()</c>.</summary>
    public bool HasUserGesture() => _snapshot.Extras?.HasUserGesture ?? false;

    /// <summary>The response's Last-Modified; always empty with stock CEF (DRIFT E13). Electron's
    /// <c>downloadItem.getLastModifiedTime()</c>.</summary>
    public string GetLastModifiedTime() => _snapshot.Extras?.LastModifiedTime ?? "";

    /// <summary>The response's ETag; always empty with stock CEF (DRIFT E13). Electron's <c>downloadItem.getETag()</c>.</summary>
    public string GetETag() => _snapshot.Extras?.ETag ?? "";

    /// <summary>The origin that started the download, <c>null</c> for an opaque one, empty for none; always empty with
    /// stock CEF (DRIFT E40). Electron's <c>downloadItem.getInitiatorOrigin()</c>.</summary>
    public string GetInitiatorOrigin() => _snapshot.Extras?.InitiatorOrigin ?? "";

    /// <summary>Electron's <c>downloadItem.getContentDisposition()</c>.</summary>
    public string GetContentDisposition() => _snapshot.ContentDisposition;

    /// <summary>Electron's <c>downloadItem.getCurrentBytesPerSecond()</c>.</summary>
    public long GetCurrentBytesPerSecond() => _snapshot.CurrentSpeed;

    /// <summary>0 when the size is unknown. Electron's <c>downloadItem.getTotalBytes()</c>.</summary>
    public long GetTotalBytes() => _snapshot.TotalBytes;

    /// <summary>Electron's <c>downloadItem.getReceivedBytes()</c>.</summary>
    public long GetReceivedBytes() => _snapshot.ReceivedBytes;

    /// <summary>-1 when the size is unknown. Electron's <c>downloadItem.getPercentComplete()</c>.</summary>
    public int GetPercentComplete() => _snapshot.PercentComplete;

    /// <summary>Electron's <c>downloadItem.getState()</c>.</summary>
    public DownloadState GetState() => StateOf(_snapshot);

    /// <summary>Seconds since the Unix epoch. Electron's <c>downloadItem.getStartTime()</c>.</summary>
    public double GetStartTime() => _snapshot.StartTime == 0 ? 0 : Cookies.FromBaseTime(_snapshot.StartTime);

    /// <summary>Seconds since the Unix epoch; 0 until the download ends. Electron's <c>downloadItem.getEndTime()</c>.</summary>
    public double GetEndTime() => _snapshot.EndTime == 0 ? 0 : Cookies.FromBaseTime(_snapshot.EndTime);

    internal static DownloadState StateOf(DownloadSnapshot snapshot)
        => snapshot.IsComplete ? DownloadState.Completed
            : snapshot.IsCanceled ? DownloadState.Cancelled
            : snapshot.IsInterrupted ? DownloadState.Interrupted
            : DownloadState.Progressing;

    /// <summary>UI thread: what <see cref="Session.WillDownload"/> decided. The before-download callback is answered here.</summary>
    internal void Start(bool cancelled, BaseWindow? window)
    {
        var before = _before!;
        if (cancelled || _cancelRequested)
        {
            Finish(before);
            return;
        }
        if (_savePath is { } path)
        {
            Save(before, path);
            return;
        }
        var generated = string.IsNullOrEmpty(SaveDialogOptions.DefaultPath)
            ? Path.Combine(Session?.DownloadDirectoryForDialog() ?? DownloadsDirectory, _filename)
            : "";
        var options = DownloadRules.DialogOptionsFor(SaveDialogOptions, _snapshot.Url, generated, OperatingSystem.IsWindows(),
            extension => OperatingSystem.IsWindows() ? DownloadRules.RegistryDescription(extension) : null);
        Dialog.ShowSaveDialogAsync(window, options).ContinueWith(t => CefThread.RunOnUi(() =>
        {
            if (t.IsCompletedSuccessfully && !t.Result.Canceled && t.Result.FilePath is { Length: > 0 } chosen && !_cancelRequested)
            {
                Session?.NoteSavedDirectory(Path.GetDirectoryName(chosen));
                Save(before, chosen);
            }
            else
                Finish(before);
        }), TaskScheduler.Default);
    }

    private void Save(BeforeDownload before, string path)
    {
        _savePath = path;
        _before = null;
        before.Continue(path, false);
    }

    // Dropping the callback cancels the download; CEF then reports it cancelled.
    private void Finish(BeforeDownload before)
    {
        _before = null;
        before.Cancel();
    }

    /// <summary>UI thread: CEF's latest report; the item owns <paramref name="control"/>.</summary>
    internal void OnUpdated(DownloadSnapshot snapshot, DownloadControl control)
    {
        _control?.Release();
        _control = control;
        _snapshot = snapshot;
        var state = StateOf(snapshot);
        if (DownloadRules.IsDone(snapshot))
        {
            control.Release();
            _control = null;
            Complete(state);
            return;
        }
        if (_before is null) ApplyRequests(control);
        Updated?.Invoke(this, new DownloadStateEventArgs(state));
    }

    /// <summary>UI thread: a control CEF gave before <see cref="Session.WillDownload"/>; the item owns it.</summary>
    internal void Adopt(DownloadControl control)
    {
        _control?.Release();
        _control = control;
    }

    private void ApplyRequests(DownloadControl control)
    {
        if (_cancelRequested) control.Cancel();
        else if (_pauseRequested is { } pause)
        {
            if (pause) control.Pause();
            else control.Resume();
        }
        _cancelRequested = false;
        _pauseRequested = null;
    }

    private void Complete(DownloadState state)
    {
        Finished?.Invoke(this);
        Done?.Invoke(this, new DownloadStateEventArgs(state));
    }

    internal uint Id => _snapshot.Id;

    internal Action<DownloadItem>? Finished { get; init; }

    // The session whose download folder the save dialog starts in.
    internal Session? Session { get; init; }

    /// <summary>Where the save dialog starts; Electron's <c>app.getPath('downloads')</c>.</summary>
    internal static string DownloadsDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
