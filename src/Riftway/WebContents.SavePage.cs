using System.Text;
using System.Text.Json;
using Riftway.Cef;

namespace Riftway;

/// <summary>How <see cref="WebContents.SavePageAsync"/> saves the page; Electron's <c>saveType</c>.</summary>
public enum SavePageType
{
    /// <summary>The page's own HTML only. Electron's <c>HTMLOnly</c>.</summary>
    HtmlOnly,

    /// <summary>The page's HTML, with its images, style sheets and scripts beside it in a <c>name_files</c> folder.
    /// Electron's <c>HTMLComplete</c>.</summary>
    HtmlComplete,

    /// <summary>The whole page in one MHTML file. Electron's <c>MHTML</c>.</summary>
    Mhtml,
}

/// <summary>Saving the page to disk.</summary>
public sealed unsafe partial class WebContents
{
    private const string SavePageFailed = "Failed to save the page";

    /// <summary>Saves the page to <paramref name="fullPath"/>: its HTML as loaded, the page and its resources as
    /// Chromium's "Webpage, Complete" lays them out, or one MHTML file from Blink's serializer. Electron's
    /// <c>webContents.savePage()</c>; through DevTools (drift C50).</summary>
    /// <exception cref="InvalidOperationException">The path is not absolute, or the page could not be saved; with
    /// Electron's messages.</exception>
    public Task SavePageAsync(string fullPath, SavePageType saveType)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (!Path.IsPathFullyQualified(fullPath)) return Task.FromException(new InvalidOperationException("Path must be absolute"));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Fail() => done.TrySetException(new InvalidOperationException(SavePageFailed));
        CefThread.RunOnUi(() =>
        {
            if (_browser is null || _mainFrameId is not { } mainId || !_frames.TryGetValue(mainId, out var main))
            {
                Fail();
                return;
            }
            switch (saveType)
            {
                case SavePageType.Mhtml:
                    _devTools.Send("Page.captureSnapshot", w => w.WriteString("format", "mhtml"), (result, error) =>
                    {
                        if (error is null && TryWrite(fullPath, Encoding.UTF8.GetBytes(result.GetProperty("data").GetString() ?? ""))) done.TrySetResult();
                        else Fail();
                    });
                    break;
                case SavePageType.HtmlOnly:
                    DownloadResource(mainId, main.Url.Split('#')[0], bytes =>
                    {
                        if (bytes is not null && TryWrite(fullPath, bytes)) done.TrySetResult();
                        else Fail();
                    });
                    break;
                default:
                    SaveComplete(main, fullPath, done);
                    break;
            }
        });
        return done.Task;
    }

    private static bool TryWrite(string path, byte[] bytes)
    {
        try
        {
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // UI thread: the bytes the frame loaded for url, null when DevTools has none.
    private void ReadResource(string frameId, string url, Action<byte[]?> then) => _devTools.Send("Page.getResourceContent", w =>
    {
        w.WriteString("frameId", frameId);
        w.WriteString("url", url);
    }, (result, error) =>
    {
        if (error is not null)
        {
            then(null);
            return;
        }
        var content = result.GetProperty("content").GetString() ?? "";
        then(result.GetProperty("base64Encoded").GetBoolean() ? Convert.FromBase64String(content) : Encoding.UTF8.GetBytes(content));
    });

    // UI thread: SavePackage's HTML-only save downloads the URL again, preferring the cache. Chromium keeps no copy of a
    // main document's bytes for DevTools (getResourceContent: "Resource was not cached").
    private void DownloadResource(string frameId, string url, Action<byte[]?> then)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            Task.Run(() => File.ReadAllBytes(uri.LocalPath)).ContinueWith(t => CefThread.RunOnUi(() => then(t.IsCompletedSuccessfully ? t.Result : null)), TaskScheduler.Default);
            return;
        }
        _devTools.Send("Network.loadNetworkResource", w =>
        {
            w.WriteString("frameId", frameId);
            w.WriteString("url", url);
            w.WriteStartObject("options");
            w.WriteBoolean("disableCache", false);
            w.WriteBoolean("includeCredentials", true);
            w.WriteEndObject();
        }, (result, error) =>
        {
            if (error is not null || !result.TryGetProperty("resource", out var resource) || !resource.GetProperty("success").GetBoolean()
                || !resource.TryGetProperty("stream", out var stream))
            {
                then(null);
                return;
            }
            var handle = stream.GetString()!;
            var data = new MemoryStream();
            void ReadNext() => _devTools.Send("IO.read", w =>
            {
                w.WriteString("handle", handle);
                w.WriteNumber("size", 1 << 18);
            }, (chunk, readError) =>
            {
                if (readError is not null)
                {
                    _devTools.Send("IO.close", w => w.WriteString("handle", handle));
                    then(null);
                    return;
                }
                var text = chunk.GetProperty("data").GetString() ?? "";
                data.Write(chunk.TryGetProperty("base64Encoded", out var b64) && b64.GetBoolean() ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text));
                if (!chunk.GetProperty("eof").GetBoolean())
                {
                    ReadNext();
                    return;
                }
                _devTools.Send("IO.close", w => w.WriteString("handle", handle));
                then(data.ToArray());
            });
            ReadNext();
        });
    }

    // UI thread: SavePackage's layout: the serialized document with its resource links pointing into "<name>_files".
    private void SaveComplete(FrameState main, string fullPath, TaskCompletionSource done)
    {
        var folderName = Path.GetFileNameWithoutExtension(fullPath) + "_files";
        var folder = Path.Combine(Path.GetDirectoryName(fullPath)!, folderName);
        var expression = $"({SavePageScript})(\"{JsonEncodedText.Encode(folderName)}\")";
        _devTools.Send("Runtime.evaluate", w =>
        {
            w.WriteString("expression", expression);
            if ((main.IsolatedContext ?? main.DefaultContext) is { } context) w.WriteNumber("contextId", context);
            w.WriteBoolean("returnByValue", true);
        }, (result, error) =>
        {
            if (error is not null || !result.GetProperty("result").TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                done.TrySetException(new InvalidOperationException(SavePageFailed));
                return;
            }
            var html = value.GetProperty("html").GetString() ?? "";
            var files = value.GetProperty("files").EnumerateArray().Select(e => (Url: e[0].GetString()!, Name: e[1].GetString()!)).ToList();
            var pending = files.Count;
            var failed = false;
            void Finish()
            {
                if (failed || !TryWrite(fullPath, Encoding.UTF8.GetBytes(html))) done.TrySetException(new InvalidOperationException(SavePageFailed));
                else done.TrySetResult();
            }
            if (pending == 0)
            {
                Finish();
                return;
            }
            try { Directory.CreateDirectory(folder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                done.TrySetException(new InvalidOperationException(SavePageFailed));
                return;
            }
            foreach (var (url, name) in files)
                ReadResource(main.Id, url, bytes =>
                {
                    // A resource DevTools no longer holds is left out, as SavePackage skips one that fails to load.
                    if (bytes is not null && !TryWrite(Path.Combine(folder, name), bytes)) failed = true;
                    if (--pending == 0) Finish();
                });
        });
    }

    // Blink's serializer for SavePackage: the mark of the web, then the document with each saved resource's link
    // pointing at its file. Names come from the URL's last segment, made unique as SavePackage does: "a(1).png".
    internal const string SavePageScript = @"(folder) => {
        const saved = new Map(), names = new Set(), files = [];
        const nameFor = (url) => {
            if (saved.has(url)) return saved.get(url);
            let base = 'file';
            try { base = decodeURIComponent(new URL(url).pathname.split('/').pop()) || 'file'; } catch (e) {}
            base = base.replace(/[\\/:*?""<>|]/g, '_');
            const dot = base.lastIndexOf('.');
            const stem = dot > 0 ? base.slice(0, dot) : base, ext = dot > 0 ? base.slice(dot) : '';
            let name = base;
            for (let i = 1; names.has(name.toLowerCase()); i++) name = stem + '(' + i + ')' + ext;
            names.add(name.toLowerCase());
            saved.set(url, name);
            files.push([url, name]);
            return name;
        };
        const copy = document.documentElement.cloneNode(true);
        const original = [...document.documentElement.querySelectorAll('*')];
        const cloned = [...copy.querySelectorAll('*')];
        original.forEach((el, i) => {
            const target = cloned[i];
            const link = (attr) => {
                const url = el[attr];
                if (typeof url !== 'string' || !/^(https?|file|data):/.test(url) || url.startsWith('data:')) return;
                target.setAttribute(attr, './' + folder + '/' + nameFor(url));
            };
            const tag = el.tagName;
            if ((tag === 'IMG' || tag === 'SCRIPT' || (tag === 'INPUT' && el.type === 'image')) && el.getAttribute('src') !== null) link('src');
            else if (tag === 'LINK' && /\bstylesheet\b/i.test(el.rel) && el.getAttribute('href') !== null) link('href');
            if (tag === 'IMG') target.removeAttribute('srcset');
        });
        const doctype = document.doctype ? '<!DOCTYPE ' + document.doctype.name + '>' : '';
        const url = document.URL;
        const mark = '\n<!-- saved from url=(' + String(url.length).padStart(4, '0') + ')' + url + ' -->\n';
        return { html: doctype + mark + copy.outerHTML, files };
    }";
}
