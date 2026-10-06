using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Options for a <see cref="WebContentsView"/>. Electron's <c>WebContentsView</c> constructor options.</summary>
public sealed record WebContentsViewOptions
{
    /// <summary>Settings for the view's page. Electron's <c>webPreferences</c>.</summary>
    public WebPreferences WebPreferences { get; init; } = new();
}

/// <summary>A view showing a page, placed inside a window's <see cref="BrowserWindow.ContentView"/> or another
/// <see cref="View"/>. Electron's <c>WebContentsView</c>.</summary>
/// <remarks>The page loads once the view is first added to a window, and fills the view below any child views. It is an
/// Alloy-style browser: a window hosts one Chrome-style page, its own (DRIFT V2). As Electron, the view stays where it is
/// when its page closes.</remarks>
public sealed unsafe class WebContentsView : View
{
    // Pages of views with a browser, which CEF needs closed before it shuts down.
    private static readonly HashSet<WebContentsView> s_live = [];
    private static Action? s_allClosed;
    // Keeps each teardown window's delegate alive until CEF is done with it.
    private static readonly HashSet<WindowDelegate> s_graveyards = [];

    // Keep the handlers alive while CEF may still call them.
    private readonly ClientHandler? _client;
    private readonly BrowserViewDelegate? _viewDelegate;

    /// <summary>Creates a view with a new page. Electron's <c>new WebContentsView()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    /// <exception cref="NotSupportedException">A web preference CEF cannot apply.</exception>
    public WebContentsView(WebContentsViewOptions? options = null)
    {
        RequireReady();
        var preferences = (options ?? new WebContentsViewOptions()).WebPreferences;
        preferences.Validate(nameof(options));
        var session = preferences.ResolveSession();
        WebContents = new WebContents(preferences.Preload, preferences, session);
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
            WebContents.ContextMenuRequests, new KeyboardHandler(keyEvent => WebContents.OnPreKeyEvent(keyEvent)), lifeSpan)
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
                s_live.Add(this);
                WebContents.OnBrowserCreated(browser);
            },
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
        // CEF refuses a browser view while its profile is still loading, as for a window's page.
        session.WaitUntilInitialized();
        CefThread.InvokeOnUi(() =>
        {
            if (session.Initialized.IsCompleted) CreatePage(preferences, session);
            else session.Initialized.ContinueWith(_ => CefThread.RunOnUi(() => CreatePage(preferences, session)), TaskScheduler.Default);
            return true;
        });
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
            NativeObject.Release(TakePage());
            return;
        }
        // CEF then calls do_close, which tears the view down.
        WebContents.Close();
    }

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
