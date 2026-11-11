using System.ComponentModel;
using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>Options for a new <see cref="BrowserWindow"/>; Electron's <c>BrowserWindowConstructorOptions</c>.</summary>
public sealed partial record BrowserWindowOptions : BaseWindowOptions
{
    /// <summary>Settings for the page. Electron's <c>webPreferences</c>.</summary>
    public WebPreferences WebPreferences { get; init; } = new();

    /// <summary>The page the window shows instead of a new one, whose preferences then apply instead of
    /// <see cref="WebPreferences"/>: only the popup page a <see cref="WindowOpenHandlerResponse.CreateWindow"/> callback
    /// receives, adopted while it runs (drift V4). Electron's <c>webContents</c>.</summary>
    public WebContents? WebContents { get; init; }

    /// <summary>Whether the page is active while the window is created hidden: its <c>document.visibilityState</c> is
    /// <c>visible</c>, it paints and <see cref="BrowserWindow.ReadyToShow"/> fires. Electron's
    /// <c>paintWhenInitiallyHidden</c>, default true. Only true is supported: false throws (drift W63).</summary>
    public bool PaintWhenInitiallyHidden { get; init; } = true;

    internal void Validate()
    {
        if (!PaintWhenInitiallyHidden)
            throw new NotSupportedException("PaintWhenInitiallyHidden = false is not supported: CEF cannot start a window's page hidden (drift W63).");
        WebPreferences.Validate("options");
    }
}

/// <summary>Settings for a window's page; Electron's <c>WebPreferences</c>.</summary>
public sealed record WebPreferences
{
    /// <summary>A script run before the page's own, in an isolated world, with <c>riftway.ipcRenderer</c>
    /// and <c>riftway.contextBridge</c>. Electron's <c>preload</c>.</summary>
    public string? Preload { get; init; }

    /// <summary>Whether <see cref="WebContents.OpenDevTools"/> works. Electron's <c>devTools</c>.</summary>
    public bool DevTools { get; init; } = true;

    /// <summary>The page's starting zoom, 1.0 for 100%. Like Electron, zoom is remembered per origin: the factor
    /// applies to each origin the page visits until that origin's zoom is changed. Electron's <c>zoomFactor</c>.</summary>
    public double ZoomFactor { get; init; } = 1.0;

    /// <summary>Whether the page's scripts run. Without them the preload and IPC do not run either.
    /// Electron's <c>javascript</c>.</summary>
    public bool Javascript { get; init; } = true;

    /// <summary>Whether images load. Electron's <c>images</c>.</summary>
    public bool Images { get; init; } = true;

    /// <summary>Whether the user can resize <c>textarea</c> elements. Electron's <c>textAreasAreResizable</c>.</summary>
    public bool TextAreasAreResizable { get; init; } = true;

    /// <summary>Whether WebGL is available. Electron's <c>webgl</c>.</summary>
    public bool WebGL { get; init; } = true;

    /// <summary>Fonts for CSS's generic families. Electron's <c>defaultFontFamily</c>.</summary>
    public DefaultFontFamily? DefaultFontFamily { get; init; }

    /// <summary>The default font size in CSS pixels; null keeps Chromium's 16. Electron's <c>defaultFontSize</c>.</summary>
    public int? DefaultFontSize { get; init; }

    /// <summary>The default size of monospace text; null keeps Chromium's 13. Electron's <c>defaultMonospaceFontSize</c>.</summary>
    public int? DefaultMonospaceFontSize { get; init; }

    /// <summary>The smallest font size text renders at. Electron's <c>minimumFontSize</c>.</summary>
    /// <remarks>Not supported (setting it throws): CEF applies the profile's value to every window.</remarks>
    public int? MinimumFontSize { get; init; }

    /// <summary>The encoding for pages that declare none, e.g. <c>ISO-8859-1</c>. Electron's <c>defaultEncoding</c>.</summary>
    /// <remarks>Not supported (setting it throws): CEF applies the profile's value to every window.</remarks>
    public string? DefaultEncoding { get; init; }

    /// <summary>Raises <see cref="WebContents.PreferredSizeChanged"/> when the page's preferred size changes. Electron's
    /// <c>enablePreferredSizeMode</c>.</summary>
    public bool EnablePreferredSizeMode { get; init; }

    /// <summary>Suppresses the page's <c>alert</c>, <c>confirm</c> and <c>prompt</c>: they return at once
    /// (<c>confirm</c> false, <c>prompt</c> null). Electron's <c>disableDialogs</c>.</summary>
    public bool DisableDialogs { get; init; }

    /// <summary>From the page's second dialog on, the dialog offers a checkbox that stops the page's origin opening any
    /// more for the rest of the page's life. Electron's <c>safeDialogs</c>.</summary>
    public bool SafeDialogs { get; init; }

    /// <summary>Strings appended to <c>process.argv</c> where the page's preload scripts run, after the app's executable;
    /// handy for passing small bits of data to a preload. Electron's <c>additionalArguments</c> (drift W66).</summary>
    public IReadOnlyList<string>? AdditionalArguments { get; init; }

    /// <summary>How the page's images animate from its first load: <c>animate</c>, <c>animateOnce</c> or
    /// <c>noAnimation</c>; anything else is ignored, and null (the default) leaves the session's policy as it is.
    /// Electron's <c>imageAnimationPolicy</c> (default <c>animate</c>). As <see cref="WebContents.SetImageAnimationPolicy"/>,
    /// it applies to every page of the page's session (drift C53).</summary>
    public string? ImageAnimationPolicy { get; init; }

