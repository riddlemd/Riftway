using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Options for a <see cref="WebContentsView"/>. Electron's <c>WebContentsView</c> constructor options.</summary>
public sealed record WebContentsViewOptions
{
    /// <summary>Settings for the view's page. Electron's <c>webPreferences</c>.</summary>
    public WebPreferences WebPreferences { get; init; } = new();

    /// <summary>The page the view shows instead of a new one, whose preferences then apply: only the popup page a
    /// <see cref="WindowOpenHandlerResponse.CreateWindow"/> callback receives, from a view's page, adopted while it runs
    /// (drift V4). Electron's <c>webContents</c>.</summary>
    public WebContents? WebContents { get; init; }
}

/// <summary>A view showing a page, placed inside a window's <see cref="BaseWindow.ContentView"/> or another
/// <see cref="View"/>. Electron's <c>WebContentsView</c>.</summary>
/// <remarks>The page loads once the view is first added to a window, and fills the view below any child views. It is an
/// Alloy-style browser: a window hosts one Chrome-style page, its own (DRIFT V2). As Electron, the view stays where it is
/// when its page closes.</remarks>
public sealed unsafe class WebContentsView : View
{
    // Pages of views with a browser, which CEF needs closed before it shuts down.
    private static readonly HashSet<WebContentsView> s_live = [];
    // Pages of views never added to a window: no browser to close, but a browser view CEF needs released before it
    // shuts down (it observes the accessibility mode, which must have no observers left).
    private static readonly HashSet<WebContentsView> s_unstarted = [];
    private static Action? s_allClosed;
    // Keeps each teardown window's delegate alive until CEF is done with it.
    private static readonly HashSet<WindowDelegate> s_graveyards = [];

    // Keep the handlers alive while CEF may still call them.
    private readonly ClientHandler? _client;
    private readonly BrowserViewDelegate? _viewDelegate;
    // The page's background, Electron's background_color_: transparent from webPreferences.transparent, or what
    // SetBackgroundColor gave; null leaves Chromium's white.
    private uint? _pageColor;

    /// <summary>Creates a view with a new page. Electron's <c>new WebContentsView()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    /// <exception cref="NotSupportedException">A web preference CEF cannot apply, or a
    /// <see cref="WebContentsViewOptions.WebContents"/> that is not a createWindow callback's popup page from a view's page.</exception>
    public WebContentsView(WebContentsViewOptions? options = null)
    {
        RequireReady();
        options ??= new WebContentsViewOptions();
        var preferences = options.WebPreferences;
        preferences.Validate(nameof(options));
        if (options.WebContents is { } adopted)
        {
            // CEF makes a popup its opener's style, and a window's own page is its one Chrome-style browser.
            if (!adopted.IsAlloyStyle && adopted.Guest is not null)
                throw new NotSupportedException("A popup of a window's own page can only be shown as a BrowserWindow's page: CEF gives it the opener's Chrome style.");
            PopupGuest.Adopt(adopted, this);
        }
        _pageColor = WindowTransparency.PageBackgroundColor(null, preferences.Transparent) is { } page ? BrowserWindow.ParseColor(page) : null;
        var session = options.WebContents?.Session ?? preferences.ResolveSession();
        WebContents = options.WebContents ?? new WebContents(preferences.Preload, preferences, session) { IsAlloyStyle = true };
        var lifeSpan = new LifeSpanHandler
        {
            BeforePopup = (request, features, settings) => BrowserWindow.OpenPopup(WebContents, request, features, settings),
            PopupAborted = BrowserWindow.OnPopupAborted,
            // A view's page would otherwise close the whole window (CEF's default for an Alloy browser).
            Closing = () =>
            {
                CefThread.PostUi(Teardown);
                return true;
            },
        };
        _client = new ClientHandler(WebContents.Display, WebContents.Load, WebContents.JsDialog, WebContents.Request, WebContents.Command,
            // Only before-input-event here: an Alloy page's unhandled keys reach the window delegate, and so the menu bar.
            WebContents.ContextMenuRequests, new KeyboardHandler(keyEvent => BrowserWindow.BlocksInputAround(this) || WebContents.OnPreKeyEvent(keyEvent)), lifeSpan)
        {
            Permission = WebContents.Permissions,
            Download = WebContents.Downloads,
            Find = WebContents.Finds,
            Focus = WebContents.FocusEvents,
        };
        _viewDelegate = new BrowserViewDelegate
        {
            RuntimeStyle = cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY,
            BrowserCreated = browser =>
            {
                s_unstarted.Remove(this);
                s_live.Add(this);
                WebContents.OnBrowserCreated(browser);
                ApplyPageColor();
            },
            // CEF resets the browser view's colour whenever the theme applies.
            ThemeChanged = ApplyPageColor,
            BrowserDestroyed = _ =>
            {
                WebContents.OnBrowserDestroyed();
                s_live.Remove(this);
                if (s_live.Count == 0 && s_allClosed is { } done)
                {
                    s_allClosed = null;
                    done();
                }
            },
            PopupDelegate = BrowserWindow.PopupDelegateFor,
            PopupViewCreated = (view, popupDelegate) => BrowserWindow.AdoptPopupView(WebContents, view, popupDelegate),
        };
        // A popup's browser view comes from CEF (AdoptPopupPage); its page may already exist.
        if (options.WebContents is not null)
        {
            CefThread.RunOnUi(ApplyPageColor);
            return;
        }
        // CEF refuses a browser view while its profile is still loading, as for a window's page.
        session.WaitUntilInitialized();
        CefThread.InvokeOnUi(() =>
        {
            if (session.Initialized.IsCompleted) CreatePage(preferences, session);
            else session.Initialized.ContinueWith(_ => CefThread.RunOnUi(() => CreatePage(preferences, session)), TaskScheduler.Default);
            return true;
        });
    }

