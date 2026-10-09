using Oriel.Cef.Handlers;

namespace Oriel;

public sealed partial class Session
{
    // UI thread only.
    private readonly Dictionary<uint, DownloadItem> _downloads = [];
    private readonly Dictionary<uint, DownloadControl> _earlyControls = [];

    /// <summary>A page in this session is about to download a file. Set the item's save path to skip the save dialog,
    /// or cancel. Electron's <c>will-download</c> event.</summary>
    public event EventHandler<WillDownloadEventArgs>? WillDownload;

    // A null page is a download the session started itself (DownloadUrl), as Electron's will-download without webContents.
    internal void OnBeforeDownload(WebContents? page, DownloadSnapshot snapshot, BeforeDownload before)
    {
        var item = new DownloadItem(snapshot, before) { Finished = done => _downloads.Remove(done.Id), Session = this };
        _downloads[snapshot.Id] = item;
        if (_earlyControls.Remove(snapshot.Id, out var early)) item.Adopt(early);
        var args = new WillDownloadEventArgs(item, page, DownloadFrame(snapshot.Extras?.FrameIdentifier, WebContents.FromFrameToken));
        try { WillDownload?.Invoke(this, args); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A will-download handler failed: {e}"); }
        item.Start(args.Cancel, page is null ? null : BrowserWindow.FromWebContents(page));
    }

    // CEF's frame identifier carries the render process id and frame token, which find the frame in any page.
    internal static WebFrameMain? DownloadFrame(string? cefFrameIdentifier, Func<int, string, WebFrameMain?> fromFrameToken)
        => FrameIdentity.Parse(cefFrameIdentifier) is { } ids ? fromFrameToken(ids.ProcessId, ids.FrameToken) : null;

    internal void OnDownloadUpdated(DownloadSnapshot snapshot, DownloadControl control)
    {
        if (!_downloads.TryGetValue(snapshot.Id, out var item))
        {
            // CEF may report a download before asking where to save it.
            if (_earlyControls.Remove(snapshot.Id, out var previous)) previous.Release();
            if (DownloadItem.StateOf(snapshot) is DownloadState.Progressing) _earlyControls[snapshot.Id] = control;
            else control.Release();
            return;
        }
        item.OnUpdated(snapshot, control);
    }
}
