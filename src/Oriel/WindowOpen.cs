namespace Oriel;

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

    /// <summary>Opens the window. Electron's <c>{ action: 'allow' }</c>.</summary>
    public static WindowOpenHandlerResponse Allow(BrowserWindowOptions? overrideBrowserWindowOptions = null, bool outlivesOpener = false)
        => new(true) { OverrideBrowserWindowOptions = overrideBrowserWindowOptions, OutlivesOpener = outlivesOpener };

    /// <summary>Opens the window with the page's features' options changed by <paramref name="configure"/>, as Electron
    /// spreads <c>overrideBrowserWindowOptions</c> over them.</summary>
    public static WindowOpenHandlerResponse Allow(Func<BrowserWindowOptions, BrowserWindowOptions> configure, bool outlivesOpener = false)
        => new(true) { ConfigureBrowserWindowOptions = configure, OutlivesOpener = outlivesOpener };

    /// <summary>Blocks the window: <c>window.open</c> returns null in the page. Electron's <c>{ action: 'deny' }</c>.</summary>
    public static WindowOpenHandlerResponse Deny { get; } = new(false);
}

/// <summary>A page opened a window; Electron's <c>did-create-window</c> arguments.</summary>
/// <param name="Window">The new window.</param>
/// <param name="Details">The request that opened it.</param>
public sealed record DidCreateWindowEventArgs(BrowserWindow Window, WindowOpenDetails Details);