    /// <summary>The checkbox's label when <see cref="SafeDialogs"/> is on; null or empty uses Electron's "Prevent this app
    /// from creating additional dialogs". Electron's <c>safeDialogsMessage</c>.</summary>
    public string? SafeDialogsMessage { get; init; }

    /// <summary>Whether a navigation focuses the page, when the page is its window's own and that window is active.
    /// Electron's <c>focusOnNavigation</c>; on by default.</summary>
    /// <remarks>A window's own page cannot turn it off (setting false throws): Chrome-style browsers focus the page on
    /// every navigation in an active window themselves (drift W60). A <see cref="WebContentsView"/>'s page never takes
    /// focus on navigation, as in Electron, so false is accepted there.</remarks>
    public bool FocusOnNavigation { get; init; } = true;

    /// <summary>Whether HTML fullscreen (<c>element.requestFullscreen()</c>) leaves the window's size alone. Electron's
    /// <c>disableHtmlFullscreenWindowResize</c>, default false. Only false is supported: true throws (drift W64).</summary>
    public bool DisableHtmlFullscreenWindowResize { get; init; }

    /// <summary>The session the page uses; overrides <see cref="Partition"/>. Null uses <see cref="Partition"/>.
    /// Electron's <c>session</c>.</summary>
    public Session? Session { get; init; }

    /// <summary>The page's session by name, as <see cref="Riftway.Session.FromPartition(string)"/>: <c>persist:</c><i>name</i>
    /// is kept on disk, any other name in memory. Null or empty uses <see cref="Riftway.Session.DefaultSession"/>.
    /// Electron's <c>partition</c>.</summary>
    public string? Partition { get; init; }

    /// <summary>Whether the page's default background is transparent instead of white, so what lies behind the page
    /// shows where the page paints nothing. A window's <see cref="BaseWindowOptions.BackgroundColor"/>, and a transparent
    /// or material window, decide its page's background over this. Electron's <c>transparent</c>, which for a window's
    /// or a <see cref="WebContentsView"/>'s page acts only when true (its documented default of true is a
    /// <c>&lt;webview&gt;</c>'s).</summary>
    public bool Transparent { get; init; }

    /// <summary>Whether the page's renderer runs in Chromium's sandbox; null follows the session. Electron's
    /// <c>sandbox</c>, which Riftway decides per session (<see cref="SessionOptions.Sandbox"/>, drift W13): a value that
    /// contradicts the page's session throws.</summary>
    public bool? Sandbox { get; init; }

    /// <summary>Whether the page rubber-bands when scrolled past its ends (macOS); null follows the session. Electron's
    /// <c>scrollBounce</c>, which Riftway decides per session (<see cref="SessionOptions.ScrollBounce"/>, drift W55): a value
    /// that contradicts the page's session throws.</summary>
    public bool? ScrollBounce { get; init; }

    /// <summary>Renders the page offscreen: it paints into <see cref="WebContents.Paint"/> instead of its window, at its
    /// window's outer size (Electron's, not the content size), and takes input only from <see cref="WebContents.SendInputEvent(KeyboardInputEvent)"/>.
    /// Null renders into the window. Electron's <c>offscreen</c>.</summary>
    public OffscreenOptions? Offscreen { get; init; }

    internal void Validate(string paramName, bool windowPage = true)
    {
        if (AdditionalArguments?.Any(a => a is null) == true)
            throw new ArgumentException("AdditionalArguments cannot hold null.", paramName);
        if (windowPage && !FocusOnNavigation)
            throw new NotSupportedException("FocusOnNavigation = false is not supported for a window's page: Chrome-style browsers focus it on every navigation in an active window.");
        if (DisableHtmlFullscreenWindowResize)
            throw new NotSupportedException("DisableHtmlFullscreenWindowResize is not supported: Chromium's fullscreen controller makes the window fullscreen with the page (drift W64).");
        if (Offscreen is { UseSharedTexture: true } && !OperatingSystem.IsWindows())
            throw new NotSupportedException("Offscreen.UseSharedTexture is supported on Windows only for now (drift C63).");
        if (Offscreen is { DeviceScaleFactor: <= 0 }) throw new ArgumentOutOfRangeException(paramName, "Offscreen.DeviceScaleFactor must be greater than 0.");
        // Chrome-style browsers take these from the profile's preferences, over the browser settings.
        if (MinimumFontSize is not null) throw new NotSupportedException("MinimumFontSize is not supported: CEF applies the profile's value to every window.");
        if (DefaultEncoding is not null) throw new NotSupportedException("DefaultEncoding is not supported: CEF applies the profile's value to every window.");
        if (ZoomFactor <= 0) throw new ArgumentOutOfRangeException(paramName, "ZoomFactor must be greater than 0.");
    }

    internal Session ResolveSession()
    {
        var session = Session ?? Riftway.Session.FromPartition(Partition ?? "");
        if (Riftway.Session.ScrollBounceMismatch(ScrollBounce, session.ScrollBounce) is { } bounce) throw new ArgumentException(bounce, "options");
        return Riftway.Session.SandboxMismatch(Sandbox, session.Sandbox) is { } error ? throw new ArgumentException(error, "options") : session;
    }
}

