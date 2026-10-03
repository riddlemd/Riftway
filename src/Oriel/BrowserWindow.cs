using System.ComponentModel;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A rectangle in screen device-independent pixels; Electron's <c>Rectangle</c>.</summary>
public readonly record struct Rectangle(int X, int Y, int Width, int Height);

/// <summary>Options for a new <see cref="BrowserWindow"/>; Electron's <c>BrowserWindowConstructorOptions</c>.</summary>
public sealed record BrowserWindowOptions
{
    /// <summary>Width in device-independent pixels.</summary>
    public int Width { get; init; } = 800;

    /// <summary>Height in device-independent pixels.</summary>
    public int Height { get; init; } = 600;

    /// <summary>Left edge on screen; with <see cref="Y"/>, places the window instead of centring it.</summary>
    public int? X { get; init; }

    /// <summary>Top edge on screen; with <see cref="X"/>, places the window instead of centring it.</summary>
    public int? Y { get; init; }

    /// <summary>The smallest width the user can resize to; 0 for none.</summary>
    public int MinWidth { get; init; }

    /// <summary>The smallest height the user can resize to; 0 for none.</summary>
    public int MinHeight { get; init; }

    /// <summary>The largest width the user can resize to; 0 for none.</summary>
    public int MaxWidth { get; init; }

    /// <summary>The largest height the user can resize to; 0 for none.</summary>
    public int MaxHeight { get; init; }

    /// <summary>The window title.</summary>
    public string Title { get; init; } = "Oriel";

    /// <summary>Whether the window appears when created; otherwise call <see cref="BrowserWindow.Show"/>.</summary>
    public bool Show { get; init; } = true;

    /// <summary>Whether the window has a title bar and border. Electron's <c>frame</c>.</summary>
    public bool Frame { get; init; } = true;

    /// <summary>Whether the user can resize the window.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>Whether the window can be minimized.</summary>
    public bool Minimizable { get; init; } = true;

    /// <summary>Whether the window can be maximized.</summary>
    public bool Maximizable { get; init; } = true;

    /// <summary>Whether the window stays above other windows.</summary>
    public bool AlwaysOnTop { get; init; }

    /// <summary>Whether the window starts full screen.</summary>
    public bool FullScreen { get; init; }

    /// <summary>On macOS, full screen fills the display without moving to a new Space, so other displays
    /// stay usable (native full screen blacks them out). Applies to <see cref="FullScreen"/> and
    /// <see cref="BrowserWindow.SetFullScreen"/>; not yet to the title bar's green button. Ignored
    /// elsewhere. Electron's <c>simpleFullscreen</c>.</summary>
    public bool SimpleFullScreen { get; init; }

    /// <summary>Settings for the page. Electron's <c>webPreferences</c>.</summary>
    public WebPreferences WebPreferences { get; init; } = new();
}

/// <summary>Settings for a window's page; Electron's <c>WebPreferences</c>.</summary>
public sealed record WebPreferences
{
    /// <summary>A script run before the page's own, in an isolated world, with <c>oriel.ipcRenderer</c>
    /// and <c>oriel.contextBridge</c>. Electron's <c>preload</c>.</summary>
    public string? Preload { get; init; }
}

