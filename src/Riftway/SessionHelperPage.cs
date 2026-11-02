using System.Text.Json;
using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>A page in a session's profile that is never shown, for what CEF only offers through a browser: DevTools
/// commands on the session's storage partition, downloads, and reading <c>chrome://gpu</c>.</summary>
/// <remarks>Not a <see cref="WebContents"/>: Electron's session methods make no page, so the app's events and window
/// list must not see one. UI thread only, except <see cref="SendAsync"/>.</remarks>
internal sealed unsafe partial class SessionHelperPage
{
    private static readonly List<SessionHelperPage> s_open = [];

    private readonly ClientHandler _client;
    private readonly BrowserViewDelegate _viewDelegate;
    private readonly WindowDelegate _windowDelegate;
    private readonly DevToolsChannel _devTools = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private cef_browser_view_t* _view;
    private cef_window_t* _window;
    private cef_browser_t* _browser;
    private bool _closing;

    private SessionHelperPage(Session session, string pageUrl)
    {
        _client = new ClientHandler(load: new LoadHandler
        {
            LoadEnded = (isMain, _) =>
            {
                if (isMain) _ready.TrySetResult();
            },
            LoadFailed = (isMain, code, text, _) =>
            {
                if (isMain) _ready.TrySetException(new InvalidOperationException($"The session's helper page did not load: {text} ({code})."));
            },
        })
        {
            Download = new DownloadHandler
            {
                Before = (snapshot, before) => session.OnBeforeDownload(null, snapshot, before),
                Updated = session.OnDownloadUpdated,
            },
        };
        _viewDelegate = new BrowserViewDelegate
        {
            BrowserCreated = browser =>
            {
                NativeObject.AddRef(browser);
                _browser = browser;
                _devTools.Attach(browser);
            },
            BrowserDestroyed = _ =>
            {
                _devTools.Detach();
                NativeObject.Release(_browser);
                _browser = null;
                _ready.TrySetException(new InvalidOperationException("The session's helper page closed."));
            },
        };
        _windowDelegate = new WindowDelegate
        {
            // Never shown: the window only gives the browser view a widget, which CEF needs to create the browser.
            Created = window =>
            {
                NativeObject.AddRef(_view);
                window->@base.add_child_view(&window->@base, (cef_view_t*)_view);
            },
            Closing = _ =>
            {
                if (_browser is null) return true;
                var host = _browser->get_host(_browser);
                var canClose = host->try_close_browser(host) != 0;
                NativeObject.Release(host);
                return canClose;
            },
            Destroyed = _ =>
            {
                NativeObject.Release(_view);
                NativeObject.Release(_window);
                _view = null;
                _window = null;
                s_open.Remove(this);
            },
        };

        var settings = new cef_browser_settings_t { size = (nuint)sizeof(cef_browser_settings_t) };
        cef_string_utf16_t url = default;
        CefString.Set(&url, pageUrl);
        NativeObject.AddRef(_client.Native);
        NativeObject.AddRef(_viewDelegate.Native);
        var context = session.IsDefault ? null : session.AddRefNative();
        _view = CefNative.cef_browser_view_create(_client.Native, &url, &settings, null, context, _viewDelegate.Native);
        CefString.Clear(&url);
        if (_view is null)
        {
            _ready.TrySetException(new InvalidOperationException("CEF could not create the session's helper page."));
            return;
        }
        s_open.Add(this);
        NativeObject.AddRef(_windowDelegate.Native);
        _window = CefNative.cef_window_create_top_level(_windowDelegate.Native);
    }

    /// <summary>Completes once the page has loaded and DevTools answers.</summary>
    internal Task Ready => _ready.Task;

    /// <summary>Creates the page once the session's profile has loaded: CEF refuses a browser view before.</summary>
    internal static Task<SessionHelperPage> CreateAsync(Session session, string url = "about:blank")
    {
        var created = new TaskCompletionSource<SessionHelperPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Initialized.ContinueWith(_ =>
        {
            // Posting fails once CEF is shutting down; the callers must not wait forever.
            try
            {
                CefThread.RunOnUi(() =>
                {
                    try { created.SetResult(new SessionHelperPage(session, url)); }
                    catch (Exception e) { created.SetException(e); }
                });
            }
            catch (Exception e) { created.TrySetException(e); }
        }, TaskScheduler.Default);
        return created.Task;
    }

    /// <summary>Whether the page still has its browser.</summary>
    internal bool IsAlive => _browser is not null;

    // UI thread.
    private void Send(string method, Action<Utf8JsonWriter>? writeParams, TaskCompletionSource<JsonElement> result, string? sessionId)
    {
        if (_browser is null)
        {
            result.SetException(new InvalidOperationException("The session's helper page closed."));
            return;
        }
        _devTools.Send(method, writeParams, (value, error) =>
        {
            if (error is null) result.SetResult(value);
            else result.SetException(new InvalidOperationException($"DevTools {method} failed: {error}"));
        }, sessionId);
    }

    /// <summary>DevTools events from the page.</summary>
    internal event Action<string, JsonElement>? EventReceived
    {
        add => _devTools.EventReceived += value;
        remove => _devTools.EventReceived -= value;
    }

    /// <summary>DevTools events from the page and from the targets it attached to, with their session (null for the page).</summary>
    internal event Action<string, JsonElement, string?>? AnyEventReceived
    {
        add => _devTools.AnyEventReceived += value;
        remove => _devTools.AnyEventReceived -= value;
    }

    /// <summary>Starts a download in the session. UI thread, once <see cref="Ready"/>.</summary>
    internal void StartDownload(string url)
    {
        if (_browser is null) return;
        var host = _browser->get_host(_browser);
        cef_string_utf16_t native = default;
        CefString.Set(&native, url);
        host->start_download(host, &native);
        CefString.Clear(&native);
        NativeObject.Release(host);
    }

    /// <summary>Closes every helper page, for app quit. UI thread.</summary>
    internal static void CloseAll()
    {
        foreach (var page in s_open.ToArray()) page.Close();
    }

    /// <summary>Closes the page from any thread; nothing to do once CEF is shutting down, which closes it anyway.</summary>
    internal void CloseSoon()
    {
        try { CefThread.RunOnUi(Close); }
        catch (InvalidOperationException) { }
    }

    private void Close()
    {
        if (_closing || _window is null) return;
        _closing = true;
        _window->close(_window);
    }
}

// Outside the unsafe part: C# does not allow await in an unsafe context.
internal sealed partial class SessionHelperPage
{
    /// <summary>Sends a DevTools command once the page is ready, to the page or to a target session from
    /// <c>Target.attachToTarget</c> (<paramref name="sessionId"/>); the task carries its result or fails with its error.</summary>
    internal async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? writeParams = null, string? sessionId = null)
    {
        await _ready.Task.ConfigureAwait(false);
        var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() => Send(method, writeParams, result, sessionId));
        return await result.Task.ConfigureAwait(false);
    }
}
