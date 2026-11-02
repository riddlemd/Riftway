namespace Riftway;

/// <summary>How a page asked for a new window; Electron's <c>disposition</c>.</summary>
public enum WindowOpenDisposition
{
    /// <summary>No particular request. Electron's <c>default</c>.</summary>
    Default,

    /// <summary>A new tab in front, e.g. a <c>target=_blank</c> link. Electron's <c>foreground-tab</c>.</summary>
    ForegroundTab,

    /// <summary>A new tab behind, e.g. a middle-click. Electron's <c>background-tab</c>.</summary>
    BackgroundTab,

    /// <summary>A new window: <c>window.open</c>, or a shift-click. Electron's <c>new-window</c>.</summary>
    NewWindow,

    /// <summary>Anything else. Electron's <c>other</c>.</summary>
    Other,
}

/// <summary>A page's request for a new window; Electron's <c>setWindowOpenHandler</c> details.</summary>
/// <param name="Url">The URL the window would load.</param>
/// <param name="FrameName">The name passed to <c>window.open</c>, or empty.</param>
/// <param name="Features">The position and size the page asked for, as <c>left=…,top=…,width=…,height=…</c>; empty if none.</param>
/// <param name="Disposition">How the page asked for it.</param>
public sealed record WindowOpenDetails(string Url, string FrameName, string Features, WindowOpenDisposition Disposition)
{
    /// <summary>Whether a user action (a click, a key) started the request.</summary>
    public bool UserGesture { get; init; }
}

/// <summary>What to do with a page's new-window request; Electron's <c>WindowOpenHandlerResponse</c>.</summary>
public sealed record WindowOpenHandlerResponse
{
    private WindowOpenHandlerResponse(bool allowed) => IsAllowed = allowed;

    /// <summary>Whether the window opens. Electron's <c>action</c>.</summary>
    public bool IsAllowed { get; }

    /// <summary>Options for the new window instead of the defaults; its <see cref="BrowserWindowOptions.WebPreferences"/>
    /// replace the opener's. Electron's <c>overrideBrowserWindowOptions</c>.</summary>
    public BrowserWindowOptions? OverrideBrowserWindowOptions { get; init; }

    /// <summary>Changes the options parsed from the page's features (size, position, the opener's web preferences);
    /// Electron's <c>overrideBrowserWindowOptions</c>, which Electron spreads over those options.</summary>
    /// <remarks>A record cannot tell a property left at its default from one set to it, so this receives the parsed
    /// options to change with <c>with</c>; <see cref="OverrideBrowserWindowOptions"/> replaces them whole.</remarks>
    public Func<BrowserWindowOptions, BrowserWindowOptions>? ConfigureBrowserWindowOptions { get; init; }

    /// <summary>Whether the new window stays open when its opener closes. Electron's <c>outlivesOpener</c>.</summary>
    public bool OutlivesOpener { get; init; }

    /// <summary>Creates the window or view the page's new window shows in, instead of Riftway's <see cref="BrowserWindow"/>.
    /// It receives the options Riftway would have used, with <see cref="BrowserWindowOptions.WebContents"/> set to the
    /// popup's page; pass that page to a <see cref="BrowserWindow"/> (the options as given) or a
    /// <see cref="WebContentsView"/>, and return it. <see cref="WebContents.DidCreateWindow"/> is not raised. Electron's
    /// <c>createWindow</c>.</summary>
    /// <remarks>Runs on the UI thread before the popup exists, so the page must be adopted before it returns. A page left
    /// unadopted blocks the popup (drift O3). A popup from a window's own page can only become a window's page (drift
    /// V4).</remarks>
    public Func<BrowserWindowOptions, WebContents?>? CreateWindow { get; init; }

    /// <summary>Opens the window. Electron's <c>{ action: 'allow' }</c>.</summary>
    public static WindowOpenHandlerResponse Allow(BrowserWindowOptions? overrideBrowserWindowOptions = null, bool outlivesOpener = false)
        => new(true) { OverrideBrowserWindowOptions = overrideBrowserWindowOptions, OutlivesOpener = outlivesOpener };

