using Riftway.Cef;

namespace Riftway;

/// <summary>The page's DevTools target id.</summary>
public sealed unsafe partial class WebContents
{
    // Chromium's agent host for a page follows its main frame tree node, so the id lasts as long as the page.
    private string? _devToolsTargetId;
    private readonly TaskCompletionSource _devToolsTargetIdRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The page's Chrome DevTools Protocol target id: what <c>Target.getTargetInfo</c> and the
    /// <c>remote-debugging-port</c> list report for it. Read when the page is created; called on the UI thread before
    /// that, it is empty (drift C46). Electron's <c>webContents.getOrCreateDevToolsTargetId()</c>.</summary>
    public string GetOrCreateDevToolsTargetId()
    {
        if (!CefThread.IsOnUiThread)
        {
            try { _devToolsTargetIdRead.Task.Wait(TimeSpan.FromSeconds(10), _destroyed.Token); }
            catch (OperationCanceledException) { }
        }
        return CefThread.InvokeOnUi(() => _devToolsTargetId ?? string.Empty);
    }

    /// <summary>The page whose DevTools target id is <paramref name="targetId"/>, or null. Electron's
    /// <c>webContents.fromDevToolsTargetId()</c>.</summary>
    public static WebContents? FromDevToolsTargetId(string targetId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        return CefThread.InvokeOnUi(() => GetAllWebContents().FirstOrDefault(w => w._devToolsTargetId == targetId));
    }

    // UI thread, once the DevTools channel is attached.
    private void ReadDevToolsTargetId() => _devTools.Send("Target.getTargetInfo", onResult: (result, error) =>
    {
        if (error is null && result.TryGetProperty("targetInfo", out var info)) _devToolsTargetId = info.GetProperty("targetId").GetString();
        if (error is null) ReadOpener(result);
        _devToolsTargetIdRead.TrySetResult();
    });
}
