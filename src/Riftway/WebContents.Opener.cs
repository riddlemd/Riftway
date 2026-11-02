using System.Text.Json;
using Riftway.Cef;

namespace Riftway;

/// <summary>The frame that opened the page.</summary>
public sealed unsafe partial class WebContents
{
    // UI thread: DevTools' view of the opener when last read.
    private WebFrameMain? _opener;

    /// <summary>The frame that opened this page with <c>window.open</c> or a link with a target; null for a page nothing
    /// opened, one opened without an opener (<c>noopener</c>, or a <c>target=_blank</c> link), and once the opener frame
    /// has gone. Read from DevTools; on the UI thread it is the value when the page was created, or when last read off it
    /// (drift C49). Electron's <c>webContents.opener</c>.</summary>
    public WebFrameMain? Opener
    {
        get
        {
            if (!CefThread.IsOnUiThread)
            {
                var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                CefThread.RunOnUi(() =>
                {
                    if (_browser is null) read.TrySetResult();
                    else _devTools.Send("Target.getTargetInfo", onResult: (result, error) =>
                    {
                        if (error is null) ReadOpener(result);
                        read.TrySetResult();
                    });
                });
                try { read.Task.Wait(TimeSpan.FromSeconds(10), _destroyed.Token); }
                catch (OperationCanceledException) { }
            }
            return CefThread.InvokeOnUi(() => _opener is { } frame && !frame.Page.IsDestroyed() && !frame.Detached ? frame : null);
        }
    }

    // UI thread: Chromium reports the opener only while the page can still reach it (the frame tree node's opener), by
    // the opener page's target id and the frame's DevTools id.
    private void ReadOpener(JsonElement result)
    {
        if (!result.TryGetProperty("targetInfo", out var info)) return;
        _opener = info.TryGetProperty("canAccessOpener", out var reachable) && reachable.GetBoolean()
            && info.TryGetProperty("openerId", out var openerId) && info.TryGetProperty("openerFrameId", out var frameId)
            && FromDevToolsTargetId(openerId.GetString()!) is { } page && page._frames.TryGetValue(frameId.GetString()!, out var frame)
                ? page.WrapperOf(frame) : null;
    }
}