/// <summary>Fonts for CSS's generic font families; null keeps Chromium's choice. Electron's <c>defaultFontFamily</c>.</summary>
public sealed record DefaultFontFamily
{
    /// <summary>Text with no <c>font-family</c>.</summary>
    public string? Standard { get; init; }

    /// <summary>The <c>serif</c> family.</summary>
    public string? Serif { get; init; }

    /// <summary>The <c>sans-serif</c> family.</summary>
    public string? SansSerif { get; init; }

    /// <summary>The <c>monospace</c> family.</summary>
    public string? Monospace { get; init; }

    /// <summary>The <c>cursive</c> family.</summary>
    public string? Cursive { get; init; }

    /// <summary>The <c>fantasy</c> family.</summary>
    public string? Fantasy { get; init; }

    /// <summary>The <c>math</c> family. Not supported: CEF has no setting for it.</summary>
    public string? Math { get; init; }
}

/// <summary>A top-level window hosting web content; Electron's <c>BrowserWindow</c>.</summary>
/// <remarks>
/// Create after <see cref="RiftwayApp.Ready"/>. Members may be called from any thread: the constructor and
/// queries wait for the UI thread, commands are queued to it. Events are raised on the UI thread.
/// Electron's event names collide with its method names in C# (<c>focus()</c> / <c>'focus'</c>), so
/// events use .NET's past tense: <see cref="BaseWindow.Focused"/>, <see cref="BaseWindow.Moved"/>,
/// <see cref="BaseWindow.Maximized"/>, ...
/// </remarks>
public sealed unsafe partial class BrowserWindow : BaseWindow
{
    private readonly ClientHandler _client;
    private readonly BrowserViewDelegate _viewDelegate;
    private cef_browser_view_t* _browserView;
    private bool _readyToShowRaised;
    private bool _panelHoldsFocus;
    private readonly UnresponsiveSchedule _unresponsive;
    // Popups allowed by on_before_popup whose view CEF has not created yet, by the client CEF will use; UI thread only.
    private static readonly Dictionary<nint, PendingPopup> s_pendingPopups = [];
    private WebContents? _opener;

    // Where a popup's view goes: a window's page or a view's (from a createWindow callback).
    private sealed record PendingPopup(BrowserWindow? Window, WebContentsView? View, WebContents Page, int PopupId,
        WindowOpenDetails Details, bool RaiseDidCreateWindow, bool ClosesWithOpener)
    {
        public nint Delegate => Window is { } window ? (nint)window._viewDelegate.Native : View!.ViewDelegateNative;
    }

    /// <summary>Creates the window, shown unless <see cref="BaseWindowOptions.Show"/> is false.</summary>
    /// <exception cref="InvalidOperationException">The application is not ready yet, or the parent is closed.</exception>
    /// <exception cref="ArgumentException"><see cref="BaseWindowOptions.Modal"/> without a parent.</exception>
    /// <exception cref="NotSupportedException">An option CEF cannot honour, such as <see cref="WebPreferences.MinimumFontSize"/>,
    /// or a <see cref="BrowserWindowOptions.WebContents"/> that is not a createWindow callback's popup page.</exception>
    public BrowserWindow(BrowserWindowOptions? options = null) : this(options ?? new BrowserWindowOptions(), opener: null)
    {
    }