    /// <summary>Opens the window with the page's features' options changed by <paramref name="configure"/>, as Electron
    /// spreads <c>overrideBrowserWindowOptions</c> over them.</summary>
    public static WindowOpenHandlerResponse Allow(Func<BrowserWindowOptions, BrowserWindowOptions> configure, bool outlivesOpener = false)
        => new(true) { ConfigureBrowserWindowOptions = configure, OutlivesOpener = outlivesOpener };

    /// <summary>Opens the window in what <paramref name="createWindow"/> makes; see <see cref="CreateWindow"/>.
    /// Electron's <c>{ action: 'allow', createWindow }</c>.</summary>
    public static WindowOpenHandlerResponse Allow(Func<BrowserWindowOptions, WebContents?> createWindow,
        BrowserWindowOptions? overrideBrowserWindowOptions = null, bool outlivesOpener = false)
        => new(true) { CreateWindow = createWindow, OverrideBrowserWindowOptions = overrideBrowserWindowOptions, OutlivesOpener = outlivesOpener };

    /// <summary>Blocks the window: <c>window.open</c> returns null in the page. Electron's <c>{ action: 'deny' }</c>.</summary>
    public static WindowOpenHandlerResponse Deny { get; } = new(false);
}

/// <summary>A page opened a window; Electron's <c>did-create-window</c> arguments.</summary>
/// <param name="Window">The new window.</param>
/// <param name="Details">The request that opened it.</param>
public sealed record DidCreateWindowEventArgs(BrowserWindow Window, WindowOpenDetails Details);

/// <summary>What becomes of a popup once a <see cref="WindowOpenHandlerResponse.CreateWindow"/> callback returned.</summary>
internal enum CreateWindowOutcome
{
    /// <summary>No window or view took the page: the popup is not created.</summary>
    Block,

    /// <summary>Shown where it was adopted, but left out of the opener's lifetime, as Electron after its error.</summary>
    Unwired,

    /// <summary>Shown where it was adopted, and closed with its opener.</summary>
    Wired,
}

/// <summary>A popup's page while a <see cref="WindowOpenHandlerResponse.CreateWindow"/> callback runs: a window or view
/// may adopt it, once.</summary>
internal sealed class PopupGuest(WebContents opener)
{
    /// <summary>Electron's error when <c>createWindow</c> returns anything but the page it was given.</summary>
    internal const string InvalidWebContents = "Invalid webContents. Created window should be connected to webContents passed with options object.";

    internal const string NotAdopted = "The page a createWindow callback receives must be given to a BrowserWindow or WebContentsView before it returns; the window is not opened.";

    public WebContents Opener { get; } = opener;

    /// <summary>The <see cref="BrowserWindow"/> or <see cref="WebContentsView"/> that took the page, or null.</summary>
    public object? Host { get; private set; }

    /// <summary>Takes <paramref name="page"/> for <paramref name="host"/>.</summary>
    /// <exception cref="NotSupportedException"><paramref name="page"/> is not a popup page whose callback is running, or
    /// another window or view took it.</exception>
    public static PopupGuest Adopt(WebContents page, object host)
    {
        var guest = Adoptable(page);
        guest.Host = host;
        return guest;
    }

    /// <summary>The guest a window or view would join by adopting <paramref name="page"/>.</summary>
    /// <exception cref="NotSupportedException">As <see cref="Adopt"/>.</exception>
    public static PopupGuest Adoptable(WebContents page)
        => page.Guest is { Host: null } guest ? guest
            : throw new NotSupportedException("Only the page a window-open handler's createWindow receives can be adopted, while it runs, by one window or view.");

    /// <summary>Electron's handling of what <c>createWindow</c> did (guest-window-manager.ts): an adopted page shows
    /// either way, and joins its opener's lifetime only when it was returned.</summary>
    public static CreateWindowOutcome Decide(bool adopted, bool returnedThePage, bool outlivesOpener)
        => !adopted ? CreateWindowOutcome.Block
            : returnedThePage && !outlivesOpener ? CreateWindowOutcome.Wired
            : CreateWindowOutcome.Unwired;

    /// <summary>The console error for what <c>createWindow</c> did, or null when it did what Electron expects.</summary>
    public static string? ErrorFor(bool adopted, bool returnedThePage)
        => !returnedThePage ? InvalidWebContents : adopted ? null : NotAdopted;
}