    // UI thread.
    private void ApplyPageColor()
    {
        if (_pageColor is not { } argb) return;
        if (Page is not null) Page->set_background_color(Page, WindowTransparency.BrowserViewColor(argb));
        BrowserWindow.SendPageBackground(WebContents, argb);
    }

    // As Electron's WebContentsView::SetBackgroundColor: the page's background follows the view's.
    private protected override void OnBackgroundColorSet(uint argb)
    {
        _pageColor = argb;
        ApplyPageColor();
    }

    // UI thread, once the session's profile has loaded.
    private void CreatePage(WebPreferences preferences, Session session)
    {
        var (client, viewDelegate) = (_client!, _viewDelegate!);
        var settings = new cef_browser_settings_t { size = (nuint)sizeof(cef_browser_settings_t) };
        BrowserWindow.ApplyWebPreferences(&settings, preferences);
        // As a window's page: DevTools needs a document before the preload is registered.
        cef_string_utf16_t url = default;
        CefString.Set(&url, "about:blank");
        NativeObject.AddRef(client.Native);
        NativeObject.AddRef(viewDelegate.Native);
        var context = session.IsDefault ? null : session.AddRefNative();
        SetPage((cef_view_t*)CefNative.cef_browser_view_create(client.Native, &url, &settings, null, context, viewDelegate.Native));
        s_unstarted.Add(this);
        WebContents.CloseWithoutBrowser = CloseUnstarted;
        CefString.Clear(&url);
        BrowserWindow.ClearWebPreferenceStrings(&settings);
    }

    // A window's own page: its browser view comes from the window, now or (for a popup) later.
    internal WebContentsView(WebContents webContents)
    {
        WebContents = webContents;
        FillsParent = true;
    }

    /// <summary>The view's page. Electron's <c>view.webContents</c>.</summary>
    public WebContents WebContents { get; }

    internal cef_client_t* ClientNative => _client!.Native;

    internal nint ViewDelegateNative => (nint)_viewDelegate!.Native;

    /// <summary>UI thread: the popup browser view CEF created for this view's adopted page; CEF's reference stays CEF's.</summary>
    internal void AdoptPopupPage(cef_browser_view_t* popupView)
    {
        NativeObject.AddRef(popupView);
        SetPage((cef_view_t*)popupView);
    }

    // UI thread: a window's own page receives its browser view, with a reference for this object.
    internal void SetNative(cef_browser_view_t* browserView)
    {
        NativeObject.AddRef(browserView);
        SetPage((cef_view_t*)browserView);
    }

    /// <summary>UI thread: closes every view page still open (views outside any window, by quit time), then runs
    /// <paramref name="done"/>.</summary>
    internal static void CloseAll(Action done)
    {
        foreach (var view in s_unstarted.ToArray()) view.ClosePage();
        if (s_live.Count == 0)
        {
            done();
            return;
        }
        s_allClosed = done;
        foreach (var view in s_live.ToArray()) view.ClosePage();
        // A page that never reports closed must not keep the app from quitting.
        CefThread.PostUiDelayed(() =>
        {
            if (s_allClosed != done) return;
            s_allClosed = null;
            done();
        }, TimeSpan.FromSeconds(5));
    }

    // UI thread.
    private void ClosePage()
    {
        // A page whose browser never started (the view never joined a window) needs no teardown.
        if (WebContents.Browser is null)
        {
            s_unstarted.Remove(this);
            NativeObject.Release(TakePage());
            return;
        }
        // CEF then calls do_close, which tears the view down.
        WebContents.Close();
    }

    // UI thread: WebContents.Close on a page that never started. Only outside a window: once in one, CEF is already
    // creating its browser, which close_browser then reaches.
    private void CloseUnstarted()
    {
        if (Parent is not null || !s_unstarted.Remove(this)) return;
        NativeObject.Release(TakePage());
        _closedUnstarted = true;
        WebContents.OnClosedWithoutBrowser();
    }

    // Electron's view stops being usable once its page is destroyed; a closed page that never started stays closed.
    private bool _closedUnstarted;

    private protected override bool IsDestroyedChild => _closedUnstarted;

    // UI thread. The page asked to close. The browser and its view hold each other, and CEF deletes a view only
    // with the window it is in, so the page goes into a hidden window that closes at once; this view stays, empty.
    private void Teardown()
    {
        var view = TakePage();
        if (view is null) return;
        WindowDelegate? graveyard = null;
        graveyard = new WindowDelegate
        {
            Created = window =>
            {
                // Takes over this object's reference.
                window->@base.add_child_view(&window->@base, view);
                NativeObject.AddRef(window);
                CefThread.PostUi(() =>
                {
                    window->close(window);
                    NativeObject.Release(window);
                });
            },
            Destroyed = _ => s_graveyards.Remove(graveyard!),
        };
        s_graveyards.Add(graveyard);
        NativeObject.AddRef(graveyard.Native);
        NativeObject.Release(CefNative.cef_window_create_top_level(graveyard.Native));
    }
}