    // A popup's window waits for CEF to create its browser view (AdoptPopupView) instead of creating one.
    private BrowserWindow(BrowserWindowOptions options, WebContents? opener) : base(Validated(options, ref opener), opensItself: false)
    {
        var adopted = options.WebContents;
        var session = adopted?.Session ?? options.WebPreferences.ResolveSession();
        if (adopted is not null) PopupGuest.Adopt(adopted, this);
        WebContents = adopted ?? new WebContents(options.WebPreferences.Preload, options.WebPreferences, session) { IsAlloyStyle = opener?.IsAlloyStyle ?? false };
        _pageView = new WebContentsView(WebContents);
        CefThread.InvokeOnUi(() =>
        {
            _contentsView.InsertChild(_pageView, 0);
            return true;
        });
        WebContents.PageTitleUpdated += (_, e) => PageTitleUpdated?.Invoke(this, e);
        WebContents.EnterHtmlFullScreen += (_, _) => EnterHtmlFullScreen?.Invoke(this, EventArgs.Empty);
        WebContents.LeaveHtmlFullScreen += (_, _) => LeaveHtmlFullScreen?.Invoke(this, EventArgs.Empty);
        _unresponsive = new UnresponsiveSchedule(CefThread.PostUiDelayed, () => !IsDestroyed && IsEnabled, () => Unresponsive?.Invoke(this, EventArgs.Empty));
        WebContents.Unresponsive += (_, _) => _unresponsive.PageUnresponsive();
        WebContents.Responsive += (_, _) =>
        {
            _unresponsive.PageResponsive();
            Responsive?.Invoke(this, EventArgs.Empty);
        };
        WebContents.Destroyed += (_, _) => _unresponsive.PageDestroyed();
        WebContents.Focused += (_, _) => ReleasePanelFocus();
        WebContents.TitleApplied = SetTitle;
        WebContents.PageMouseDown = () => _menuBar?.OnPageMouseDown();
        WebContents.DidFinishLoad += (_, _) =>
        {
            if (_readyToShowRaised) return;
            _readyToShowRaised = true;
            ReadyToShow?.Invoke(this, EventArgs.Empty);
        };
        _client = new ClientHandler(WebContents.Display, WebContents.Load, WebContents.JsDialog, WebContents.Request, WebContents.Command,
            WebContents.ContextMenuRequests, new KeyboardHandler(keyEvent => BlocksInput || WebContents.OnPreKeyEvent(keyEvent) || (!WebContents.IgnoresMenuShortcuts && (_menuBar?.OnKeyEvent(keyEvent) ?? false)),
                // Electron's macOS PlatformHandleKeyboardEvent returns before the app menu's performKeyEquivalent: for a page
                // that ignores menu shortcuts; char events never reach the menu. Elsewhere the bar runs its Windows-key
                // accelerators here, which CEF's set_accelerator cannot take.
                keyEvent => OperatingSystem.IsMacOS()
                    ? WebContents.IgnoresMenuShortcuts && keyEvent->type != cef_key_event_type_t.KEYEVENT_CHAR
                    : !WebContents.IgnoresMenuShortcuts && (_menuBar?.OnUnhandledKey(keyEvent) ?? false)),
            new LifeSpanHandler
            {
                BeforePopup = (request, features, settings) => OpenPopup(WebContents, request, features, settings),
                PopupAborted = OnPopupAborted,
                PopupWindowless = IsWindowlessPopup,
                // A windowless page has no browser view to report it: CEF's life span handler does.
                AfterCreated = WebContents.IsOffscreen() ? OnBrowserCreated : null,
                BeforeClose = WebContents.IsOffscreen() ? OnOffscreenBrowserClosed : null,
            },
            new DragHandler(regions => Command(w => SetDraggableRegionsOn(w, regions))))
        {
            Permission = WebContents.Permissions,
            Download = WebContents.Downloads,
            Find = WebContents.Finds,
            Focus = WebContents.FocusEvents,
            Render = WebContents.OffscreenRender,
        };
        if (WebContents.IsOffscreen()) Resized += (_, _) => UpdateOffscreenView();
        _viewDelegate = new BrowserViewDelegate
        {
            BrowserCreated = OnBrowserCreated,
            BrowserDestroyed = OnBrowserDestroyed,
            PopupDelegate = PopupDelegateFor,
            PopupViewCreated = (view, popupDelegate) => AdoptPopupView(WebContents, view, popupDelegate),
            ThemeChanged = () => Command(ApplyBackgroundColor),
        };

        RiftwayApp.CurrentOrNull?.RaiseBrowserWindowCreated(this);
        if (opener is not null)
        {
            _opener = opener;
            return;
        }
        // CEF refuses a browser view while its profile loads (cef_browser_view_create returns null), and the UI thread
        // cannot wait for that: the window opens at once, as Electron's, and its page joins once the profile has loaded.
        var profile = WebContents.Session;
        profile.WaitUntilInitialized();
        CefThread.InvokeOnUi(() =>
        {
            ThrowIfParentClosed();
            if (profile.Initialized.IsCompleted)
            {
                if (!CreateBrowserView())
                {
                    ReleaseName();
                    throw new InvalidOperationException("CEF could not create the window's page.");
                }
                OpenWindow();
                return true;
            }
            _page = PageState.Pending;
            OpenWindow();
            profile.Initialized.ContinueWith(_ =>
            {
                try { CefThread.RunOnUi(AttachPendingPage); }
                catch (Exception e) { Console.Error.WriteLine($"[Riftway] Window {Id} could not get its page: {e}"); }
            }, TaskScheduler.Default);
            return true;
        });
    }

    // A new on-disk session's window opens before its page, which waits for the profile; UI thread only.
    private PageState _page;

    private enum PageState { Created, Pending, Abandoned }

    private void AttachPendingPage()
    {
        if (_page != PageState.Pending || _window is null) return;
        if (!CreateBrowserView())
        {
            // Electron would have thrown from the constructor; the window cannot exist without its page.
            Console.Error.WriteLine($"[Riftway] Window {Id}: CEF could not create the window's page.");
            Destroy();
            return;
        }
        _page = PageState.Created;
        ApplyBackgroundColor(_window);
    }

    // Runs before the base constructor claims the window's name, so a rejected window leaves it free.
    private static BrowserWindowOptions Validated(BrowserWindowOptions options, ref WebContents? opener)
    {
        CheckOptions(options);
        options.Validate();
        var adopted = options.WebContents;
        var session = adopted?.Session ?? options.WebPreferences.ResolveSession();
        if (adopted is not null) opener = PopupGuest.Adoptable(adopted).Opener;
        // CEF gives a popup its opener's request context, whatever the options say.
        if (opener is not null && session != opener.Session)
            throw new InvalidOperationException("A window a page opens must use its opener's session.");
        if (opener is not null && OffscreenMismatch(opener, options.WebPreferences) is { } offscreen) throw new InvalidOperationException(offscreen);
        return options;
    }

    private BrowserWindowOptions BrowserOptions => (BrowserWindowOptions)_options;

