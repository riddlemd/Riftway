using System.Text.Json.Nodes;

namespace Oriel;

/// <summary>What Oriel knows of one frame, kept up to date from DevTools events; UI thread only.</summary>
internal sealed class FrameState
{
    public required string Id { get; init; }
    public string? ParentId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public int? DefaultContext { get; set; }
    public int? IsolatedContext { get; set; }
    public bool Detached { get; set; }
    public WebFrameMain? Wrapper { get; set; }
    // Kept once the frame goes: Electron's frameToken and processId outlive the frame.
    public string? CefId { get; set; }
    // Creation order, which tells apart sibling frames with the same name and URL.
    public long Created { get; } = Interlocked.Increment(ref s_created);
    private static long s_created;
}

/// <summary>A frame in a page: the main frame or an iframe; Electron's <c>WebFrameMain</c>.</summary>
/// <remarks>Members may be called from any thread. The same frame is always the same object. A frame in another
/// process (a cross-site iframe) is listed, but cannot run script or receive messages from here yet.</remarks>
public sealed class WebFrameMain
{
    private readonly WebContents _page;
    private readonly FrameState _state;

    internal WebContents Page => _page;

    internal WebFrameMain(WebContents page, FrameState state)
    {
        _page = page;
        _state = state;
    }

    /// <summary>The frame's document has been parsed; raised on the UI thread for each document the frame loads.
    /// Electron's <c>dom-ready</c>.</summary>
    public event EventHandler? DomReady;

    internal void RaiseDomReady() => DomReady?.Invoke(this, EventArgs.Empty);

    /// <summary>Handlers and listeners for this frame's messages, asked before its page's and <see cref="IpcMain"/>'s.
    /// Electron's <c>frame.ipc</c>.</summary>
    public IpcMainScope Ipc { get; } = new();

    /// <summary>Chromium's token for the frame's current document host: 32 upper-case hex digits, new when a navigation
    /// moves the frame to another host. Empty while the frame is not matched to CEF's (drift F6). Electron's <c>frameToken</c>.</summary>
    public string FrameToken => _page.IdsOf(_state)?.FrameToken ?? string.Empty;

    /// <summary>Chromium's id for the frame's render process (not the OS process id); -1 while the frame is not matched
    /// to CEF's (drift F6). Electron's <c>processId</c>.</summary>
    public int ProcessId => _page.IdsOf(_state)?.ProcessId ?? -1;

    /// <summary>The operating system's id for the frame's render process; 0 while that process is not running.
    /// Electron's <c>osProcessId</c>.</summary>
    /// <exception cref="InvalidOperationException">The frame has gone.</exception>
    public int OsProcessId
    {
        get
        {
            ThrowIfGone();
            return ProcessId is var id and >= 0 ? OrielApp.RendererPid(id) : 0;
        }
    }

    /// <summary>The page's visibility as the frame's document sees it: <c>visible</c> or <c>hidden</c>. Electron's
    /// <c>visibilityState</c>.</summary>
    /// <exception cref="InvalidOperationException">The frame has gone.</exception>
    public string VisibilityState
    {
        get
        {
            ThrowIfGone();
            return _page.VisibilityState;
        }
    }

    // Electron's CheckRenderFrame message.
    private void ThrowIfGone()
    {
        if (Detached) throw new InvalidOperationException("Render frame was disposed before WebFrameMain could be accessed");
    }

    /// <summary>The frame with <paramref name="frameToken"/> in the render process <paramref name="processId"/>, in any page;
    /// null when there is none. Electron's <c>webFrameMain.fromFrameToken()</c>.</summary>
    public static WebFrameMain? FromFrameToken(int processId, string frameToken)
    {
        ArgumentNullException.ThrowIfNull(frameToken);
        return WebContents.FromFrameToken(processId, frameToken);
    }

    /// <summary>The frame's current URL. Electron's <c>url</c>.</summary>
    public string Url => _page.ReadFrame(() => _state.Url);

