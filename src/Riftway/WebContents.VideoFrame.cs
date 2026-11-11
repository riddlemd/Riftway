using System.Globalization;
using System.Text.Json;
using Riftway.Cef;

namespace Riftway;

/// <summary>Copying or saving the frame a video under a point is showing: Blink's media player actions.</summary>
public sealed unsafe partial class WebContents
{
    /// <summary>Copies the frame the video at (<paramref name="x"/>, <paramref name="y"/>), DIPs in the view, is showing
    /// to the clipboard, at the size it is drawn (its <c>object-fit</c> applied), as Blink's "Copy Video Frame" does.
    /// Nothing is copied where there is no video, or the video is cross-origin without CORS (drift C60). Electron's
    /// <c>webContents.copyVideoFrameAt()</c>.</summary>
    public void CopyVideoFrameAt(int x, int y) => _ = CopyVideoFrameAtAsync(x, y);

    /// <summary>Saves the frame the video at (<paramref name="x"/>, <paramref name="y"/>), DIPs in the view, is showing,
    /// at its own size, as a PNG download named <c>videoframe_</c> and the playback position in milliseconds; the
    /// session's <see cref="Session.WillDownload"/> is raised and, unless a save path is set, the save dialog shown.
    /// Nothing is saved where there is no video, or the video is cross-origin without CORS (drift C60). Electron's
    /// <c>webContents.saveVideoFrameAs()</c>.</summary>
    public void SaveVideoFrameAs(int x, int y) => _ = EvaluateVideoFrameAsync(x, y, save: true);

    // Completes once the clipboard is written, or nothing was found to copy.
    internal Task CopyVideoFrameAtAsync(int x, int y) => EvaluateVideoFrameAsync(x, y, save: false).ContinueWith(t =>
    {
        if (t.Result is not { } dataUrl) return;
        try
        {
            if (ImageCodec.Decode(Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..])) is not { } bitmap) return;
            var writer = new ClipboardWriter();
            writer.WriteImage(bitmap);
            Clipboard.Write(writer);
        }
        catch (PlatformNotSupportedException) { }
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // The frame as a PNG data URL (copy), or null; saving downloads it in the page and gives null.
    private Task<string?> EvaluateVideoFrameAsync(int x, int y, bool save)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (_browser is null || _mainFrameId is not { } mainId || !_frames.TryGetValue(mainId, out var main))
            {
                done.TrySetResult(null);
                return;
            }
            var zoom = QueryHost(host => Math.Pow(ZoomBase, host->get_zoom_level(host)), 1);
            var expression = $"({VideoFrameScript})({(x / zoom).ToString(CultureInfo.InvariantCulture)}, {(y / zoom).ToString(CultureInfo.InvariantCulture)}, {(save ? "true" : "false")})";
            _devTools.Send("Runtime.evaluate", w =>
            {
                w.WriteString("expression", expression);
                if ((main.IsolatedContext ?? main.DefaultContext) is { } context) w.WriteNumber("contextId", context);
                w.WriteBoolean("returnByValue", true);
            }, (result, error) => done.TrySetResult(error is null && result.GetProperty("result").TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null));
        });
        return done.Task;
    }

    // The video under the point, found as CopyImageScript's hit test does; drawn at its drawn size (Blink's
    // ReplacedContentRect, object-fit applied) to copy, or at its own size to save as "videoframe_<position ms>".
    internal const string VideoFrameScript = @"(x, y, save) => {
        let doc = document, px = x, py = y, node = null;
        for (let depth = 0; depth < 32; depth++) {
            node = doc.elementFromPoint(px, py);
            if (!node || !(node.tagName === 'IFRAME' || node.tagName === 'FRAME')) break;
            let inner = null;
            try { inner = node.contentDocument; } catch (e) {}
            if (!inner) return null;
            const r = node.getBoundingClientRect(), s = getComputedStyle(node);
            px -= r.left + node.clientLeft + parseFloat(s.paddingLeft);
            py -= r.top + node.clientTop + parseFloat(s.paddingTop);
            doc = inner;
        }
        if (!node || node.tagName !== 'VIDEO' || !node.videoWidth || !node.videoHeight || node.readyState < 2) return null;
        let w = node.videoWidth, h = node.videoHeight;
        if (!save) {
            const s = getComputedStyle(node);
            const bw = node.clientWidth - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight);
            const bh = node.clientHeight - parseFloat(s.paddingTop) - parseFloat(s.paddingBottom);
            const fit = s.objectFit, contain = Math.min(bw / w, bh / h), cover = Math.max(bw / w, bh / h);
            const scale = fit === 'fill' ? null : fit === 'cover' ? cover : fit === 'none' ? 1 : fit === 'scale-down' ? Math.min(1, contain) : contain;
            [w, h] = scale === null ? [bw, bh] : [w * scale, h * scale];
            w = Math.round(w); h = Math.round(h);
            if (w <= 0 || h <= 0) return null;
        }
        const c = document.createElement('canvas');
        c.width = w; c.height = h;
        c.getContext('2d').drawImage(node, 0, 0, w, h);
        let data;
        try { data = c.toDataURL('image/png'); } catch (e) { return null; }
        if (!save) return data;
        const a = document.createElement('a');
        a.href = data;
        a.download = 'videoframe_' + Math.floor(node.currentTime * 1000) + '.png';
        a.click();
        return null;
    }";
}
