using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;

namespace Oriel;

/// <summary>Copying the image under a point to the clipboard.</summary>
public sealed unsafe partial class WebContents
{
    /// <summary>Copies the image at (<paramref name="x"/>, <paramref name="y"/>), DIPs in the view, to the clipboard as
    /// Blink's "Copy Image" does: the decoded image, and with its <c>src</c> a bookmark (not on macOS) and
    /// <c>&lt;img&gt;</c> markup. A canvas the page's script cannot read, or a cross-origin image inside an iframe,
    /// is not copied (drift C48). Electron's <c>webContents.copyImageAt()</c>.</summary>
    public void CopyImageAt(int x, int y) => _ = CopyImageAtAsync(x, y);

    // Completes once the clipboard is written, or nothing was found to copy.
    internal Task CopyImageAtAsync(int x, int y)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (_browser is null || _mainFrameId is not { } mainId || !_frames.TryGetValue(mainId, out var main))
            {
                done.TrySetResult();
                return;
            }
            var zoom = QueryHost(host => Math.Pow(ZoomBase, host->get_zoom_level(host)), 1);
            var expression = $"({CopyImageScript})({(x / zoom).ToString(CultureInfo.InvariantCulture)}, {(y / zoom).ToString(CultureInfo.InvariantCulture)})";
            _devTools.Send("Runtime.evaluate", w =>
            {
                w.WriteString("expression", expression);
                if ((main.IsolatedContext ?? main.DefaultContext) is { } context) w.WriteNumber("contextId", context);
                w.WriteBoolean("returnByValue", true);
            }, (result, error) =>
            {
                var hit = error is null && result.GetProperty("result").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(value.GetRawText()) : null;
                if (hit is null)
                {
                    done.TrySetResult();
                    return;
                }
                var url = (string?)hit["url"] ?? "";
                var alt = (string?)hit["alt"] ?? "";
                if ((string?)hit["data"] is { } dataUrl)
                {
                    WriteCopiedImage(Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]), url, alt, done);
                    return;
                }
                // A cross-origin image taints the canvas; the bytes the page loaded are still DevTools'.
                if ((string?)hit["resource"] is not { Length: > 0 } resource || hit["inMain"]?.GetValue<bool>() != true)
                {
                    done.TrySetResult();
                    return;
                }
                _devTools.Send("Page.getResourceContent", w =>
                {
                    w.WriteString("frameId", mainId);
                    w.WriteString("url", resource);
                }, (content, contentError) =>
                {
                    if (contentError is not null || !content.GetProperty("base64Encoded").GetBoolean())
                    {
                        done.TrySetResult();
                        return;
                    }
                    WriteCopiedImage(Convert.FromBase64String(content.GetProperty("content").GetString()!), url, alt, done);
                });
            });
        });
        return done.Task;
    }

    private static void WriteCopiedImage(byte[] encoded, string url, string alt, TaskCompletionSource done)
    {
        try
        {
            if (ImageCodec.Decode(encoded) is not { } bitmap) return;
            var writer = new ClipboardWriter();
            writer.WriteImage(bitmap);
            if (url.Length > 0)
            {
                // Blink leaves text formats out on macOS, so editors there take the image.
                if (!OperatingSystem.IsMacOS()) writer.WriteUrl(url, alt);
                writer.WriteHtml(ImageMarkup(url, alt));
            }
            Clipboard.Write(writer);
        }
        catch (PlatformNotSupportedException) { }
        finally { done.TrySetResult(); }
    }

    /// <summary>Blink's <c>URLToImageMarkup</c>.</summary>
    internal static string ImageMarkup(string url, string alt)
        => $"<img src=\"{EscapeForHtml(url)}\"" + (alt.Length > 0 ? $" alt=\"{EscapeForHtml(alt)}\"" : "") + "/>";

    // Blink's EscapeForHTML: the five characters markup gives meaning to.
    private static string EscapeForHtml(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    // Blink's hit test down through same-process frames, then ImageFromNode and GetUrlStringFromNode: an <img> or image
    // <input> with its decoded image and src attribute, or a canvas's pixels; an image map's area stands for its image.
    internal const string CopyImageScript = @"(x, y) => {
        let doc = document, px = x, py = y, node = null, inMain = true;
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
            inMain = false;
        }
        if (node && node.tagName === 'AREA') {
            const map = node.closest('map');
            node = map && map.name ? doc.querySelector('img[usemap=""#' + CSS.escape(map.name) + '""]') : null;
        }
        if (!node) return null;
        const isCanvas = node.tagName === 'CANVAS', isImg = node.tagName === 'IMG', isInput = node.tagName === 'INPUT' && node.type === 'image';
        if (!isCanvas && !isImg && !isInput) return null;
        const src = isCanvas ? null : node.getAttribute('src');
        let url = '';
        if (src !== null) { try { url = new URL(src.trim(), doc.baseURI).href; } catch (e) {} }
        const alt = isCanvas ? '' : (node.getAttribute('alt') || '');
        let data = null;
        if (isCanvas) {
            if (!node.width || !node.height) return null;
            try { data = node.toDataURL('image/png'); } catch (e) { return null; }
        } else if (isImg) {
            if (!node.complete || !node.naturalWidth) return null;
            const c = document.createElement('canvas');
            c.width = node.naturalWidth;
            c.height = node.naturalHeight;
            c.getContext('2d').drawImage(node, 0, 0);
            try { data = c.toDataURL('image/png'); } catch (e) {}
        }
        return { data, url, alt, inMain, resource: isCanvas ? '' : (isImg ? node.currentSrc : url) };
    }";
}
