using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A page in a <see cref="NavigationHistory"/>; Electron's <c>NavigationEntry</c>.</summary>
/// <param name="Url">The page's URL.</param>
/// <param name="Title">The page's title.</param>
public sealed record NavigationEntry(string Url, string Title);

/// <summary>A page's back/forward list; Electron's <c>webContents.navigationHistory</c>.</summary>
/// <remarks>Members may be called from any thread. The list starts with the first page the window loads.</remarks>
public sealed unsafe class NavigationHistory
{
    private readonly WebContents _page;

    internal NavigationHistory(WebContents page) => _page = page;

    /// <summary>Whether there is a page to go back to. Electron's <c>canGoBack()</c>.</summary>
    // From the entry list, not the browser's can_go_back: CEF caches that, and Clear does not update it.
    public bool CanGoBack() => CanGoToOffset(-1);

    /// <summary>Whether there is a page to go forward to. Electron's <c>canGoForward()</c>.</summary>
    public bool CanGoForward() => CanGoToOffset(1);

    /// <summary>Whether <see cref="GoToOffset"/> with <paramref name="offset"/> lands on a page. Electron's <c>canGoToOffset()</c>.</summary>
    public bool CanGoToOffset(int offset) => Snapshot() is var (entries, active) && active + offset >= 0 && active + offset < entries.Count;

    /// <summary>Goes back one page. Electron's <c>goBack()</c>.</summary>
    public void GoBack() => _page.WithBrowser(b => b->go_back(b));

    /// <summary>Goes forward one page. Electron's <c>goForward()</c>.</summary>
    public void GoForward() => _page.WithBrowser(b => b->go_forward(b));

    /// <summary>Goes to the page at <paramref name="index"/>. Electron's <c>goToIndex()</c>.</summary>
    public void GoToIndex(int index) => _page.GoToHistoryEntry((_, _) => index);

    /// <summary>Goes <paramref name="offset"/> pages back (negative) or forward. Electron's <c>goToOffset()</c>.</summary>
    public void GoToOffset(int offset) => _page.GoToHistoryEntry((current, _) => current + offset);

    /// <summary>Forgets every page but the one showing. Electron's <c>clear()</c>.</summary>
    public void Clear() => _page.ResetHistory();

    /// <summary>The index of the page showing, or -1 before the first page. Electron's <c>getActiveIndex()</c>.</summary>
    public int GetActiveIndex() => Snapshot().Active;

    /// <summary>How many pages the list holds. Electron's <c>length()</c>.</summary>
    public int Length() => Snapshot().Entries.Count;

    /// <summary>The page at <paramref name="index"/>, or null outside the list. Electron's <c>getEntryAtIndex()</c>.</summary>
    public NavigationEntry? GetEntryAtIndex(int index) => Snapshot().Entries is var entries && index >= 0 && index < entries.Count ? entries[index] : null;

    /// <summary>Every page in the list, oldest first. Electron's <c>getAllEntries()</c>.</summary>
    public IReadOnlyList<NavigationEntry> GetAllEntries() => Snapshot().Entries;

    private (IReadOnlyList<NavigationEntry> Entries, int Active) Snapshot() => _page.QueryHost(host =>
    {
        var visitor = new NavigationEntryVisitor();
        NativeObject.AddRef(visitor.Native);
        host->get_navigation_entries(host, visitor.Native, 0);
        IReadOnlyList<NavigationEntry> entries = [.. visitor.Entries.Select(e => new NavigationEntry(e.Url, e.Title))];
        var active = visitor.Entries.FindIndex(e => e.IsCurrent);
        NativeObject.Release(visitor.Native);
        return (entries, active);
    }, ([], -1));
}
