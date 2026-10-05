using System.Drawing;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Options for <see cref="WebContents.FindInPage"/>; Electron's <c>findInPage</c> options.</summary>
public sealed record FindInPageOptions
{
    /// <summary>Searches forward; false searches backward.</summary>
    public bool Forward { get; init; } = true;

    /// <summary>Begins a new search; false moves to the next match of the current one. CEF starts a new search only
    /// when the text or case changes (drift C18).</summary>
    public bool FindNext { get; init; }

    /// <summary>Matches case.</summary>
    public bool MatchCase { get; init; }
}

/// <summary>What <see cref="WebContents.StopFindInPage"/> does with the active match; Electron's <c>action</c>.</summary>
public enum StopFindAction
{
    /// <summary>Clears the selection.</summary>
    ClearSelection,

    /// <summary>Keeps the active match selected.</summary>
    KeepSelection,

    /// <summary>Focuses and clicks the active match's node; CEF can only keep it selected.</summary>
    ActivateSelection,
}

/// <summary>A find's progress; Electron's <c>found-in-page</c> result.</summary>
/// <param name="RequestId">The <see cref="WebContents.FindInPage"/> request it answers.</param>
/// <param name="ActiveMatchOrdinal">The active match, from 1.</param>
/// <param name="Matches">The matches found so far.</param>
/// <param name="SelectionArea">Where the active match is, in the page's view.</param>
/// <param name="FinalUpdate">Whether the search has finished.</param>
public sealed record FoundInPageEventArgs(int RequestId, int ActiveMatchOrdinal, int Matches, Rectangle SelectionArea, bool FinalUpdate);

public sealed unsafe partial class WebContents
{
    private int _findRequestId;

    /// <summary>A <see cref="FindInPage"/> request has a result. Electron's <c>found-in-page</c>.</summary>
    public event EventHandler<FoundInPageEventArgs>? FoundInPage;

    /// <summary>The page entered HTML full screen (<c>requestFullscreen</c>). Electron's <c>enter-html-full-screen</c>.</summary>
    public event EventHandler? EnterHtmlFullScreen;

    /// <summary>The page left HTML full screen. Electron's <c>leave-html-full-screen</c>.</summary>
    public event EventHandler? LeaveHtmlFullScreen;

    /// <summary>A frame finished loading. Electron's <c>did-frame-finish-load</c>.</summary>
    public event EventHandler<bool>? DidFrameFinishLoad;

    /// <summary>A frame's navigation failed or was cancelled before it committed. Electron's
    /// <c>did-fail-provisional-load</c>; <see cref="DidFailLoad"/> follows unless it was cancelled.</summary>
    public event EventHandler<LoadFailedEventArgs>? DidFailProvisionalLoad;

    /// <summary>Starts or continues a search for <paramref name="text"/>; results arrive as <see cref="FoundInPage"/>.
    /// Returns the request's id. Electron's <c>webContents.findInPage()</c>.</summary>
    /// <exception cref="ArgumentException">The text is empty.</exception>
    public int FindInPage(string text, FindInPageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) throw new ArgumentException("Must provide a non-empty search content", nameof(text));
        options ??= new FindInPageOptions();
        var id = Interlocked.Increment(ref _findRequestId);
        WithHost(host =>
        {
            cef_string_utf16_t native = default;
            CefString.Set(&native, text);
            // CEF's findNext=0 only counts matches; findNext=1 selects one. CEF starts a new search when the text or case changes.
            host->find(host, &native, options.Forward ? 1 : 0, options.MatchCase ? 1 : 0, 1);
            CefString.Clear(&native);
        });
        return id;
    }

    /// <summary>Ends the search <see cref="FindInPage"/> started. Electron's <c>webContents.stopFindInPage()</c>.</summary>
    public void StopFindInPage(StopFindAction action)
        => WithHost(host => host->stop_finding(host, action == StopFindAction.ClearSelection ? 1 : 0));

    internal FindHandler Finds => _finds ??= new FindHandler
    {
        Result = r => Raise(FoundInPage, new FoundInPageEventArgs(Volatile.Read(ref _findRequestId), r.ActiveMatchOrdinal, r.Matches,
            new Rectangle(r.Selection.x, r.Selection.y, r.Selection.width, r.Selection.height), r.FinalUpdate), "found-in-page"),
    };

    private FindHandler? _finds;

    // UI thread.
    private void OnFullscreenModeChanged(bool fullscreen)
    {
        if (fullscreen) EnterHtmlFullScreen?.Invoke(this, EventArgs.Empty);
        else LeaveHtmlFullScreen?.Invoke(this, EventArgs.Empty);
    }
}
