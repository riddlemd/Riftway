using System.ComponentModel;
using System.Text.Json;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A navigation; Electron's navigation event <c>details</c>. Set <see cref="CancelEventArgs.Cancel"/>
/// (Electron's <c>event.preventDefault()</c>) to stop it from <see cref="WebContents.WillNavigate"/>,
/// <see cref="WebContents.WillFrameNavigate"/> or <see cref="WebContents.WillRedirect"/>; other events ignore it.</summary>
public sealed class NavigationEventArgs(string url, bool isSameDocument, bool isMainFrame, WebFrameMain? frame) : CancelEventArgs
{
    /// <summary>Where the frame is going.</summary>
    public string Url { get; } = url;

    /// <summary>Whether the frame stays on its document (a fragment or history API change).</summary>
    public bool IsSameDocument { get; } = isSameDocument;

    /// <summary>Whether the main frame navigates.</summary>
    public bool IsMainFrame { get; } = isMainFrame;

    /// <summary>The frame that navigates, when Oriel can tell which.</summary>
    public WebFrameMain? Frame { get; } = frame;

    /// <summary>The frame that started the navigation: CEF does not say, so always null.</summary>
    public WebFrameMain? Initiator => null;
}

/// <summary>A frame committed a new document; Electron's <c>did-navigate</c> and <c>did-frame-navigate</c> arguments.</summary>
/// <param name="Url">The document's URL.</param>
/// <param name="HttpResponseCode">The response's status, or -1 without an HTTP response.</param>
/// <param name="HttpStatusText">The response's status text, or empty.</param>
/// <param name="IsMainFrame">Whether it is the main frame.</param>
/// <param name="Frame">The frame.</param>
public sealed record DidNavigateEventArgs(string Url, int HttpResponseCode, string HttpStatusText, bool IsMainFrame, WebFrameMain? Frame);

/// <summary>A frame moved within its document; Electron's <c>did-navigate-in-page</c> arguments.</summary>
/// <param name="Url">The new URL.</param>
/// <param name="IsMainFrame">Whether it is the main frame.</param>
/// <param name="Frame">The frame.</param>
public sealed record DidNavigateInPageEventArgs(string Url, bool IsMainFrame, WebFrameMain? Frame);

public sealed partial class WebContents
{
    /// <summary>The page itself wants its main frame to load a new document (a link, a form, <c>location</c>); not
    /// raised for <see cref="LoadUrl"/>, reloads or history. Electron's <c>will-navigate</c>.</summary>
    public event EventHandler<NavigationEventArgs>? WillNavigate;

    /// <summary>The page wants any of its frames to load a new document; raised before <see cref="WillNavigate"/>.
    /// Electron's <c>will-frame-navigate</c>.</summary>
    public event EventHandler<NavigationEventArgs>? WillFrameNavigate;

    /// <summary>A server redirects a frame's navigation. Electron's <c>will-redirect</c>.</summary>
    public event EventHandler<NavigationEventArgs>? WillRedirect;

    /// <summary>A frame starts navigating, within its document or to a new one. Electron's <c>did-start-navigation</c>.</summary>
    public event EventHandler<NavigationEventArgs>? DidStartNavigation;

    /// <summary>A frame's navigation followed a redirect. Electron's <c>did-redirect-navigation</c>.</summary>
    public event EventHandler<NavigationEventArgs>? DidRedirectNavigation;

    /// <summary>The main frame committed a new document. Electron's <c>did-navigate</c>.</summary>
    public event EventHandler<DidNavigateEventArgs>? DidNavigate;

    /// <summary>Any frame committed a new document. Electron's <c>did-frame-navigate</c>.</summary>
    public event EventHandler<DidNavigateEventArgs>? DidFrameNavigate;

    /// <summary>A frame moved within its document. Electron's <c>did-navigate-in-page</c>.</summary>
    public event EventHandler<DidNavigateInPageEventArgs>? DidNavigateInPage;

    // A navigation's response by URL, until its document commits; written on the IO thread.
    private readonly Dictionary<string, (int Status, string Text)> _navigationResponses = [];

    // Oriel's own loads and history moves: Electron's browser-initiated navigations.
    private const uint BrowserInitiated = (uint)(cef_transition_type_t.TT_FROM_API_FLAG | cef_transition_type_t.TT_DIRECT_LOAD_FLAG
        | cef_transition_type_t.TT_FORWARD_BACK_FLAG);

