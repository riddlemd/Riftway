using System.ComponentModel;
using Oriel.Cef;
using Oriel.Cef.Handlers;

namespace Oriel;

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
public sealed class WillDownloadEventArgs(DownloadItem item, WebContents? webContents) : CancelEventArgs
{
    /// <summary>The download.</summary>
    public DownloadItem Item { get; } = item;

    /// <summary>The page that started it; null for <see cref="Session.DownloadUrl"/>.</summary>
    public WebContents? WebContents { get; } = webContents;
}

/// <summary>A download; Electron's <c>DownloadItem</c>. Its events are raised on the UI thread.</summary>
public sealed class DownloadItem
{
    private DownloadSnapshot _snapshot;
    private BeforeDownload? _before;
    private DownloadControl? _control;
    private string? _savePath;
    private bool _cancelRequested;

    internal DownloadItem(DownloadSnapshot snapshot, BeforeDownload before)
    {
        _snapshot = snapshot;
        _before = before;
    }

    /// <summary>The download progressed or was interrupted. Electron's <c>updated</c> event.</summary>
    public event EventHandler<DownloadStateEventArgs>? Updated;

    /// <summary>The download completed, was cancelled or was interrupted for good. Electron's <c>done</c> event.</summary>
    public event EventHandler<DownloadStateEventArgs>? Done;

    /// <summary>Where to save the file, skipping the save dialog; only honoured during <see cref="Session.WillDownload"/>.
    /// Chromium creates missing directories. Electron's <c>downloadItem.setSavePath()</c>.</summary>
    public void SetSavePath(string path)
    {
        if (_before is not null) _savePath = path;
    }

    /// <summary>The file's path: the one set, the one chosen in the dialog, or empty until then. Electron's
    /// <c>downloadItem.getSavePath()</c>.</summary>
    public string GetSavePath() => _savePath ?? _snapshot.FullPath;

    /// <summary>The save dialog's options when no save path is set. Electron's <c>downloadItem.setSaveDialogOptions()</c>.</summary>
    public void SetSaveDialogOptions(SaveDialogOptions options) => SaveDialogOptions = options;

    /// <summary>Electron's <c>downloadItem.getSaveDialogOptions()</c>.</summary>
    public SaveDialogOptions GetSaveDialogOptions() => SaveDialogOptions;

    internal SaveDialogOptions SaveDialogOptions { get; private set; } = new();

    /// <summary>Electron's <c>downloadItem.pause()</c>.</summary>
    public void Pause() => CefThread.RunOnUi(() => _control?.Pause());

    /// <summary>Electron's <c>downloadItem.isPaused()</c>.</summary>
    public bool IsPaused() => _snapshot.IsPaused;

    /// <summary>Electron's <c>downloadItem.resume()</c>.</summary>
    public void Resume() => CefThread.RunOnUi(() => _control?.Resume());

    /// <summary>Whether <see cref="Resume"/> can continue the download: true while paused. Electron's
    /// <c>downloadItem.canResume()</c>.</summary>
    public bool CanResume() => _snapshot.IsPaused;

    /// <summary>Electron's <c>downloadItem.cancel()</c>.</summary>
    public void Cancel() => CefThread.RunOnUi(() =>
    {
        if (_before is not null) _cancelRequested = true;
        else _control?.Cancel();
    });

    /// <summary>Electron's <c>downloadItem.getURL()</c>.</summary>
    public string GetUrl() => _snapshot.Url;

    /// <summary>Electron's <c>downloadItem.getURLChain()</c>: the original URL, then the final one after redirects.</summary>
    public IReadOnlyList<string> GetUrlChain()
        => _snapshot.OriginalUrl.Length == 0 || _snapshot.OriginalUrl == _snapshot.Url ? [_snapshot.Url] : [_snapshot.OriginalUrl, _snapshot.Url];

    /// <summary>Electron's <c>downloadItem.getMimeType()</c>.</summary>
    public string GetMimeType() => _snapshot.MimeType;

    /// <summary>The file's name: the saved file's once its path is known, the suggested one before. Electron's
    /// <c>downloadItem.getFilename()</c>.</summary>
    public string GetFilename()
        => GetSavePath() is { Length: > 0 } path ? Path.GetFileName(path) : _snapshot.SuggestedFileName;

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
    internal void Start(bool cancelled, BrowserWindow? window)
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
        var options = SaveDialogOptions;
        if (string.IsNullOrEmpty(options.DefaultPath))
            options = options with { DefaultPath = Path.Combine(DownloadsDirectory, _snapshot.SuggestedFileName) };
        Dialog.ShowSaveDialogAsync(window, options).ContinueWith(t => CefThread.RunOnUi(() =>
        {
            if (t.IsCompletedSuccessfully && !t.Result.Canceled && t.Result.FilePath is { Length: > 0 } chosen && !_cancelRequested)
                Save(before, chosen);
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
        if (state is DownloadState.Progressing) Updated?.Invoke(this, new DownloadStateEventArgs(state));
        else
        {
            control.Release();
            _control = null;
            Complete(state);
        }
    }

    private void Complete(DownloadState state)
    {
        Finished?.Invoke(this);
        Done?.Invoke(this, new DownloadStateEventArgs(state));
    }

    internal uint Id => _snapshot.Id;

    internal Action<DownloadItem>? Finished { get; init; }

    /// <summary>Where the save dialog starts; Electron's <c>app.getPath('downloads')</c>.</summary>
    internal static string DownloadsDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