    private bool CreateBrowserView()
    {
        var settings = new cef_browser_settings_t
        {
            size = (nuint)sizeof(cef_browser_settings_t),
            background_color = _backgroundColor is null ? 0 : ParseColor(_backgroundColor),
            windowless_frame_rate = WebContents.InitialFrameRate,
        };
        ApplyWebPreferences(&settings, BrowserOptions.WebPreferences);
        // A document must exist before DevTools answers commands, and the preload is registered
        // over DevTools before the real page loads.
        cef_string_utf16_t url = default;
        CefString.Set(&url, "about:blank");

        NativeObject.AddRef(_client.Native);
        NativeObject.AddRef(_viewDelegate.Native);
        // Null is the global context, which the default session wraps.
        var context = WebContents.Session.IsDefault ? null : WebContents.Session.AddRefNative();
        if (WebContents.IsOffscreen())
        {
            // No view: the page renders into Paint, at the window's content size; CEF reports it through the client.
            NativeObject.Release(_viewDelegate.Native);
            UpdateOffscreenView();
            var info = cef_window_info_native_t.Windowless();
            if (WebContents.UsesSharedTexture) info.win.shared_texture_enabled = 1;
            var created = CefNative.cef_browser_host_create_browser((cef_window_info_t*)&info, _client.Native, &url, &settings, null, context) != 0;
            CefString.Clear(&url);
            ClearWebPreferenceStrings(&settings);
            return created;
        }
        _browserView = CefNative.cef_browser_view_create(_client.Native, &url, &settings, null, context, _viewDelegate.Native);
        CefString.Clear(&url);
        ClearWebPreferenceStrings(&settings);
        if (_browserView is null) return false;
        _pageView.SetNative(_browserView);
        return true;
    }

    // UI thread, inside CEF's on_before_popup: asks the page's handler, then prepares the window CEF's popup will live in.
    internal static cef_client_t* OpenPopup(WebContents opener, PopupRequest request, cef_popup_features_t* features, cef_browser_settings_t* settings)
    {
        var details = new WindowOpenDetails(request.Url, request.FrameName, FeaturesOf(features), DispositionOf(request.Disposition))
        {
            UserGesture = request.UserGesture,
        };
        var response = opener.DecideWindowOpen(details);
        if (!response.IsAllowed) return null;

        var parsed = PopupOptionsFrom(features, opener.Preferences);
        var options = response.OverrideBrowserWindowOptions ?? response.ConfigureBrowserWindowOptions?.Invoke(parsed) ?? parsed;
        if (response.CreateWindow is { } createWindow)
            return OpenCreatedPopup(opener, request, details, options, createWindow, response.OutlivesOpener, settings);
        BrowserWindow popup;
        try { popup = new BrowserWindow(options, opener); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[Riftway] Blocked a window the page opened: {e.Message}");
            return null;
        }
        settings->background_color = popup._backgroundColor is null ? settings->background_color : ParseColor(popup._backgroundColor);
        ApplyWebPreferences(settings, popup.BrowserOptions.WebPreferences);
        s_pendingPopups[(nint)popup._client.Native] = new PendingPopup(popup, null, popup.WebContents, request.PopupId, details,
            RaiseDidCreateWindow: true, ClosesWithOpener: !response.OutlivesOpener);
        return popup._client.Native;
    }

