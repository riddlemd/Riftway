using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Options for a new <see cref="BrowserWindow"/>; Electron's <c>BrowserWindowConstructorOptions</c>.</summary>
public sealed record BrowserWindowOptions
{
    /// <summary>Width in device-independent pixels.</summary>
    public int Width { get; init; } = 800;

    /// <summary>Height in device-independent pixels.</summary>
    public int Height { get; init; } = 600;

    /// <summary>The window title.</summary>
    public string Title { get; init; } = "Oriel";

    /// <summary>Whether the user can resize the window.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>Whether the window can be minimized.</summary>
    public bool Minimizable { get; init; } = true;

    /// <summary>Whether the window can be maximized.</summary>
    public bool Maximizable { get; init; } = true;
}

/// <summary>A top-level window hosting web content; Electron's <c>BrowserWindow</c>.</summary>
/// <remarks>Create on the UI thread, after <see cref="OrielApp.Ready"/>.</remarks>
public sealed unsafe class BrowserWindow
{
    // Native delegates call back into these objects, so open windows must stay reachable.
    private static readonly List<BrowserWindow> s_open = [];

    private readonly BrowserWindowOptions _options;
    private readonly ClientHandler _client = new();
    private readonly BrowserViewDelegate _viewDelegate;
    private readonly WindowDelegate _windowDelegate;

    private cef_browser_view_t* _browserView;
    private cef_window_t* _window;
    private cef_browser_t* _browser;
    private string? _pendingUrl;

    /// <summary>Creates and shows a window.</summary>
    /// <exception cref="InvalidOperationException">The application is not ready yet.</exception>
    public BrowserWindow(BrowserWindowOptions? options = null)
    {
        if (!OrielApp.Current.IsReady) throw new InvalidOperationException("Create windows after OrielApp.Ready.");

        _options = options ?? new BrowserWindowOptions();
        _viewDelegate = new BrowserViewDelegate { BrowserCreated = OnBrowserCreated, BrowserDestroyed = OnBrowserDestroyed };
        _windowDelegate = new WindowDelegate
        {
            Created = OnWindowCreated,
            Closing = OnClosing,
            Destroyed = OnWindowDestroyed,
            Resizable = _options.Resizable,
            Minimizable = _options.Minimizable,
            Maximizable = _options.Maximizable,
        };

        var settings = new cef_browser_settings_t { size = (nuint)sizeof(cef_browser_settings_t) };
        cef_string_utf16_t url = default;

        NativeObject.AddRef(_client.Native);
        NativeObject.AddRef(_viewDelegate.Native);
        _browserView = CefNative.cef_browser_view_create(_client.Native, &url, &settings, null, null, _viewDelegate.Native);

        s_open.Add(this);
        NativeObject.AddRef(_windowDelegate.Native);
        _window = CefNative.cef_window_create_top_level(_windowDelegate.Native);
    }

    /// <summary>The window closed. Electron's <c>closed</c>.</summary>
    public event EventHandler? Closed;

    /// <summary>Navigates the window's page. Electron's <c>win.loadURL()</c>.</summary>
    public void LoadUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        // The browser exists once the view joins a window; until then the URL waits.
        if (_browser is null)
        {
            _pendingUrl = url;
            return;
        }

        var frame = _browser->get_main_frame(_browser);
        cef_string_utf16_t value = default;
        CefString.Set(&value, url);
        frame->load_url(frame, &value);
        CefString.Clear(&value);
        NativeObject.Release(frame);
    }

    private void OnWindowCreated(cef_window_t* window)
    {
        NativeObject.AddRef(_browserView);
        window->@base.add_child_view(&window->@base, (cef_view_t*)_browserView);

        var size = new cef_size_t { width = _options.Width, height = _options.Height };
        window->center_window(window, &size);

        cef_string_utf16_t title = default;
        CefString.Set(&title, _options.Title);
        window->set_title(window, &title);
        CefString.Clear(&title);

        window->show(window);
    }

    private void OnBrowserCreated(cef_browser_t* browser)
    {
        NativeObject.AddRef(browser);
        _browser = browser;

        if (_pendingUrl is { } url)
        {
            _pendingUrl = null;
            LoadUrl(url);
        }
    }

    private bool OnClosing(cef_window_t* window)
    {
        if (_browser is null) return true;

        // Lets the page run beforeunload first; the window closes once the browser has gone.
        var host = _browser->get_host(_browser);
        var canClose = host->try_close_browser(host) != 0;
        NativeObject.Release(host);
        return canClose;
    }

    private void OnBrowserDestroyed(cef_browser_t* browser)
    {
        NativeObject.Release(_browser);
        _browser = null;
    }

    private void OnWindowDestroyed(cef_window_t* window)
    {
        NativeObject.Release(_browserView);
        NativeObject.Release(_window);
        _browserView = null;
        _window = null;

        s_open.Remove(this);
        Closed?.Invoke(this, EventArgs.Empty);
        if (s_open.Count == 0) OrielApp.Current.OnAllWindowsClosed();
    }
}
