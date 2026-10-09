using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A download's state when CEF last reported it; CEF's item is valid only during its callback.</summary>
public sealed record DownloadSnapshot(uint Id, string Url, string OriginalUrl, string SuggestedFileName, string ContentDisposition,
    string MimeType, string FullPath, long TotalBytes, long ReceivedBytes, long CurrentSpeed, int PercentComplete,
    long StartTime, long EndTime, bool IsInProgress, bool IsComplete, bool IsCanceled, bool IsInterrupted, bool IsPaused,
    cef_download_interrupt_reason_t InterruptReason)
{
    /// <summary>The fields Oriel's CEF patch 0005 adds; null with stock CEF.</summary>
    public DownloadItemExtras? Extras { get; init; }

    internal static unsafe DownloadSnapshot Read(cef_download_item_t* item) => new(
        item->get_id(item),
        CefString.ReadUserFree(item->get_url(item)),
        CefString.ReadUserFree(item->get_original_url(item)),
        CefString.ReadUserFree(item->get_suggested_file_name(item)),
        CefString.ReadUserFree(item->get_content_disposition(item)),
        CefString.ReadUserFree(item->get_mime_type(item)),
        CefString.ReadUserFree(item->get_full_path(item)),
        item->get_total_bytes(item),
        item->get_received_bytes(item),
        item->get_current_speed(item),
        item->get_percent_complete(item),
        item->get_start_time(item).val,
        item->get_end_time(item).val,
        item->is_in_progress(item) != 0,
        item->is_complete(item) != 0,
        item->is_canceled(item) != 0,
        item->is_interrupted(item) != 0,
        item->is_paused(item) != 0,
        item->get_interrupt_reason(item))
    {
        Extras = CefPatchedApi.ReadDownloadExtras(item),
    };
}

/// <summary>Continues or cancels a download CEF is about to start. Dropping it unanswered cancels the download.</summary>
public sealed unsafe class BeforeDownload
{
    private cef_before_download_callback_t* _callback;

    internal BeforeDownload(cef_before_download_callback_t* callback) => _callback = callback;

    /// <summary>UI thread: saves to <paramref name="path"/>, or shows CEF's save dialog when it is empty and
    /// <paramref name="showDialog"/> is set.</summary>
    public void Continue(string path, bool showDialog)
    {
        if (_callback is null) return;
        cef_string_utf16_t native = default;
        CefString.Set(&native, path);
        _callback->cont(_callback, &native, showDialog ? 1 : 0);
        CefString.Clear(&native);
        Cancel();
    }

    /// <summary>UI thread: cancels the download, unless it was continued.</summary>
    public void Cancel()
    {
        if (_callback is null) return;
        NativeObject.Release(_callback);
        _callback = null;
    }
}

/// <summary>Cancels, pauses or resumes a download; CEF's latest callback for it.</summary>
public sealed unsafe class DownloadControl
{
    private cef_download_item_callback_t* _callback;

    internal DownloadControl(cef_download_item_callback_t* callback) => _callback = callback;

    public void Cancel() => Run(c => c->cancel(c));

    public void Pause() => Run(c => c->pause(c));

    public void Resume() => Run(c => c->resume(c));

    /// <summary>UI thread: drops CEF's callback once a newer one replaces it or the download is done.</summary>
    public void Release()
    {
        if (_callback is null) return;
        NativeObject.Release(_callback);
        _callback = null;
    }

    private delegate void Call(cef_download_item_callback_t* callback);

    private void Run(Call call)
    {
        if (_callback is not null) call(_callback);
    }
}

/// <summary><c>cef_download_handler_t</c>: reports downloads instead of CEF's default handling.</summary>
public sealed unsafe class DownloadHandler
{
    public DownloadHandler()
    {
        Native = NativeObject.Create<cef_download_handler_t>(this);
        Native->can_download = &CanDownload;
        Native->on_before_download = &OnBeforeDownload;
        Native->on_download_updated = &OnDownloadUpdated;
    }

    public cef_download_handler_t* Native { get; }

    /// <summary>UI thread: a download is about to start; the handler owns the <see cref="BeforeDownload"/>.</summary>
    public Action<DownloadSnapshot, BeforeDownload>? Before { get; init; }

    /// <summary>UI thread: a download changed; the handler owns the <see cref="DownloadControl"/>.</summary>
    public Action<DownloadSnapshot, DownloadControl>? Updated { get; init; }

    // CEF cancels a page's own downloads without this, even when the function is absent.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanDownload(cef_download_handler_t* self, cef_browser_t* browser, cef_string_utf16_t* url, cef_string_utf16_t* method)
    {
        NativeObject.Release(browser);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnBeforeDownload(cef_download_handler_t* self, cef_browser_t* browser, cef_download_item_t* item,
        cef_string_utf16_t* suggestedName, cef_before_download_callback_t* callback)
    {
        var owner = NativeObject.Owner<DownloadHandler>(self);
        NativeObject.Release(browser);
        // The item has no suggested name yet; CEF passes it separately.
        var snapshot = DownloadSnapshot.Read(item) with { SuggestedFileName = CefString.Read(suggestedName) };
        NativeObject.Release(item);
        var before = new BeforeDownload(callback);
        if (owner.Before is not { } handle)
        {
            before.Cancel();
            return 0;
        }
        Guard.Run(() => handle(snapshot, before));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDownloadUpdated(cef_download_handler_t* self, cef_browser_t* browser, cef_download_item_t* item,
        cef_download_item_callback_t* callback)
    {
        var owner = NativeObject.Owner<DownloadHandler>(self);
        NativeObject.Release(browser);
        var snapshot = DownloadSnapshot.Read(item);
        NativeObject.Release(item);
        var control = new DownloadControl(callback);
        if (owner.Updated is not { } handle)
        {
            control.Release();
            return;
        }
        Guard.Run(() => handle(snapshot, control));
    }
}