    // Electron's createWindow path: the popup's page exists before the app's window or view does, which adopts it. Under
    // CEF the browser comes after on_before_popup, so the page waits here, and CEF is given the adopter's client.
    private static cef_client_t* OpenCreatedPopup(WebContents opener, PopupRequest request, WindowOpenDetails details, BrowserWindowOptions options,
        Func<BrowserWindowOptions, WebContents?> createWindow, bool outlivesOpener, cef_browser_settings_t* settings)
    {
        var preferences = options.WebPreferences;
        try
        {
            preferences.Validate(nameof(options));
            // CEF gives a popup its opener's request context, whatever the options say.
            if (preferences.ResolveSession() != opener.Session) throw new InvalidOperationException("A window a page opens must use its opener's session.");
            if (OffscreenMismatch(opener, preferences) is { } offscreen) throw new InvalidOperationException(offscreen);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[Riftway] Blocked a window the page opened: {e.Message}");
            return null;
        }
        var page = new WebContents(preferences.Preload, preferences, opener.Session) { IsAlloyStyle = opener.IsAlloyStyle };
        var guest = page.Guest = new PopupGuest(opener);
        WebContents? returned = null;
        try { returned = createWindow(options with { WebContents = page }); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in createWindow: {e}"); }
        finally { page.Guest = null; }

        var (adopted, returnedThePage) = (guest.Host is not null, ReferenceEquals(returned, page));
        if (PopupGuest.ErrorFor(adopted, returnedThePage) is { } error) Console.Error.WriteLine($"[Riftway] {error}");
        var outcome = PopupGuest.Decide(adopted, returnedThePage, outlivesOpener);
        if (outcome == CreateWindowOutcome.Block)
        {
            page.DiscardUnborn();
            return null;
        }
        var (window, view) = (guest.Host as BrowserWindow, guest.Host as WebContentsView);
        if (window?._backgroundColor is { } color) settings->background_color = ParseColor(color);
        ApplyWebPreferences(settings, preferences);
        var client = window is not null ? window._client.Native : view!.ClientNative;
        s_pendingPopups[(nint)client] = new PendingPopup(window, view, page, request.PopupId, details,
            RaiseDidCreateWindow: false, ClosesWithOpener: outcome == CreateWindowOutcome.Wired);
        return client;
    }

    // A windowless opener's popups arrive through no browser view, and a windowed opener's through one: CEF can give a
    // popup the other kind only by its window info, which Riftway sets for offscreen popups of offscreen openers alone.
    private static string? OffscreenMismatch(WebContents opener, WebPreferences preferences)
        => opener.IsOffscreen() == preferences.Offscreen is not null ? null
            : "A window an offscreen page opens must be offscreen too, and a windowed page's windowed (drift C63).";

    // CEF's on_before_popup, after OpenPopup chose this client: whether the popup's page renders offscreen.
    internal static bool IsWindowlessPopup(nint client) => s_pendingPopups.TryGetValue(client, out var pending) && pending.Page.IsOffscreen();

    // An offscreen popup has no view to adopt: its page arrives through the client's life span handler.
    private void AdoptWindowlessPopup()
    {
        if (_opener is not { } opener || !s_pendingPopups.Remove((nint)_client.Native, out var pending) || pending.Window != this) return;
        pending.Page.MarkOpenedByPage();
        if (pending.ClosesWithOpener) opener.Popups.Add(this);
        OpenWindow();
        if (pending.RaiseDidCreateWindow) opener.RaiseDidCreateWindow(new DidCreateWindowEventArgs(this, pending.Details));
    }

    internal static void OnPopupAborted(int popupId)
    {
        foreach (var (client, pending) in s_pendingPopups.ToArray())
        {
            if (pending.PopupId != popupId) continue;
            s_pendingPopups.Remove(client);
            pending.Window?.ReleaseName();
            pending.Page.DiscardUnborn();
        }
    }

    // The delegate for a popup's view: the waiting window's or view's, found by the client OpenPopup chose.
    internal static nint PopupDelegateFor(nint client)
        => s_pendingPopups.TryGetValue(client, out var pending) ? pending.Delegate : 0;

    // The popup's view arrives with the delegate PopupDelegateFor chose, which identifies the waiting window or view.
    internal static bool AdoptPopupView(WebContents opener, nint popupView, nint popupDelegate)
    {
        foreach (var (client, pending) in s_pendingPopups.ToArray())
        {
            if (pending.Delegate != popupDelegate) continue;
            s_pendingPopups.Remove(client);
            pending.Page.MarkOpenedByPage();
            if (pending.Window is { } popup)
            {
                NativeObject.AddRef((void*)popupView);
                popup._browserView = (cef_browser_view_t*)popupView;
                popup._pageView.SetNative(popup._browserView);
                if (pending.ClosesWithOpener) opener.Popups.Add(popup);
                popup.OpenWindow();
                if (pending.RaiseDidCreateWindow) opener.RaiseDidCreateWindow(new DidCreateWindowEventArgs(popup, pending.Details));
            }
            else
            {
                pending.View!.AdoptPopupPage((cef_browser_view_t*)popupView);
                if (pending.ClosesWithOpener) opener.ClosePopupPageWithThis(pending.Page);
            }
            return true;
        }
        return false;
    }

    /// <summary>The options of a window a page opens with no override: the opener's web preferences, and the size and
    /// position the page asked for (the outer size, as Electron).</summary>
    internal static BrowserWindowOptions PopupOptionsFrom(cef_popup_features_t* features, WebPreferences openerPreferences)
    {
        var options = new BrowserWindowOptions { WebPreferences = openerPreferences };
        // As Electron (parse-features-string.ts): width and height are the window's outer size, not the page's.
        if (features->widthSet != 0) options = options with { Width = features->width };
        if (features->heightSet != 0) options = options with { Height = features->height };
        if (features->xSet != 0 && features->ySet != 0) options = options with { X = features->x, Y = features->y };
        return options;
    }

    /// <summary>Electron's <c>features</c> string, rebuilt from what CEF parsed: position and size only.</summary>
    internal static string FeaturesOf(cef_popup_features_t* features)
    {
        var parts = new List<string>();
        if (features->xSet != 0) parts.Add($"left={features->x}");
        if (features->ySet != 0) parts.Add($"top={features->y}");
        if (features->widthSet != 0) parts.Add($"width={features->width}");
        if (features->heightSet != 0) parts.Add($"height={features->height}");
        return string.Join(',', parts);
    }

    /// <summary>Electron's disposition for CEF's.</summary>
    internal static WindowOpenDisposition DispositionOf(cef_window_open_disposition_t disposition) => disposition switch
    {
        cef_window_open_disposition_t.CEF_WOD_CURRENT_TAB => WindowOpenDisposition.Default,
        cef_window_open_disposition_t.CEF_WOD_NEW_FOREGROUND_TAB => WindowOpenDisposition.ForegroundTab,
        cef_window_open_disposition_t.CEF_WOD_NEW_BACKGROUND_TAB => WindowOpenDisposition.BackgroundTab,
        cef_window_open_disposition_t.CEF_WOD_NEW_POPUP or cef_window_open_disposition_t.CEF_WOD_NEW_WINDOW => WindowOpenDisposition.NewWindow,
        _ => WindowOpenDisposition.Other,
    };

    internal static void ApplyWebPreferences(cef_browser_settings_t* settings, WebPreferences preferences)
    {
        static cef_state_t State(bool enabled) => enabled ? cef_state_t.STATE_DEFAULT : cef_state_t.STATE_DISABLED;
        settings->javascript = State(preferences.Javascript);
        settings->image_loading = State(preferences.Images);
        settings->text_area_resize = State(preferences.TextAreasAreResizable);
        settings->webgl = State(preferences.WebGL);
        if (preferences.DefaultFontFamily is { } fonts)
        {
            if (fonts.Standard is { } standard) CefString.Set(&settings->standard_font_family, standard);
            if (fonts.Serif is { } serif) CefString.Set(&settings->serif_font_family, serif);
            if (fonts.SansSerif is { } sansSerif) CefString.Set(&settings->sans_serif_font_family, sansSerif);
            if (fonts.Monospace is { } monospace) CefString.Set(&settings->fixed_font_family, monospace);
            if (fonts.Cursive is { } cursive) CefString.Set(&settings->cursive_font_family, cursive);
            if (fonts.Fantasy is { } fantasy) CefString.Set(&settings->fantasy_font_family, fantasy);
        }
    }

    // CEF copies the settings, so the strings are freed once the browser view exists.
    internal static void ClearWebPreferenceStrings(cef_browser_settings_t* settings)
    {
        CefString.Clear(&settings->standard_font_family);
        CefString.Clear(&settings->serif_font_family);
        CefString.Clear(&settings->sans_serif_font_family);
        CefString.Clear(&settings->fixed_font_family);
        CefString.Clear(&settings->cursive_font_family);
        CefString.Clear(&settings->fantasy_font_family);
    }

    /// <summary>The window's page. Electron's <c>win.webContents</c>.</summary>
    public WebContents WebContents { get; }

    private readonly WebContentsView _pageView;

    /// <summary>All open windows. Electron's <c>BrowserWindow.getAllWindows()</c>.</summary>
    public static new IReadOnlyList<BrowserWindow> GetAllWindows()
    {
        lock (s_openGate) return [.. s_open.OfType<BrowserWindow>()];
    }

    /// <summary>The focused window, or null. Electron's <c>BrowserWindow.getFocusedWindow()</c>.</summary>
    public static new BrowserWindow? GetFocusedWindow() => GetAllWindows().FirstOrDefault(w => w.IsFocused);

    /// <summary>The window showing <paramref name="webContents"/>, or null. Electron's <c>BrowserWindow.fromWebContents()</c>.</summary>
    public static BrowserWindow? FromWebContents(WebContents webContents)
        => GetAllWindows().FirstOrDefault(w => ReferenceEquals(w.WebContents, webContents));

    /// <summary>The open window with <paramref name="id"/>, or null. Electron's <c>BrowserWindow.fromId()</c>.</summary>
    public static new BrowserWindow? FromId(int id) => GetAllWindows().FirstOrDefault(w => w.Id == id);

    /// <summary>The page set its title; cancel to keep the window's title. Electron's <c>page-title-updated</c>.</summary>
    public event EventHandler<PageTitleUpdatedEventArgs>? PageTitleUpdated;

    /// <summary>The page entered HTML full screen. Electron's <c>enter-html-full-screen</c>.</summary>
    public event EventHandler? EnterHtmlFullScreen;

    /// <summary>The page left HTML full screen. Electron's <c>leave-html-full-screen</c>.</summary>
    public event EventHandler? LeaveHtmlFullScreen;

    /// <summary>The page has stopped answering input, 50 ms after its hang is reported, or 5 s after a close goes ahead
    /// without the page closing; not while the window is disabled. Electron's <c>unresponsive</c>.</summary>
    public event EventHandler? Unresponsive;

    /// <summary>The page answers input again. Electron's <c>responsive</c>.</summary>
    public event EventHandler? Responsive;

    /// <summary>The page finished its first load, so the window can be shown without a blank flash.
    /// Electron's <c>ready-to-show</c>.</summary>
    public event EventHandler? ReadyToShow;

    /// <summary>Navigates the window's page. Electron's <c>win.loadURL()</c>.</summary>
    /// <inheritdoc cref="Riftway.WebContents.LoadUrl" path="/param|/exception"/>
    public Task LoadUrl(string url, LoadUrlOptions? options = null) => WebContents.LoadUrl(url, options);

    /// <summary>Loads a local file in the window's page. Electron's <c>win.loadFile()</c>.</summary>
    public Task LoadFile(string path, IReadOnlyDictionary<string, string>? query = null, string? hash = null, string? search = null)
        => WebContents.LoadFile(path, query, hash, search);

    /// <summary>Reloads the window's page. Electron's <c>win.reload()</c>.</summary>
    public void Reload() => WebContents.Reload();

    /// <summary>Captures the page, or <paramref name="rect"/> of it in DIPs. Electron's <c>win.capturePage()</c>, which is
    /// <see cref="WebContents.CapturePageAsync"/>.</summary>
    /// <inheritdoc cref="Riftway.WebContents.CapturePageAsync" path="/param|/exception"/>
    public Task<NativeImage> CapturePageAsync(Rectangle? rect = null, CapturePageOptions? options = null) => WebContents.CapturePageAsync(rect, options);

    /// <summary>Gives the page keyboard focus, leaving the window as it is. Electron's <c>win.focusOnWebView()</c>.</summary>
    public void FocusOnWebView() => WebContents.SetWidgetFocus(true);

    /// <summary>Takes keyboard focus from the page. Electron's <c>win.blurWebView()</c>.</summary>
    public void BlurWebView() => Command(_ =>
    {
        // CEF's set_focus(0) does nothing for a Views-hosted page, so the window's own panel takes the focus, focusable
        // only while it holds it so Tab never stops there.
        var panel = _contentsView.NativeView;
        if (panel is null || !WebContents.ViewHasFocus()) return;
        panel->set_focusable(panel, 1);
        panel->request_focus(panel);
        _panelHoldsFocus = true;
        WebContents.OnViewBlurred();
    });

    // UI thread: the page has the focus back, so the panel can stop being focusable without Views moving focus.
    private void ReleasePanelFocus()
    {
        var panel = _contentsView.NativeView;
        if (!_panelHoldsFocus || panel is null) return;
        _panelHoldsFocus = false;
        panel->set_focusable(panel, 0);
    }

    /// <summary>Electron's <c>win.showDefinitionForSelection()</c>: <see cref="WebContents.ShowDefinitionForSelection"/>.</summary>
    public void ShowDefinitionForSelection() => WebContents.ShowDefinitionForSelection();

    // CEF takes only opaque colours for a windowed browser (GetColor in libcef/browser/context.cc) and cannot change the
    // page's after creation; DevTools' default background override can, and lasts across navigations.
    internal static void SendPageBackground(WebContents page, uint argb)
    {
        if (page.Browser is null) return;
        var (r, g, b, a) = WindowTransparency.PageColor(argb);
        page.DevTools.Send("Emulation.setDefaultBackgroundColorOverride", w =>
        {
            w.WritePropertyName("color");
            w.WriteStartObject();
            w.WriteNumber("r", r);
            w.WriteNumber("g", g);
            w.WriteNumber("b", b);
            w.WriteNumber("a", a);
            w.WriteEndObject();
        });
    }

    private void OnBrowserCreated(cef_browser_t* browser)
    {
        WebContents.OnBrowserCreated(browser);
        if (WebContents.IsOffscreen())
        {
            AdoptWindowlessPopup();
            // Created before the window opened, a windowless page took no content size: it takes it now.
            UpdateOffscreenView();
        }
        ApplyPageBackground();
    }

    // Electron's OffScreenWebContentsView::GetSize: the window's outer size, not its content size.
    private void UpdateOffscreenView()
    {
        var (width, height) = GetSize();
        WebContents.SetOffscreenView(width, height);
    }

    // An offscreen page has gone: closed by its window, or by itself. Nothing else closes the window then, so the
    // window's close runs again, and with the page gone it is approved.
    private void OnOffscreenBrowserClosed(cef_browser_t* browser)
    {
        OnBrowserDestroyed(browser);
        CefThread.PostUi(() =>
        {
            if (!IsDestroyed) Close();
        });
    }

    // The window's page goes first; the window's teardown follows at once, without asking can_close again.
    private void OnBrowserDestroyed(cef_browser_t* browser)
    {
        DetachViews();
        WebContents.OnBrowserDestroyed();
    }

    private protected override WebContents? OwnPage => WebContents;

    private protected override bool IgnoresMenuShortcuts => WebContents.IgnoresMenuShortcuts;

    private protected override bool PageTransparent => BrowserOptions.WebPreferences.Transparent;

    private protected override void ApplyPageViewBackground(uint argb)
    {
        if (_browserView is not null) _browserView->@base.set_background_color(&_browserView->@base, argb);
    }

    private protected override void ApplyPageBackground()
    {
        if (WindowTransparency.PageBackgroundColor(_backgroundColor, PageTransparent) is { } page)
            SendPageBackground(WebContents, ParseColor(page));
    }

    // No beforeunload: the page closes at once, and _destroying keeps the can_close that follows from raising Closing.
    private protected override void DestroyContents()
    {
        if (_page == PageState.Pending) _page = PageState.Abandoned;
        var browser = WebContents.Browser;
        if (browser is null) return;
        var host = browser->get_host(browser);
        WebContents.CloseWithoutUnload(host);
        NativeObject.Release(host);
    }

    // A page still waiting for its profile has not approved anything: the close goes to Closing first.
    private protected override bool CloseApproved => WebContents.Browser is null && _page != PageState.Pending;

    // Lets the page run beforeunload first; the window closes once the browser has gone.
    private protected override bool ContinueClose()
    {
        _unresponsive.CloseProceeding();
        var browser = WebContents.Browser;
        // A page still waiting for its profile has no beforeunload to run, and is never made.
        if (browser is null)
        {
            _page = PageState.Abandoned;
            return true;
        }
        var host = browser->get_host(browser);
        var canClose = host->try_close_browser(host) != 0;
        NativeObject.Release(host);
        return canClose;
    }

    // The page's view loses focus with its window, as a Chromium view does when its window resigns.
    private protected override void OnWindowDeactivated() => WebContents.OnViewBlurred();

    private protected override void OnActivationChangedCore(bool active) => RiftwayApp.CurrentOrNull?.RaiseBrowserWindowFocus(this, active);

    private protected override void OnNativeWindowDestroyed()
    {
        NativeObject.Release(_browserView);
        _browserView = null;
        // A page that was never made still ends with its window, as Electron's web contents do.
        if (_page != PageState.Created)
        {
            _page = PageState.Abandoned;
            WebContents.OnBrowserDestroyed();
        }
    }

    private protected override void DetachFromOpener() => _opener?.Popups.Remove(this);
}