/// <summary>A top-level window hosting web content; Electron's <c>BrowserWindow</c>.</summary>
/// <remarks>
/// Create on the UI thread, after <see cref="OrielApp.Ready"/>. Other members may be called from any
/// thread: queries wait for the UI thread, commands are queued to it. Events are raised on the UI thread.
/// Electron's event names collide with its method names in C# (<c>focus()</c> / <c>'focus'</c>), so
/// events use .NET's past tense: <see cref="Focused"/>, <see cref="Moved"/>, <see cref="Maximized"/>, ...
/// </remarks>
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

    // Last observed state, to turn CEF's bounds callbacks into Electron's specific events.
    private Rectangle _lastBounds;
    private bool _wasMaximized;
    private bool _wasMinimized;

    /// <summary>Creates the window, shown unless <see cref="BrowserWindowOptions.Show"/> is false.</summary>
    /// <exception cref="InvalidOperationException">The application is not ready yet.</exception>
    public BrowserWindow(BrowserWindowOptions? options = null)
    {
        if (!OrielApp.Current.IsReady) throw new InvalidOperationException("Create windows after OrielApp.Ready.");

        _options = options ?? new BrowserWindowOptions();
        WebContents = new WebContents(_options.WebPreferences.Preload is { } preload ? File.ReadAllText(preload) : null);
        _viewDelegate = new BrowserViewDelegate { BrowserCreated = OnBrowserCreated, BrowserDestroyed = OnBrowserDestroyed };
        _windowDelegate = new WindowDelegate
        {
            Created = OnWindowCreated,
            Closing = OnClosing,
            Destroyed = OnWindowDestroyed,
            ActivationChanged = OnActivationChanged,
            BoundsChanged = OnBoundsChanged,
            FullscreenTransition = OnFullscreenTransition,
            Resizable = _options.Resizable,
            Minimizable = _options.Minimizable,
            Maximizable = _options.Maximizable,
            Frameless = !_options.Frame,
            MinimumSize = new cef_size_t { width = _options.MinWidth, height = _options.MinHeight },
            MaximumSize = new cef_size_t { width = _options.MaxWidth, height = _options.MaxHeight },
        };

        var settings = new cef_browser_settings_t { size = (nuint)sizeof(cef_browser_settings_t) };
        // A document must exist before DevTools answers commands, and the preload is registered
        // over DevTools before the real page loads.
        cef_string_utf16_t url = default;
        CefString.Set(&url, "about:blank");

        NativeObject.AddRef(_client.Native);
        NativeObject.AddRef(_viewDelegate.Native);
        _browserView = CefNative.cef_browser_view_create(_client.Native, &url, &settings, null, null, _viewDelegate.Native);
        CefString.Clear(&url);

        s_open.Add(this);
        NativeObject.AddRef(_windowDelegate.Native);
        _window = CefNative.cef_window_create_top_level(_windowDelegate.Native);
    }

    /// <summary>The window's page. Electron's <c>win.webContents</c>.</summary>
    public WebContents WebContents { get; }

    /// <summary>The window is about to close; set <see cref="CancelEventArgs.Cancel"/> to keep it open.
    /// Electron's <c>close</c> with <c>preventDefault()</c>.</summary>
    public event EventHandler<CancelEventArgs>? Closing;

    /// <summary>The window closed. Electron's <c>closed</c>.</summary>
    public event EventHandler? Closed;

    /// <summary>The window gained focus. Electron's <c>focus</c>.</summary>
    public event EventHandler? Focused;

    /// <summary>The window lost focus. Electron's <c>blur</c>.</summary>
    public event EventHandler? Blurred;

    /// <summary>The window became visible. Electron's <c>show</c>.</summary>
    public event EventHandler? Shown;

    /// <summary>The window was hidden. Electron's <c>hide</c>.</summary>
    public event EventHandler? Hidden;

    /// <summary>The window moved. Electron's <c>move</c>.</summary>
    public event EventHandler? Moved;

    /// <summary>The window changed size. Electron's <c>resize</c>.</summary>
    public event EventHandler? Resized;

    /// <summary>The window was maximized. Electron's <c>maximize</c>.</summary>
    public event EventHandler? Maximized;

    /// <summary>The window left the maximized state. Electron's <c>unmaximize</c>.</summary>
    public event EventHandler? Unmaximized;

    /// <summary>The window was minimized. Electron's <c>minimize</c>.</summary>
    public event EventHandler? Minimized;

    /// <summary>The window was restored from minimized. Electron's <c>restore</c>.</summary>
    public event EventHandler? Restored;

    /// <summary>The window finished entering full screen. Electron's <c>enter-full-screen</c>.</summary>
    public event EventHandler? EnteredFullScreen;

    /// <summary>The window finished leaving full screen. Electron's <c>leave-full-screen</c>.</summary>
    public event EventHandler? LeftFullScreen;

    /// <summary>Whether the window has closed. Electron's <c>win.isDestroyed()</c>.</summary>
    public bool IsDestroyed => Query(_ => false, true);

    /// <summary>Navigates the window's page. Electron's <c>win.loadURL()</c>.</summary>
    public void LoadUrl(string url) => WebContents.LoadUrl(url);

    /// <summary>Shows and focuses the window. Electron's <c>win.show()</c>.</summary>
    public void Show() => Command(w =>
    {
        var wasVisible = w->@base.@base.is_visible(&w->@base.@base) != 0;
        w->show(w);
        if (!wasVisible) Shown?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>Hides the window. Electron's <c>win.hide()</c>.</summary>
    public void Hide() => Command(w =>
    {
        var wasVisible = w->@base.@base.is_visible(&w->@base.@base) != 0;
        w->hide(w);
        if (wasVisible) Hidden?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>Whether the window is shown. Electron's <c>win.isVisible()</c>.</summary>
    public bool IsVisible => Query(w => w->@base.@base.is_visible(&w->@base.@base) != 0, false);

    /// <summary>Brings the window to the front and gives it focus. Electron's <c>win.focus()</c>.</summary>
    public void Focus() => Command(w => w->activate(w));

    /// <summary>Removes focus from the window. Electron's <c>win.blur()</c>.</summary>
    public void Blur() => Command(w => w->deactivate(w));

    /// <summary>Whether the window has focus. Electron's <c>win.isFocused()</c>.</summary>
    public bool IsFocused => Query(w => w->is_active(w) != 0, false);

    /// <summary>Maximizes the window. Electron's <c>win.maximize()</c>.</summary>
    public void Maximize() => Command(w => w->maximize(w));

    /// <summary>Leaves the maximized state. Electron's <c>win.unmaximize()</c>.</summary>
    public void Unmaximize() => Command(w =>
    {
        if (w->is_maximized(w) != 0) w->restore(w);
    });

    /// <summary>Whether the window is maximized. Electron's <c>win.isMaximized()</c>.</summary>
    public bool IsMaximized => Query(w => w->is_maximized(w) != 0, false);

    /// <summary>Minimizes the window. Electron's <c>win.minimize()</c>.</summary>
    public void Minimize() => Command(w => w->minimize(w));

    /// <summary>Restores the window from minimized. Electron's <c>win.restore()</c>.</summary>
    public void Restore() => Command(w => w->restore(w));

    /// <summary>Whether the window is minimized. Electron's <c>win.isMinimized()</c>.</summary>
    public bool IsMinimized => Query(w => w->is_minimized(w) != 0, false);

    /// <summary>Enters or leaves full screen. Electron's <c>win.setFullScreen()</c>.</summary>
    /// <remarks>On macOS this is native full screen unless <see cref="BrowserWindowOptions.SimpleFullScreen"/>
    /// is set: native moves the window to its own Space and the other displays go black. For a window on
    /// one display while the others stay usable (an audience screen, a kiosk beside a control window),
    /// set that option or call <see cref="SetSimpleFullScreen"/>.</remarks>
    public void SetFullScreen(bool flag)
    {
        if (UsesSimpleFullScreen) SetSimpleFullScreen(flag);
        else Command(w => w->set_fullscreen(w, flag ? 1 : 0));
    }

    /// <summary>Whether the window is full screen. Electron's <c>win.isFullScreen()</c>.</summary>
    public bool IsFullScreen => UsesSimpleFullScreen ? IsSimpleFullScreen : IsNativeFullScreen;

    private bool IsNativeFullScreen => Query(w => w->is_fullscreen(w) != 0, false);

    private bool UsesSimpleFullScreen => _options.SimpleFullScreen && OperatingSystem.IsMacOS();

    /// <summary>Fills the window's display without leaving the current Space, so other displays stay
    /// usable. Electron's <c>win.setSimpleFullScreen()</c>.</summary>
    /// <remarks>macOS: hides the title bar, menu bar and Dock while active. Elsewhere native full screen
    /// already leaves other displays alone, so this is <see cref="SetFullScreen"/>.</remarks>
    public void SetSimpleFullScreen(bool flag)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Command(w => w->set_fullscreen(w, flag ? 1 : 0));
            return;
        }

        Command(w =>
        {
            if (!OperatingSystem.IsMacOS()) return;
            var wasSimple = IsMacSimpleFullScreen(w);
            MacWindow.SetSimpleFullScreen((nint)w->get_window_handle(w), flag);
            if (wasSimple != flag) (flag ? EnteredFullScreen : LeftFullScreen)?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Whether the window is in simple full screen. Electron's <c>win.isSimpleFullScreen()</c>.</summary>
    public bool IsSimpleFullScreen => OperatingSystem.IsMacOS()
        ? Query(w => OperatingSystem.IsMacOS() && IsMacSimpleFullScreen(w), false)
        : IsNativeFullScreen;

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool IsMacSimpleFullScreen(cef_window_t* window)
        => MacWindow.IsSimpleFullScreen((nint)window->get_window_handle(window));

    /// <summary>Keeps the window above others, or stops. Electron's <c>win.setAlwaysOnTop()</c>.</summary>
    public void SetAlwaysOnTop(bool flag) => Command(w => w->set_always_on_top(w, flag ? 1 : 0));

    /// <summary>Whether the window stays above others. Electron's <c>win.isAlwaysOnTop()</c>.</summary>
    public bool IsAlwaysOnTop => Query(w => w->is_always_on_top(w) != 0, false);

    /// <summary>Sets the title bar text. Electron's <c>win.setTitle()</c>.</summary>
    public void SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        Command(w =>
        {
            cef_string_utf16_t value = default;
            CefString.Set(&value, title);
            w->set_title(w, &value);
            CefString.Clear(&value);
        });
    }

    /// <summary>The title bar text. Electron's <c>win.getTitle()</c>.</summary>
    public string GetTitle() => Query(w => CefString.ReadUserFree(w->get_title(w)), string.Empty);

    /// <summary>Moves and resizes the window, in screen DIPs. Electron's <c>win.setBounds()</c>.</summary>
    public void SetBounds(Rectangle bounds) => Command(w =>
    {
        var rect = new cef_rect_t { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };
        w->@base.@base.set_bounds(&w->@base.@base, &rect);
    });

    /// <summary>The window's bounds in screen DIPs. Electron's <c>win.getBounds()</c>.</summary>
    public Rectangle GetBounds() => Query(w => ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base)), default);

    /// <summary>Resizes the window, keeping its position. Electron's <c>win.setSize()</c>.</summary>
    public void SetSize(int width, int height) => Command(w =>
    {
        var size = new cef_size_t { width = width, height = height };
        w->@base.@base.set_size(&w->@base.@base, &size);
    });

    /// <summary>Moves the window, keeping its size. Electron's <c>win.setPosition()</c>.</summary>
    public void SetPosition(int x, int y) => Command(w =>
    {
        var point = new cef_point_t { x = x, y = y };
        w->@base.@base.set_position(&w->@base.@base, &point);
    });

    /// <summary>Centres the window on its display. Electron's <c>win.center()</c>.</summary>
    public void Center() => Command(w =>
    {
        var size = w->@base.@base.get_size(&w->@base.@base);
        w->center_window(w, &size);
    });

    /// <summary>Closes the window as the user would: <see cref="Closing"/> and the page's beforeunload
    /// may still cancel it. Electron's <c>win.close()</c>.</summary>
    public void Close() => Command(w => w->close(w));

    internal static void FocusAll()
    {
        foreach (var window in s_open.ToArray()) window.Focus();
    }

    // Window calls are only valid on the UI thread and before the window is destroyed.
    private void Command(WindowAction action) => CefThread.RunOnUi(() =>
    {
        if (_window is not null) action(_window);
    });

    private T Query<T>(WindowQuery<T> query, T whenClosed)
        => CefThread.InvokeOnUi(() => _window is null ? whenClosed : query(_window));

    private delegate void WindowAction(cef_window_t* window);

    private delegate T WindowQuery<out T>(cef_window_t* window);

    private static Rectangle ToRectangle(cef_rect_t rect) => new(rect.x, rect.y, rect.width, rect.height);

    private void OnWindowCreated(cef_window_t* window)
    {
        NativeObject.AddRef(_browserView);
        window->@base.add_child_view(&window->@base, (cef_view_t*)_browserView);

        var size = new cef_size_t { width = _options.Width, height = _options.Height };
        if (_options is { X: { } x, Y: { } y })
        {
            var rect = new cef_rect_t { x = x, y = y, width = _options.Width, height = _options.Height };
            window->@base.@base.set_bounds(&window->@base.@base, &rect);
        }
        else
        {
            window->center_window(window, &size);
        }

        cef_string_utf16_t title = default;
        CefString.Set(&title, _options.Title);
        window->set_title(window, &title);
        CefString.Clear(&title);

        if (_options.AlwaysOnTop) window->set_always_on_top(window, 1);
        if (_options.Show) window->show(window);
        if (_options.FullScreen)
        {
            if (UsesSimpleFullScreen && OperatingSystem.IsMacOS()) MacWindow.SetSimpleFullScreen((nint)window->get_window_handle(window), true);
            else window->set_fullscreen(window, 1);
        }

        _lastBounds = ToRectangle(window->@base.@base.get_bounds_in_screen(&window->@base.@base));
        CefThread.PostUiDelayed(PollState, s_statePoll);
    }

    private void OnBrowserCreated(cef_browser_t* browser) => WebContents.OnBrowserCreated(browser);

    private bool OnClosing(cef_window_t* window)
    {
        var browser = WebContents.Browser;
        // CEF asks again once the browser is gone; by then the close was already approved.
        if (browser is null) return true;

        var args = new CancelEventArgs();
        Closing?.Invoke(this, args);
        if (args.Cancel) return false;

        // Lets the page run beforeunload first; the window closes once the browser has gone.
        var host = browser->get_host(browser);
        var canClose = host->try_close_browser(host) != 0;
        NativeObject.Release(host);
        return canClose;
    }

    private void OnActivationChanged(bool active) => (active ? Focused : Blurred)?.Invoke(this, EventArgs.Empty);

    // CEF reports no minimize/restore of its own, and on macOS minimizing changes no bounds, so the
    // state is also polled while the window is open.
    private static readonly TimeSpan s_statePoll = TimeSpan.FromMilliseconds(250);

    private void PollState()
    {
        if (_window is null) return;
        RaiseStateChanges();
        CefThread.PostUiDelayed(PollState, s_statePoll);
    }

    private void OnBoundsChanged(cef_rect_t rect)
    {
        var bounds = ToRectangle(rect);
        var previous = _lastBounds;
        _lastBounds = bounds;
        if (bounds.X != previous.X || bounds.Y != previous.Y) Moved?.Invoke(this, EventArgs.Empty);
        if (bounds.Width != previous.Width || bounds.Height != previous.Height) Resized?.Invoke(this, EventArgs.Empty);
        RaiseStateChanges();
    }

    private void RaiseStateChanges()
    {
        if (_window is null) return;
        var maximized = _window->is_maximized(_window) != 0;
        var minimized = _window->is_minimized(_window) != 0;
        if (maximized != _wasMaximized) (maximized ? Maximized : Unmaximized)?.Invoke(this, EventArgs.Empty);
        if (minimized != _wasMinimized) (minimized ? Minimized : Restored)?.Invoke(this, EventArgs.Empty);
        _wasMaximized = maximized;
        _wasMinimized = minimized;
    }

    private void OnFullscreenTransition(bool completed)
    {
        if (!completed || _window is null) return;
        (_window->is_fullscreen(_window) != 0 ? EnteredFullScreen : LeftFullScreen)?.Invoke(this, EventArgs.Empty);
    }

    private void OnBrowserDestroyed(cef_browser_t* browser) => WebContents.OnBrowserDestroyed();

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