    // CEF marks no reload as the API's, so Oriel's own Reload says so here; UI thread.
    private bool _reloadRequested;

    internal static bool IsRendererInitiated(uint transition)
        => (transition & BrowserInitiated) == 0 && (transition & (uint)cef_transition_type_t.TT_SOURCE_MASK) != (uint)cef_transition_type_t.TT_EXPLICIT;

    // UI thread: true cancels.
    private bool OnBeforeBrowse(BrowseRequest request)
    {
        if (!_navigated && request.Url == "about:blank") return false;
        var frame = FrameOfPath(request.FramePath);
        var isMain = request.FramePath.Count == 1;
        NavigationEventArgs Args() => new(request.Url, false, isMain, frame);
        if (isMain) _loadStartedUrl = request.Url;
        if (request.IsRedirect)
        {
            var redirect = Args();
            Raise(WillRedirect, redirect, "will-redirect");
            if (redirect.Cancel) return true;
            Raise(DidRedirectNavigation, Args(), "did-redirect-navigation");
            return false;
        }
        Raise(DidStartNavigation, Args(), "did-start-navigation");
        var ownReload = isMain && _reloadRequested && (request.Transition & (uint)cef_transition_type_t.TT_SOURCE_MASK) == (uint)cef_transition_type_t.TT_RELOAD;
        if (isMain) _reloadRequested = false;
        if (ownReload || !IsRendererInitiated(request.Transition)) return false;
        var anyFrame = Args();
        Raise(WillFrameNavigate, anyFrame, "will-frame-navigate");
        if (anyFrame.Cancel) return true;
        if (!isMain) return false;
        var main = Args();
        Raise(WillNavigate, main, "will-navigate");
        return main.Cancel;
    }

    // The DevTools frame for CEF's frame: the main frame, then down by each frame's name and current URL.
    internal WebFrameMain? FrameOfPath(IReadOnlyList<(string Name, string Url)> path)
    {
        if (_mainFrameId is null || !_frames.TryGetValue(_mainFrameId, out var current)) return null;
        for (var i = path.Count - 2; i >= 0; i--)
        {
            var (name, url) = path[i];
            var parentId = current.Id;
            current = _frames.Values.FirstOrDefault(f => f.ParentId == parentId && !f.Detached && f.Name == name && f.Url == url)!;
            if (current is null) return null;
        }
        return WrapperOf(current);
    }

    // IO thread.
    private void RecordNavigationResponse(RequestInfo request, ResponseInfo response)
    {
        if (request.ResourceType is not (cef_resource_type_t.RT_MAIN_FRAME or cef_resource_type_t.RT_SUB_FRAME)) return;
        lock (_navigationResponses) _navigationResponses[request.Url] = (response.Status, response.StatusText);
    }

    // UI thread: DevTools' Page.frameNavigated, after the frame state is updated.
    private void OnFrameCommitted(JsonElement frame)
    {
        if (!_navigated || frame.TryGetProperty("unreachableUrl", out _)) return;
        var url = frame.GetProperty("url").GetString()! + (frame.TryGetProperty("urlFragment", out var fragment) ? fragment.GetString() : "");
        var id = frame.GetProperty("id").GetString()!;
        var isMain = !frame.TryGetProperty("parentId", out _);
        (int Status, string Text) response;
        lock (_navigationResponses)
            if (!_navigationResponses.Remove(frame.GetProperty("url").GetString()!, out response)) response = (-1, "");
        var wrapper = _frames.TryGetValue(id, out var state) ? WrapperOf(state) : null;
        var args = new DidNavigateEventArgs(url, response.Status, response.Text, isMain, wrapper);
        Raise(DidFrameNavigate, args, "did-frame-navigate");
        if (isMain) Raise(DidNavigate, args, "did-navigate");
    }

    // UI thread: DevTools' Page.navigatedWithinDocument.
    private void OnNavigatedWithinDocument(JsonElement parameters)
    {
        if (!_navigated) return;
        var id = parameters.GetProperty("frameId").GetString()!;
        var url = parameters.GetProperty("url").GetString()!;
        var isMain = id == _mainFrameId;
        var wrapper = _frames.TryGetValue(id, out var state) ? WrapperOf(state) : null;
        Raise(DidStartNavigation, new NavigationEventArgs(url, true, isMain, wrapper), "did-start-navigation");
        Raise(DidNavigateInPage, new DidNavigateInPageEventArgs(url, isMain, wrapper), "did-navigate-in-page");
    }
}