    /// <summary>The frame's origin, e.g. <c>https://example.com</c>. Electron's <c>origin</c>.</summary>
    public string Origin => _page.ReadFrame(() => _state.Origin);

    /// <summary>The frame's name attribute, or empty. Electron's <c>name</c>.</summary>
    public string Name => _page.ReadFrame(() => _state.Name);

    /// <summary>The frame containing this one, or null for the main frame. Electron's <c>parent</c>.</summary>
    public WebFrameMain? Parent => _page.ParentOf(_state);

    /// <summary>The page's main frame. Electron's <c>top</c>.</summary>
    public WebFrameMain Top
    {
        get
        {
            var frame = this;
            while (frame.Parent is { } parent) frame = parent;
            return frame;
        }
    }

    /// <summary>The frames directly inside this one. Electron's <c>frames</c>.</summary>
    public IReadOnlyList<WebFrameMain> Frames => _page.ChildrenOf(_state);

    /// <summary>This frame and every frame inside it, depth first. Electron's <c>framesInSubtree</c>.</summary>
    public IReadOnlyList<WebFrameMain> FramesInSubtree
    {
        get
        {
            var all = new List<WebFrameMain> { this };
            foreach (var child in Frames) all.AddRange(child.FramesInSubtree);
            return all;
        }
    }

    /// <summary>Whether the frame has been removed from its page. Electron's <c>detached</c>.</summary>
    public bool Detached => _page.ReadFrame(() => _state.Detached);

    /// <summary>Whether the frame is gone, with its page or on its own. Electron's <c>isDestroyed()</c>.</summary>
    public bool IsDestroyed() => Detached;

    /// <summary>Runs <paramref name="code"/> in the frame's main world and returns its value, awaiting a promise.
    /// Electron's <c>frame.executeJavaScript()</c>.</summary>
    /// <exception cref="JavaScriptException">The code threw or its promise rejected.</exception>
    /// <exception cref="InvalidOperationException">The frame is detached, not loaded yet, or in another process.</exception>
    public Task<JsonNode?> ExecuteJavaScriptAsync(string code, bool userGesture = false) => _page.EvaluateAsync(code, userGesture, _state);

    /// <summary>Prints this frame to PDF and returns the file's bytes. Electron's <c>frame.printToPDF()</c>; the main
    /// frame only (drift C51).</summary>
    /// <exception cref="InvalidOperationException">The frame has gone, or as <see cref="WebContents.PrintToPdfAsync"/>.</exception>
    /// <exception cref="NotSupportedException">The frame is not its page's main frame.</exception>
    public Task<byte[]> PrintToPdfAsync(PrintToPdfOptions? options = null) => _page.PrintToPdf(_state, options);

    /// <summary>Reloads the frame. Electron's <c>frame.reload()</c>.</summary>
    public void Reload() => _ = _page.EvaluateAsync("location.reload()", false, _state).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    /// <summary>Sends a message to the frame's <c>oriel.ipcRenderer.on(channel)</c> listeners; dropped while the frame has
    /// no preload (not loaded yet, or in another process). Electron's <c>frame.send()</c>.</summary>
    public void Send(string channel, params JsonNode?[] args)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _page.SendToFrame(_state, new JsonObject { ["t"] = "msg", ["ch"] = channel, ["args"] = new JsonArray(args) }.ToJsonString());
    }

    /// <summary>Sends <paramref name="message"/> to this frame with ports moved into it. Electron's
    /// <c>frame.postMessage()</c>.</summary>
    /// <exception cref="InvalidOperationException">A port was already moved.</exception>
    public void PostMessage(string channel, JsonNode? message, IReadOnlyList<MessagePortMain>? transfer = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var ports = PortLink.CheckTransfer(transfer, null);
        Oriel.Cef.CefThread.RunOnUi(() => _page.PostToFrame(_state, channel, message, ports));
    }
}
