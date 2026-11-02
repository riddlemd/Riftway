using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>The page's render process and its kind.</summary>
public sealed unsafe partial class WebContents
{
    /// <summary>Chromium's id for the main frame's render process (not the OS process id), as frame events and
    /// <see cref="WebFrameMain.ProcessId"/> carry it; -1 once the page has closed. Electron's
    /// <c>webContents.getProcessId()</c>.</summary>
    public int GetProcessId() => CefThread.InvokeOnUi(MainFrameProcessId);

    /// <summary>The operating system's id for the main frame's render process; 0 while that process is not running.
    /// Electron's <c>webContents.getOSProcessId()</c>.</summary>
    public int GetOSProcessId() => GetProcessId() is var id and >= 0 ? RiftwayApp.RendererPid(id) : 0;

    /// <summary>The page's kind: always <c>window</c>, which Electron gives both a window's page and a
    /// <see cref="WebContentsView"/>'s. Electron's <c>webContents.getType()</c>; renamed, as <c>GetType</c> is .NET's
    /// own (drift C45).</summary>
    public string GetWebContentsType() => "window";

    /// <summary>Whether the page renders offscreen: always false, as Riftway has no offscreen rendering. Electron's
    /// <c>webContents.isOffscreen()</c>.</summary>
    public bool IsOffscreen() => false;

    // UI thread: CEF's main frame is Chromium's primary main frame, so its identifier needs no matching to DevTools'.
    private int MainFrameProcessId()
    {
        if (_browser is null) return -1;
        var frame = _browser->get_main_frame(_browser);
        if (frame is null) return -1;
        var identifier = CefString.ReadUserFree(frame->get_identifier(frame));
        NativeObject.Release(frame);
        return FrameIdentity.Parse(identifier)?.ProcessId ?? -1;
    }
}
