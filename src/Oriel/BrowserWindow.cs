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

    /// <summary>Whether the user can close the window; code can still call <see cref="BrowserWindow.Close"/>.
    /// Not implemented on Linux in Electron; honoured everywhere here.</summary>
    public bool Closable { get; init; } = true;

    /// <summary>Whether <see cref="Width"/> and <see cref="Height"/> size the page rather than the whole window.</summary>
    public bool UseContentSize { get; init; }

    /// <summary>Centres the window even when <see cref="X"/> and <see cref="Y"/> are set.</summary>
    public bool Center { get; init; }

    /// <summary>The colour shown before the page paints, as <c>#RRGGBB</c> or <c>#AARRGGBB</c>.
    /// Electron's <c>backgroundColor</c>.</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>A PNG or JPEG for the window and taskbar icon (Windows, Linux). Electron's <c>icon</c> as a path.</summary>
    public string? Icon { get; init; }

    /// <summary>The window and taskbar icon as an image; wins over <see cref="Icon"/>. Electron's <c>icon</c> as a
    /// <c>NativeImage</c>.</summary>
    public NativeImage? IconImage { get; init; }

    /// <summary>Whether the window has no taskbar button (Windows, Linux; macOS has no per-window taskbar button).
    /// Electron's <c>skipTaskbar</c>.</summary>
    public bool SkipTaskbar { get; init; }

    /// <summary>What Windows 11 22H2 and later draws behind the window; null leaves the system default. Windows only:
    /// ignored on macOS and Linux. Electron's <c>backgroundMaterial</c>.</summary>
    public BackgroundMaterial? BackgroundMaterial { get; init; }

    /// <summary>Whether the window stays above other windows.</summary>
    public bool AlwaysOnTop { get; init; }

    /// <summary>Whether the window starts full screen.</summary>
    public bool FullScreen { get; init; }

    /// <summary>On macOS, full screen fills the display without moving to a new Space, so other displays
    /// stay usable (native full screen blacks them out). Applies to <see cref="FullScreen"/>,
    /// <see cref="BrowserWindow.SetFullScreen"/>, the title bar's green button and Window ▸ Enter Full Screen.
    /// Ignored elsewhere. Electron's <c>simpleFullscreen</c>.</summary>
    public bool SimpleFullScreen { get; init; }

    /// <summary>The window this one belongs to: it stays above its parent, has no taskbar button of its own,
    /// and closes with it. Electron's <c>parent</c>.</summary>
    public BrowserWindow? Parent { get; init; }

    /// <summary>Disables <see cref="Parent"/> while this window is shown; requires a parent. Electron's <c>modal</c>.</summary>
    /// <remarks>On Linux the parent stays usable under window managers that ignore modal hints (openbox does);
    /// Electron disables the parent's input itself there, which Oriel does not yet.</remarks>
    public bool Modal { get; init; }

    /// <summary>Hides the menu bar until Alt is pressed (Windows, Linux). Electron's <c>autoHideMenuBar</c>.</summary>
    public bool AutoHideMenuBar { get; init; }

    /// <summary>Settings for the page. Electron's <c>webPreferences</c>.</summary>
    public WebPreferences WebPreferences { get; init; } = new();
}

/// <summary>Settings for a window's page; Electron's <c>WebPreferences</c>.</summary>
public sealed record WebPreferences
{
    /// <summary>A script run before the page's own, in an isolated world, with <c>oriel.ipcRenderer</c>
    /// and <c>oriel.contextBridge</c>. Electron's <c>preload</c>.</summary>
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

    /// <summary>Suppresses the page's <c>alert</c>, <c>confirm</c> and <c>prompt</c>: they return at once
    /// (<c>confirm</c> false, <c>prompt</c> null). Electron's <c>disableDialogs</c>.</summary>
    public bool DisableDialogs { get; init; }

    /// <summary>The session the page uses; overrides <see cref="Partition"/>. Null uses <see cref="Partition"/>.
    /// Electron's <c>session</c>.</summary>
    public Session? Session { get; init; }

    /// <summary>The page's session by name, as <see cref="Oriel.Session.FromPartition"/>: <c>persist:</c><i>name</i>
    /// is kept on disk, any other name in memory. Null or empty uses <see cref="Oriel.Session.DefaultSession"/>.
    /// Electron's <c>partition</c>.</summary>
    public string? Partition { get; init; }
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
/// Create after <see cref="OrielApp.Ready"/>. Members may be called from any thread: the constructor and
/// queries wait for the UI thread, commands are queued to it. Events are raised on the UI thread.
/// Electron's event names collide with its method names in C# (<c>focus()</c> / <c>'focus'</c>), so
/// events use .NET's past tense: <see cref="Focused"/>, <see cref="Moved"/>, <see cref="Maximized"/>, ...
/// </remarks>
public sealed unsafe class BrowserWindow
{
    // Native delegates call back into these objects, so open windows must stay reachable.
    private static readonly List<BrowserWindow> s_open = [];
    private static readonly Lock s_openGate = new();
    private static int s_nextId;

    private readonly BrowserWindowOptions _options;
    private readonly ClientHandler _client;
    private readonly BrowserViewDelegate _viewDelegate;
    private readonly WindowDelegate _windowDelegate;
    // Windows and Linux only; on macOS the application menu is the menu bar.
    private readonly WindowMenuBar? _menuBar;
    private readonly Action<Menu?> _applicationMenuChanged;
    private Menu? _ownMenu;
    private bool _hasOwnMenu;

    private cef_browser_view_t* _browserView;
    private cef_window_t* _window;

    // Last observed state, to turn CEF's bounds callbacks into Electron's specific events.
    private Rectangle _lastBounds;
    private bool _wasMaximized;
    private bool _wasMinimized;

    private bool _closable;
    private bool _skipTaskbar;
    private readonly NativeImage? _icon;
    private bool _thumbarAdded;
    private readonly nint[] _thumbarIcons = new nint[Thumbar.MaxButtons];
    private readonly Action?[] _thumbarClicks = new Action?[Thumbar.MaxButtons];
    // macOS: the Dock bounce FlashFrame started, to cancel.
    private nint _attentionRequest;
    // Set by Close()/Destroy(): a close from code goes through even when the user may not close.
    private bool _closeFromCode;
    private bool _readyToShowRaised;
    private string? _backgroundColor;
    // macOS: the NSWindow whose full-screen toggle routes to simple full screen, or 0.
    private nint _routedNSWindow;
    // Touched only on the UI thread.
    private readonly List<BrowserWindow> _children = [];
    // Popups this window's page opened, closed with it unless they outlive it; UI thread only.
    private readonly List<BrowserWindow> _popups = [];
    // Popups allowed by on_before_popup whose view CEF has not created yet, by the client CEF will use.
    private static readonly Dictionary<nint, (BrowserWindow Window, int PopupId, WindowOpenDetails Details)> s_pendingPopups = [];
    private BrowserWindow? _opener;
    private bool _outlivesOpener;

    /// <summary>Creates the window, shown unless <see cref="BrowserWindowOptions.Show"/> is false.</summary>
    /// <exception cref="InvalidOperationException">The application is not ready yet, or the parent is closed.</exception>
    /// <exception cref="ArgumentException"><see cref="BrowserWindowOptions.Modal"/> without a parent.</exception>
    /// <exception cref="NotSupportedException">An option CEF cannot honour, such as <see cref="WebPreferences.MinimumFontSize"/>.</exception>
    public BrowserWindow(BrowserWindowOptions? options = null) : this(options, opener: null)
    {
    }

    // A popup's window waits for CEF to create its browser view (AdoptPopupView) instead of creating one.
    private BrowserWindow(BrowserWindowOptions? options, BrowserWindow? opener)
    {
        if (!OrielApp.Current.IsReady) throw new InvalidOperationException("Create windows after OrielApp.Ready.");

        _options = options ?? new BrowserWindowOptions();
        if (_options.Modal && _options.Parent is null) throw new ArgumentException("A modal window needs a Parent.", nameof(options));
        // Chrome-style browsers take these from the profile's preferences, over the browser settings.
        if (_options.WebPreferences.MinimumFontSize is not null) throw new NotSupportedException("MinimumFontSize is not supported: CEF applies the profile's value to every window.");
        if (_options.WebPreferences.DefaultEncoding is not null) throw new NotSupportedException("DefaultEncoding is not supported: CEF applies the profile's value to every window.");
        if (_options.WebPreferences.ZoomFactor <= 0) throw new ArgumentOutOfRangeException(nameof(options), "ZoomFactor must be greater than 0.");

        Id = Interlocked.Increment(ref s_nextId);
        _closable = _options.Closable;
        _skipTaskbar = _options.SkipTaskbar;
        _icon = _options.IconImage ?? (_options.Icon is { } iconPath ? NativeImage.CreateFromBuffer(File.ReadAllBytes(iconPath)) : null);
        _backgroundColor = _options.BackgroundColor;
        var session = _options.WebPreferences.Session ?? Session.FromPartition(_options.WebPreferences.Partition ?? "");
        // CEF gives a popup its opener's request context, whatever the options say.
        if (opener is not null && session != opener.WebContents.Session)
            throw new InvalidOperationException("A window a page opens must use its opener's session.");
        WebContents = new WebContents(_options.WebPreferences.Preload is { } preload ? File.ReadAllText(preload) : null, _options.WebPreferences, session);
        WebContents.PageTitleUpdated += (_, e) => PageTitleUpdated?.Invoke(this, e);
        WebContents.TitleApplied = SetTitle;
        WebContents.PageMouseDown = () => _menuBar?.OnPageMouseDown();
        WebContents.DidFinishLoad += (_, _) =>
        {
            if (_readyToShowRaised) return;
            _readyToShowRaised = true;
            ReadyToShow?.Invoke(this, EventArgs.Empty);
        };
        _client = new ClientHandler(WebContents.Display, WebContents.Load, WebContents.JsDialog, WebContents.Request, WebContents.Command,
            WebContents.ContextMenuRequests, new KeyboardHandler(keyEvent => _menuBar?.OnKeyEvent(keyEvent) ?? false),
            new LifeSpanHandler { BeforePopup = OnBeforePopup, PopupAborted = OnPopupAborted })
        {
            Permission = WebContents.Permissions,
        };
        if (!OperatingSystem.IsMacOS())
        {
            _menuBar = new WindowMenuBar(this) { AutoHide = _options.AutoHideMenuBar };
            _menuBar.SetShown(!_options.AutoHideMenuBar);
        }
        _applicationMenuChanged = menu => Command(_ =>
        {
            if (!_hasOwnMenu) _menuBar?.SetMenu(menu);
        });
        _viewDelegate = new BrowserViewDelegate
        {
            BrowserCreated = OnBrowserCreated,
            BrowserDestroyed = OnBrowserDestroyed,
            PopupDelegate = client => s_pendingPopups.TryGetValue(client, out var pending) ? (nint)pending.Window._viewDelegate.Native : 0,
            PopupViewCreated = OnPopupViewCreated,
        };
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
            Parent = _options.Parent is { } parent ? () => (nint)parent._window : null,
            Modal = _options.Modal,
            Accelerator = commandId => _menuBar?.OnAccelerator(commandId) ?? false,
            KeyEvent = keyEvent => _menuBar?.OnKeyEvent(keyEvent) ?? false,
        };

        if (opener is not null)
        {
            _opener = opener;
            return;
        }
        CefThread.InvokeOnUi(() =>
        {
            if (_options.Parent is { _window: null }) throw new InvalidOperationException("The parent window is closed.");
            CreateNative();
            return true;
        });
    }

    private void CreateNative()
    {
        var settings = new cef_browser_settings_t
        {
            size = (nuint)sizeof(cef_browser_settings_t),
            background_color = _backgroundColor is null ? 0 : ParseColor(_backgroundColor),
        };
        ApplyWebPreferences(&settings, _options.WebPreferences);
        // A document must exist before DevTools answers commands, and the preload is registered
        // over DevTools before the real page loads.
        cef_string_utf16_t url = default;
        CefString.Set(&url, "about:blank");

        NativeObject.AddRef(_client.Native);
        NativeObject.AddRef(_viewDelegate.Native);
        // Null is the global context, which the default session wraps.
        var context = WebContents.Session.IsDefault ? null : WebContents.Session.AddRefNative();
        _browserView = CefNative.cef_browser_view_create(_client.Native, &url, &settings, null, context, _viewDelegate.Native);
        CefString.Clear(&url);
        ClearWebPreferenceStrings(&settings);
        OpenWindow();
    }

    private void OpenWindow()
    {
        lock (s_openGate) s_open.Add(this);
        Menu.ApplicationMenuChanged += _applicationMenuChanged;
        _options.Parent?._children.Add(this);
        NativeObject.AddRef(_windowDelegate.Native);
        _window = CefNative.cef_window_create_top_level(_windowDelegate.Native);
    }

    // UI thread, inside CEF's on_before_popup: asks the page's handler, then prepares the window CEF's popup will live in.
    private cef_client_t* OnBeforePopup(PopupRequest request, cef_popup_features_t* features, cef_browser_settings_t* settings)
    {
        var details = new WindowOpenDetails(request.Url, request.FrameName, FeaturesOf(features), DispositionOf(request.Disposition))
        {
            UserGesture = request.UserGesture,
        };
        var response = WebContents.DecideWindowOpen(details);
        if (!response.IsAllowed) return null;

        var parsed = PopupOptionsFrom(features, WebContents.Preferences);
        var options = response.OverrideBrowserWindowOptions ?? response.ConfigureBrowserWindowOptions?.Invoke(parsed) ?? parsed;
        BrowserWindow popup;
        try { popup = new BrowserWindow(options, opener: this) { _outlivesOpener = response.OutlivesOpener }; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[Oriel] Blocked a window the page opened: {e.Message}");
            return null;
        }
        settings->background_color = popup._backgroundColor is null ? settings->background_color : ParseColor(popup._backgroundColor);
        ApplyWebPreferences(settings, popup._options.WebPreferences);
        s_pendingPopups[(nint)popup._client.Native] = (popup, request.PopupId, details);
        return popup._client.Native;
    }

    private static void OnPopupAborted(int popupId)
    {
        foreach (var (client, pending) in s_pendingPopups.ToArray())
            if (pending.PopupId == popupId) s_pendingPopups.Remove(client);
    }

    // The popup's view arrives with the delegate PopupDelegate chose, which identifies the waiting window.
    private bool OnPopupViewCreated(nint popupView, nint popupDelegate)
    {
        foreach (var (client, pending) in s_pendingPopups.ToArray())
        {
            if ((nint)pending.Window._viewDelegate.Native != popupDelegate) continue;
            s_pendingPopups.Remove(client);
            var popup = pending.Window;
            NativeObject.AddRef((void*)popupView);
            popup._browserView = (cef_browser_view_t*)popupView;
            popup.WebContents.MarkOpenedByPage();
            if (!popup._outlivesOpener) _popups.Add(popup);
            popup.OpenWindow();
            WebContents.RaiseDidCreateWindow(new DidCreateWindowEventArgs(popup, pending.Details));
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

    private static void ApplyWebPreferences(cef_browser_settings_t* settings, WebPreferences preferences)
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
    private static void ClearWebPreferenceStrings(cef_browser_settings_t* settings)
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

    /// <summary>A number unique among this process's windows. Electron's <c>win.id</c>.</summary>
    public int Id { get; }

    /// <summary>All open windows. Electron's <c>BrowserWindow.getAllWindows()</c>.</summary>
    public static IReadOnlyList<BrowserWindow> GetAllWindows()
    {
        lock (s_openGate) return [.. s_open];
    }

    /// <summary>The focused window, or null. Electron's <c>BrowserWindow.getFocusedWindow()</c>.</summary>
    public static BrowserWindow? GetFocusedWindow() => GetAllWindows().FirstOrDefault(w => w.IsFocused);

    /// <summary>The window showing <paramref name="webContents"/>, or null. Electron's <c>BrowserWindow.fromWebContents()</c>.</summary>
    public static BrowserWindow? FromWebContents(WebContents webContents)
        => GetAllWindows().FirstOrDefault(w => ReferenceEquals(w.WebContents, webContents));

    /// <summary>The open window with <paramref name="id"/>, or null. Electron's <c>BrowserWindow.fromId()</c>.</summary>
    public static BrowserWindow? FromId(int id) => GetAllWindows().FirstOrDefault(w => w.Id == id);

    /// <summary>The window this one belongs to, or null. Electron's <c>win.getParentWindow()</c>.</summary>
    public BrowserWindow? GetParentWindow() => _options.Parent;

    /// <summary>The open windows that belong to this one. Electron's <c>win.getChildWindows()</c>.</summary>
    public IReadOnlyList<BrowserWindow> GetChildWindows() => CefThread.InvokeOnUi(() => (IReadOnlyList<BrowserWindow>)[.. _children]);

    /// <summary>Whether this window disables its parent while shown. Electron's <c>win.isModal()</c>.</summary>
    public bool IsModal => _options.Modal;

    /// <summary>Sets the window's menu bar, replacing the application menu for this window; null removes it
    /// (Windows, Linux). Electron's <c>win.setMenu()</c>.</summary>
    /// <remarks>On macOS the menu bar belongs to the application: use <see cref="Menu.SetApplicationMenu"/>.</remarks>
    public void SetMenu(Menu? menu) => CefThread.RunOnUi(() =>
    {
        _hasOwnMenu = true;
        _ownMenu = menu;
        if (_window is not null) _menuBar?.SetMenu(menu);
    });

    /// <summary>Removes the window's menu bar (Windows, Linux). Electron's <c>win.removeMenu()</c>.</summary>
    public void RemoveMenu() => SetMenu(null);

    /// <summary>Whether the menu bar stays hidden until Alt is pressed (Windows, Linux). Setting it shows or
    /// hides the bar to match. Electron's <c>win.autoHideMenuBar</c>.</summary>
    public bool AutoHideMenuBar
    {
        get => Query(_ => _menuBar?.AutoHide ?? false, false);
        set => Command(_ =>
        {
            if (_menuBar is null) return;
            _menuBar.AutoHide = value;
            _menuBar.SetShown(!value);
        });
    }

    /// <summary>Shows or hides the menu bar; with <see cref="AutoHideMenuBar"/>, Alt still toggles it (Windows,
    /// Linux). Electron's <c>win.setMenuBarVisibility()</c>.</summary>
    public void SetMenuBarVisibility(bool visible) => Command(_ => _menuBar?.SetShown(visible));

    /// <summary>Whether a menu bar is showing; always false on macOS. Electron's <c>win.isMenuBarVisible()</c>.</summary>
    public bool IsMenuBarVisible => Query(_ => _menuBar?.IsVisible ?? false, false);

    /// <summary>Shows <paramref name="menu"/> at a point in the page, by default at the pointer.</summary>
    internal void ShowPopup(Menu menu, int? x, int? y, Action? closed) => Command(w =>
    {
        var content = ContentBoundsOn(w);
        var at = x is { } ax && y is { } ay ? new Point(content.X + ax, content.Y + ay) : Screen.GetCursorScreenPoint();
        var screen = new cef_point_t { x = at.X, y = at.Y };
        var popupDelegate = new MenuModelDelegate { CommandExecuted = (id, flags) => menu.FindByCommandId(id)?.Activate(this, KeyboardEvent.FromCefFlags(flags)), Closed = closed };
        NativeObject.AddRef(popupDelegate.Native);
        var model = CefNative.cef_menu_model_create(popupDelegate.Native);
        MenuModels.Fill(model, menu);
        // The model reference passes to CEF, which keeps it, and through it the delegate, until the menu closes.
        w->show_menu(w, model, &screen, cef_menu_anchor_position_t.CEF_MENU_ANCHOR_TOPLEFT);
    });

    /// <summary>Closes a menu shown by <see cref="ShowPopup"/>.</summary>
    internal void ClosePopup()
    {
        // An open popup holds macOS in AppKit's menu loop, where a posted CEF task would wait for the menu to close.
        if (OperatingSystem.IsMacOS()) MacMenu.PerformOnMainThread(() => { if (_window is not null) _window->cancel_menu(_window); });
        else Command(w => w->cancel_menu(w));
    }

    /// <summary>Opens the menu bar's menu at <paramref name="index"/> as a click would; false if there is none.</summary>
    internal bool TriggerMenuBarItem(int index) => Query(_ => _menuBar?.TriggerMenu(index) ?? false, false);

    /// <summary>Whether a menu bar menu is open.</summary>
    internal bool IsMenuBarMenuOpen => Query(_ => _menuBar?.IsMenuOpen ?? false, false);

    /// <summary>Closes an open menu bar menu.</summary>
    internal void CancelMenuBarMenu() => Command(_ => _menuBar?.CancelMenu());

    /// <summary>The page set its title; cancel to keep the window's title. Electron's <c>page-title-updated</c>.</summary>
    public event EventHandler<PageTitleUpdatedEventArgs>? PageTitleUpdated;

    /// <summary>The page finished its first load, so the window can be shown without a blank flash.
    /// Electron's <c>ready-to-show</c>.</summary>
    public event EventHandler? ReadyToShow;

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
    /// <inheritdoc cref="Oriel.WebContents.LoadUrl" path="/param|/exception"/>
    public Task LoadUrl(string url, LoadUrlOptions? options = null) => WebContents.LoadUrl(url, options);

    /// <summary>Loads a local file in the window's page. Electron's <c>win.loadFile()</c>.</summary>
    public Task LoadFile(string path, IReadOnlyDictionary<string, string>? query = null, string? hash = null, string? search = null)
        => WebContents.LoadFile(path, query, hash, search);

    /// <summary>Reloads the window's page. Electron's <c>win.reload()</c>.</summary>
    public void Reload() => WebContents.Reload();

    /// <summary>Shows and focuses the window. Electron's <c>win.show()</c>.</summary>
    public void Show() => Command(w =>
    {
        var wasVisible = w->@base.@base.is_visible(&w->@base.@base) != 0;
        w->show(w);
        ApplySkipTaskbar(w);
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
    public void Focus() => CefThread.RunOnUi(() =>
    {
        // Windows disables a modal's parent, but SetForegroundWindow still activates a disabled window.
        var target = this;
        while (target._children.FirstOrDefault(c => c._options.Modal && c._window is not null && c._window->@base.@base.is_visible(&c._window->@base.@base) != 0) is { } modal)
            target = modal;
        if (target._window is not null) target._window->activate(target._window);
    });

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

    /// <summary>Whether the window is in native full screen; false in simple full screen, as Electron (which reads the
    /// window's full-screen style). Electron's <c>win.isFullScreen()</c>.</summary>
    public bool IsFullScreen => IsNativeFullScreen;

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

    /// <summary>The native window: the content <c>NSView</c> on macOS, the <c>HWND</c> on Windows, the X11
    /// <c>Window</c> on Linux; 0 once closed. Electron's <c>win.getNativeWindowHandle()</c>.</summary>
    public nint GetNativeWindowHandle() => Query(w => (nint)w->get_window_handle(w), 0);

    /// <summary>Keeps the window above others, or stops. Electron's <c>win.setAlwaysOnTop()</c>.</summary>
    /// <remarks>On macOS, <paramref name="level"/> (<c>normal</c>, <c>floating</c>, <c>torn-off-menu</c>, <c>modal-panel</c>,
    /// <c>main-menu</c>, <c>status</c>, <c>pop-up-menu</c>, <c>screen-saver</c>) plus <paramref name="relativeLevel"/> sets the
    /// window's level, as Electron; elsewhere they are ignored (DRIFT W29).</remarks>
    /// <exception cref="ArgumentException">An unknown level.</exception>
    public void SetAlwaysOnTop(bool flag, string level = "floating", int relativeLevel = 0)
    {
        var macLevel = MacWindowLevel(level) ?? throw new ArgumentException($"Unknown level '{level}'.", nameof(level));
        Command(w =>
        {
            var onTop = flag && level != "normal";
            w->set_always_on_top(w, onTop ? 1 : 0);
            if (OperatingSystem.IsMacOS()) MacWindow.SetLevel((nint)w->get_window_handle(w), onTop ? macLevel + relativeLevel : 0);
        });
    }

    /// <summary>Electron's level names as AppKit window levels (NSWindowLevel).</summary>
    internal static long? MacWindowLevel(string level) => level switch
    {
        "normal" => 0,
        "floating" or "torn-off-menu" => 3,
        "modal-panel" => 8,
        "main-menu" => 24,
        "status" => 25,
        "pop-up-menu" => 101,
        "screen-saver" => 1000,
        _ => null,
    };

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
    /// <remarks>On macOS, <paramref name="animate"/> uses AppKit's animation; elsewhere it is ignored, as in Electron.</remarks>
    public void SetBounds(Rectangle bounds, bool animate = false) => Command(w => ApplyOuterBounds(w, bounds, animate));

    private static void ApplyOuterBounds(cef_window_t* w, Rectangle bounds, bool animate)
    {
        if (animate && OperatingSystem.IsMacOS())
        {
            MacWindow.AnimateFrame((nint)w->get_window_handle(w), bounds.X, bounds.Y, bounds.Width, bounds.Height);
            return;
        }
        var rect = new cef_rect_t { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };
        w->@base.@base.set_bounds(&w->@base.@base, &rect);
    }

    private static Rectangle OuterBoundsOn(cef_window_t* w) => ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base));

    /// <summary>The window's bounds in screen DIPs. Electron's <c>win.getBounds()</c>.</summary>
    public Rectangle GetBounds() => Query(w => ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base)), default);

    /// <summary>Resizes the window, keeping its position. Electron's <c>win.setSize()</c>.</summary>
    /// <remarks>On macOS, <paramref name="animate"/> uses AppKit's animation; elsewhere it is ignored, as in Electron.</remarks>
    public void SetSize(int width, int height, bool animate = false) => Command(w =>
    {
        if (animate) ApplyOuterBounds(w, OuterBoundsOn(w) with { Width = width, Height = height }, animate);
        else
        {
            var size = new cef_size_t { width = width, height = height };
            w->@base.@base.set_size(&w->@base.@base, &size);
        }
    });

    /// <summary>Moves the window, keeping its size. Electron's <c>win.setPosition()</c>.</summary>
    /// <remarks>On macOS, <paramref name="animate"/> uses AppKit's animation; elsewhere it is ignored, as in Electron.</remarks>
    public void SetPosition(int x, int y, bool animate = false) => Command(w =>
    {
        if (animate) ApplyOuterBounds(w, OuterBoundsOn(w) with { X = x, Y = y }, animate);
        else
        {
            var point = new cef_point_t { x = x, y = y };
            w->@base.@base.set_position(&w->@base.@base, &point);
        }
    });

    /// <summary>Centres the window on its display. Electron's <c>win.center()</c>.</summary>
    public void Center() => Command(w =>
    {
        var size = w->@base.@base.get_size(&w->@base.@base);
        w->center_window(w, &size);
    });

    /// <summary>Closes the window as the user would: <see cref="Closing"/> and the page's beforeunload
    /// may still cancel it. Electron's <c>win.close()</c>.</summary>
    public void Close() => Command(w =>
    {
        _closeFromCode = true;
        w->close(w);
    });

    /// <summary>Closes the window at once: no <see cref="Closing"/>, no beforeunload. Electron's <c>win.destroy()</c>.</summary>
    // A forced close_browser destroys the browser before CEF asks can_close, so Closing is never raised.
    public void Destroy() => Command(w =>
    {
        var browser = WebContents.Browser;
        if (browser is not null)
        {
            var host = browser->get_host(browser);
            host->close_browser(host, 1);
            NativeObject.Release(host);
        }
        w->close(w);
    });

    /// <summary>Lets the user resize the window, or stops. Electron's <c>win.setResizable()</c>.</summary>
    public void SetResizable(bool flag) => Command(_ => _windowDelegate.Resizable = flag);

    /// <summary>Whether the user can resize the window. Electron's <c>win.isResizable()</c>.</summary>
    public bool IsResizable => Query(_ => _windowDelegate.Resizable, false);

    /// <summary>Lets the window be minimized, or stops. Electron's <c>win.setMinimizable()</c>.</summary>
    public void SetMinimizable(bool flag) => Command(_ => _windowDelegate.Minimizable = flag);

    /// <summary>Whether the window can be minimized. Electron's <c>win.isMinimizable()</c>.</summary>
    public bool IsMinimizable => Query(_ => _windowDelegate.Minimizable, false);

    /// <summary>Lets the window be maximized, or stops. Electron's <c>win.setMaximizable()</c>.</summary>
    public void SetMaximizable(bool flag) => Command(_ => _windowDelegate.Maximizable = flag);

    /// <summary>Whether the window can be maximized. Electron's <c>win.isMaximizable()</c>.</summary>
    public bool IsMaximizable => Query(_ => _windowDelegate.Maximizable, false);

    /// <summary>Lets the user close the window, or stops; code can still close it. Electron's <c>win.setClosable()</c>.</summary>
    public void SetClosable(bool flag) => Command(_ => _closable = flag);

    /// <summary>Whether the user can close the window. Electron's <c>win.isClosable()</c>.</summary>
    public bool IsClosable => Query(_ => _closable, false);

    /// <summary>The smallest size the user can resize to; 0 for no limit. Electron's <c>win.setMinimumSize()</c>.</summary>
    public void SetMinimumSize(int width, int height)
        => Command(_ => _windowDelegate.MinimumSize = new cef_size_t { width = width, height = height });

    /// <summary>The smallest size the user can resize to. Electron's <c>win.getMinimumSize()</c>.</summary>
    public (int Width, int Height) GetMinimumSize() => Query(_ => (_windowDelegate.MinimumSize.width, _windowDelegate.MinimumSize.height), (0, 0));

    /// <summary>The largest size the user can resize to; 0 for no limit. Electron's <c>win.setMaximumSize()</c>.</summary>
    public void SetMaximumSize(int width, int height)
        => Command(_ => _windowDelegate.MaximumSize = new cef_size_t { width = width, height = height });

    /// <summary>The largest size the user can resize to. Electron's <c>win.getMaximumSize()</c>.</summary>
    public (int Width, int Height) GetMaximumSize() => Query(_ => (_windowDelegate.MaximumSize.width, _windowDelegate.MaximumSize.height), (0, 0));

    /// <summary>The window's size. Electron's <c>win.getSize()</c>.</summary>
    public (int Width, int Height) GetSize() => GetBounds() is var b ? (b.Width, b.Height) : default;

    /// <summary>The window's position on screen. Electron's <c>win.getPosition()</c>.</summary>
    public (int X, int Y) GetPosition() => GetBounds() is var b ? (b.X, b.Y) : default;

    /// <summary>The page area's bounds on screen, excluding title bar and frame. Electron's <c>win.getContentBounds()</c>.</summary>
    public Rectangle GetContentBounds() => Query(ContentBoundsOn, default);

    /// <summary>Places the window so its page area has <paramref name="bounds"/>. Electron's <c>win.setContentBounds()</c>.</summary>
    /// <remarks>On macOS, <paramref name="animate"/> uses AppKit's animation; elsewhere it is ignored, as in Electron.</remarks>
    public void SetContentBounds(Rectangle bounds, bool animate = false) => Command(w => SetContentBoundsOn(w, bounds, animate));

    /// <summary>The page area's size. Electron's <c>win.getContentSize()</c>.</summary>
    public (int Width, int Height) GetContentSize() => GetContentBounds() is var b ? (b.Width, b.Height) : default;

    /// <summary>Resizes the window so its page area is <paramref name="width"/>×<paramref name="height"/>.
    /// Electron's <c>win.setContentSize()</c>.</summary>
    /// <remarks>On macOS, <paramref name="animate"/> uses AppKit's animation; elsewhere it is ignored, as in Electron.</remarks>
    public void SetContentSize(int width, int height, bool animate = false) => Command(w =>
    {
        var content = ContentBoundsOn(w);
        SetContentBoundsOn(w, content with { Width = width, Height = height }, animate);
    });

    /// <summary>Sets the colour behind the page. Electron's <c>win.setBackgroundColor()</c>.</summary>
    /// <exception cref="FormatException">The colour is not <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</exception>
    public void SetBackgroundColor(string color)
    {
        var argb = ParseColor(color);
        Command(w =>
        {
            _backgroundColor = color;
            w->@base.@base.set_background_color(&w->@base.@base, argb);
            if (_browserView is not null) _browserView->@base.set_background_color(&_browserView->@base, argb);
        });
    }

    /// <summary>The colour behind the page, as set. Electron's <c>win.getBackgroundColor()</c>.</summary>
    public string? GetBackgroundColor() => Query(_ => _backgroundColor, null);

    /// <summary>Sets the window and taskbar icon from a PNG or JPEG file (Windows, Linux). Electron's <c>win.setIcon()</c>.</summary>
    public void SetIcon(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        SetIcon(NativeImage.CreateFromBuffer(File.ReadAllBytes(path)));
    }

    /// <summary>Sets the window and taskbar icon, every scale factor of it (Windows, Linux). Electron's <c>win.setIcon()</c>.</summary>
    public void SetIcon(NativeImage icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        Command(w => SetIconOn(w, icon));
    }

    private static void SetIconOn(cef_window_t* w, NativeImage icon)
    {
        var image = icon.ToCefImage();
        NativeObject.AddRef(image);
        w->set_window_icon(w, image);
        NativeObject.AddRef(image);
        w->set_window_app_icon(w, image);
        NativeObject.Release(image);
    }

    /// <summary>Shows progress on the taskbar button, from 0 to 1; below 0 removes it and above 1 shows it as
    /// indeterminate. Electron's <c>win.setProgressBar()</c>.</summary>
    /// <remarks>macOS: on the Dock icon, which all windows share; error and paused show as the plain bar.
    /// Linux: not shown yet (desktop-specific).</remarks>
    public void SetProgressBar(double progress, ProgressBarOptions? options = null)
    {
        var (state, percent) = ProgressBar.Map(progress, options?.Mode ?? ProgressBarMode.Normal);
        Command(w =>
        {
            if (OperatingSystem.IsWindows() && !WindowsTaskbar.SetProgress(Hwnd(w), state, percent))
                Console.Error.WriteLine("[Oriel] The taskbar did not take the progress bar.");
            // One Dock tile for the whole application; error and paused show as the plain bar.
            if (OperatingSystem.IsMacOS())
                MacDock.SetProgress(state == TaskbarProgressState.None ? null : percent / 100.0, state == TaskbarProgressState.Indeterminate);
        });
    }

    /// <summary>Flashes the title bar and taskbar button to get the user's attention, or stops. Electron's <c>win.flashFrame()</c>.</summary>
    /// <remarks>macOS: bounces the Dock icon once, only while the application is not active. Linux: sets the
    /// window's urgency hint, which the window manager shows its own way.</remarks>
    public void FlashFrame(bool flag) => Command(w =>
    {
        if (OperatingSystem.IsWindows()) WindowsTaskbar.FlashFrame(Hwnd(w), flag);
        if (OperatingSystem.IsLinux() && !X11Window.SetUrgency(Hwnd(w), flag)) Console.Error.WriteLine("[Oriel] No X display for FlashFrame.");
        if (OperatingSystem.IsMacOS())
        {
            MacDock.CancelAttention(_attentionRequest);
            _attentionRequest = flag ? MacDock.RequestAttention() : 0;
        }
    });

    /// <summary>Removes the window's taskbar button, or restores it (Windows, Linux; macOS has none per window).
    /// Electron's <c>win.setSkipTaskbar()</c>.</summary>
    public void SetSkipTaskbar(bool skip) => Command(w =>
    {
        var changed = _skipTaskbar != skip;
        _skipTaskbar = skip;
        if (changed || skip) ApplySkipTaskbar(w, restore: !skip);
    });

    /// <summary>Shows a small image over the taskbar icon, with <paramref name="description"/> for screen readers; null
    /// removes it. Windows only for now: does nothing on macOS and Linux. Electron's <c>win.setOverlayIcon()</c>.</summary>
    public void SetOverlayIcon(NativeImage? overlay, string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        Command(w =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var hwnd = Hwnd(w);
            var done = overlay is null ? WindowsTaskbar.ClearOverlayIcon(hwnd) : SetOverlayIconOn(hwnd, overlay, description);
            if (!done) Console.Error.WriteLine("[Oriel] The taskbar did not take the overlay icon.");
        });
    }

    /// <summary>Sets the buttons in the window's taskbar thumbnail, at most seven; an empty list removes them. False if
    /// there are more than seven, the shell refused, or not on Windows. Electron's <c>win.setThumbarButtons()</c>.</summary>
    /// <remarks>Call it once the window is shown: the shell adds the toolbar to an existing taskbar button.</remarks>
    public bool SetThumbarButtons(IReadOnlyList<ThumbarButton> buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        var plan = Thumbar.Plan(buttons);
        return plan is not null && Query(w => OperatingSystem.IsWindows() && SetThumbarButtonsOn(w, plan), false);
    }

    /// <summary>Sets the window's App User Model ID and relaunch details, which decide how the taskbar groups and pins
    /// it; empty options are left as they are. Windows only: does nothing on macOS and Linux.
    /// Electron's <c>win.setAppDetails()</c>.</summary>
    public void SetAppDetails(AppDetailsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var values = AppDetails.Plan(options);
        Command(w =>
        {
            if (OperatingSystem.IsWindows() && values.Count > 0 && !WindowsWindow.SetAppUserModel(Hwnd(w), values))
                Console.Error.WriteLine("[Oriel] The window's property store did not take the app details.");
        });
    }

    /// <summary>Shows only <paramref name="region"/> of the window (DIPs, from the client area's top left) in its
    /// taskbar thumbnail; an empty region shows the whole window. Windows only: does nothing on macOS and Linux.
    /// Electron's <c>win.setThumbnailClip()</c>.</summary>
    public void SetThumbnailClip(Rectangle region) => Command(w =>
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = Hwnd(w);
        WindowsTaskbar.SetThumbnailClip(hwnd, AppDetails.ClipToPixels(region, WindowsWindow.ScaleFactor(hwnd)), done =>
        {
            if (!done) Console.Error.WriteLine("[Oriel] The taskbar did not take the thumbnail clip.");
        });
    });

    /// <summary>Sets the tooltip shown over the window's taskbar thumbnail; empty restores the title.
    /// Windows only: does nothing on macOS and Linux. Electron's <c>win.setThumbnailToolTip()</c>.</summary>
    public void SetThumbnailToolTip(string toolTip)
    {
        ArgumentNullException.ThrowIfNull(toolTip);
        Command(w =>
        {
            if (OperatingSystem.IsWindows() && !WindowsTaskbar.SetThumbnailToolTip(Hwnd(w), toolTip))
                Console.Error.WriteLine("[Oriel] The taskbar did not take the thumbnail tooltip.");
        });
    }

    /// <summary>Sets what Windows 11 22H2 and later draws behind the window. Windows only: does nothing on macOS and
    /// Linux. Electron's <c>win.setBackgroundMaterial()</c>.</summary>
    /// <remarks>DWM holds the material, but CEF paints the whole window opaque, title bar included, so it does not
    /// show yet, even under a transparent page and <see cref="BrowserWindowOptions.BackgroundColor"/>.</remarks>
    public void SetBackgroundMaterial(BackgroundMaterial material) => Command(w => ApplyBackgroundMaterial(w, material));

    private static void ApplyBackgroundMaterial(cef_window_t* w, BackgroundMaterial material)
    {
        if (OperatingSystem.IsWindows() && !WindowsWindow.SetBackdrop(Hwnd(w), BackgroundMaterials.ToBackdrop(material)))
            Console.Error.WriteLine("[Oriel] DWM did not take the background material (it needs Windows 11 22H2).");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool SetThumbarButtonsOn(cef_window_t* w, (int Slot, ThumbarButton? Button, uint Flags)[] plan)
    {
        var hwnd = Hwnd(w);
        if (!WindowsSubclass.RouteThumbButtonClicks(hwnd, slot => _thumbarClicks[slot]?.Invoke())) return false;
        var icons = plan.Select(s => s.Button is { } b ? StraightIcon(b.Icon) : 0).ToArray();
        var native = plan.Select(s => new ThumbButton((uint)s.Slot, icons[s.Slot], s.Button?.Tooltip, s.Flags)).ToArray();
        if (!WindowsTaskbar.SetThumbButtons(hwnd, native, add: !_thumbarAdded))
        {
            foreach (var icon in icons) WindowsTaskbar.DestroyIconHandle(icon);
            return false;
        }
        _thumbarAdded = true;
        for (var i = 0; i < Thumbar.MaxButtons; i++)
        {
            WindowsTaskbar.DestroyIconHandle(_thumbarIcons[i]);
            _thumbarIcons[i] = icons[i];
            _thumbarClicks[i] = plan[i].Button?.Click;
        }
        return true;
    }

    // Electron takes the 1x representation; icon DIBs take straight alpha.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static nint StraightIcon(NativeImage image)
    {
        if (image.IsEmpty()) return 0;
        var bitmap = ImagePixels.Unpremultiply(image.BitmapAt(1.0));
        return WindowsTaskbar.CreateIconHandle(bitmap.Bgra, bitmap.Width, bitmap.Height);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool SetOverlayIconOn(nint hwnd, NativeImage overlay, string description)
    {
        if (overlay.IsEmpty()) return false;
        var bitmap = ImagePixels.Unpremultiply(overlay.BitmapAt(1.0));
        return WindowsTaskbar.SetOverlayIcon(hwnd, bitmap.Bgra, bitmap.Width, bitmap.Height, description);
    }

    // The shell creates the button on the first show, whether at creation or from Show(); later shows keep it removed.
    private void ApplySkipTaskbar(cef_window_t* w, bool restore = false)
    {
        if (OperatingSystem.IsWindows() && (_skipTaskbar || restore) && !WindowsTaskbar.SetSkipTaskbar(Hwnd(w), _skipTaskbar))
            Console.Error.WriteLine("[Oriel] The taskbar did not take SkipTaskbar.");
        if (OperatingSystem.IsLinux() && (_skipTaskbar || restore) && !X11Window.SetSkipTaskbar(Hwnd(w), _skipTaskbar))
            Console.Error.WriteLine("[Oriel] No X display for SkipTaskbar.");
    }

    private static nint Hwnd(cef_window_t* w) => (nint)w->get_window_handle(w);

    // The page area: the client area below the menu bar.
    private Rectangle ContentBoundsOn(cef_window_t* w)
    {
        var client = ToRectangle(w->get_client_area_bounds_in_screen(w));
        var bar = _menuBar?.Height ?? 0;
        return client with { Y = client.Y + bar, Height = client.Height - bar };
    }

    private void SetContentBoundsOn(cef_window_t* w, Rectangle content, bool animate = false)
    {
        // The frame is whatever the window has beyond its page area; keep it the same around the new content.
        var outer = ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base));
        var inner = ContentBoundsOn(w);
        var rect = new cef_rect_t
        {
            x = content.X - (inner.X - outer.X),
            y = content.Y - (inner.Y - outer.Y),
            width = content.Width + (outer.Width - inner.Width),
            height = content.Height + (outer.Height - inner.Height),
        };
        ApplyOuterBounds(w, ToRectangle(rect), animate);
    }

    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c> into CEF's ARGB.</summary>
    /// <exception cref="FormatException">Not one of those forms.</exception>
    internal static uint ParseColor(string color)
    {
        ArgumentNullException.ThrowIfNull(color);
        var hex = color.StartsWith('#') ? color[1..] : color;
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"'{color}' is not a #RRGGBB or #AARRGGBB colour.");
        return hex.Length == 6 ? 0xFF000000 | value : value;
    }

    internal static void FocusAll()
    {
        foreach (var window in GetAllWindows()) window.Focus();
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
        if (_menuBar is not null)
        {
            _menuBar.Attach(window, _browserView);
            _menuBar.SetMenu(_hasOwnMenu ? _ownMenu : Menu.GetApplicationMenu());
        }
        else
        {
            NativeObject.AddRef(_browserView);
            window->@base.add_child_view(&window->@base, (cef_view_t*)_browserView);
        }

        var size = new cef_size_t { width = _options.Width, height = _options.Height };
        if (_options is { X: { } x, Y: { } y, Center: false })
        {
            var rect = new cef_rect_t { x = x, y = y, width = _options.Width, height = _options.Height };
            window->@base.@base.set_bounds(&window->@base.@base, &rect);
        }
        else
        {
            window->center_window(window, &size);
            // macOS sizes the page area here, not the window: the window would come out a title bar taller.
            var centred = ToRectangle(window->@base.@base.get_bounds_in_screen(&window->@base.@base));
            if (centred.Width != _options.Width || centred.Height != _options.Height)
            {
                var exact = new cef_rect_t
                {
                    x = centred.X + (centred.Width - _options.Width) / 2,
                    y = centred.Y + (centred.Height - _options.Height) / 2,
                    width = _options.Width,
                    height = _options.Height,
                };
                window->@base.@base.set_bounds(&window->@base.@base, &exact);
            }
        }

        cef_string_utf16_t title = default;
        CefString.Set(&title, _options.Title);
        window->set_title(window, &title);
        CefString.Clear(&title);

        if (_options.UseContentSize)
        {
            var content = ContentBoundsOn(window);
            SetContentBoundsOn(window, content with { Width = _options.Width, Height = _options.Height });
        }
        if (_backgroundColor is not null)
        {
            var argb = ParseColor(_backgroundColor);
            window->@base.@base.set_background_color(&window->@base.@base, argb);
            _browserView->@base.set_background_color(&_browserView->@base, argb);
        }
        if (_options.BackgroundMaterial is { } material) ApplyBackgroundMaterial(window, material);
        if (_icon is not null) SetIconOn(window, _icon);

        if (UsesSimpleFullScreen && OperatingSystem.IsMacOS())
            _routedNSWindow = MacWindow.RouteFullScreenToggle((nint)window->get_window_handle(window), () => SetSimpleFullScreen(!IsSimpleFullScreen));
        if (_options.AlwaysOnTop) window->set_always_on_top(window, 1);
        if (_options.Show)
        {
            window->show(window);
            ApplySkipTaskbar(window);
        }
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

        var fromCode = _closeFromCode;
        _closeFromCode = false;
        if (!_closable && !fromCode)
        {
            OrielApp.Current.OnCloseCancelled();
            return false;
        }

        var args = new CancelEventArgs();
        Closing?.Invoke(this, args);
        if (args.Cancel)
        {
            OrielApp.Current.OnCloseCancelled();
            return false;
        }

        // Lets the page run beforeunload first; the window closes once the browser has gone.
        var host = browser->get_host(browser);
        var canClose = host->try_close_browser(host) != 0;
        NativeObject.Release(host);
        return canClose;
    }

    private void OnActivationChanged(bool active)
    {
        if (!active) _menuBar?.OnWindowBlurred();
        (active ? Focused : Blurred)?.Invoke(this, EventArgs.Empty);
    }

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
        if (_routedNSWindow != 0 && OperatingSystem.IsMacOS()) MacWindow.UnrouteFullScreenToggle(_routedNSWindow);
        Menu.ApplicationMenuChanged -= _applicationMenuChanged;
        _menuBar?.Detach();
        if (OperatingSystem.IsWindows())
            foreach (var icon in _thumbarIcons) WindowsTaskbar.DestroyIconHandle(icon);
        NativeObject.Release(_browserView);
        NativeObject.Release(_window);
        _browserView = null;
        _window = null;

        lock (s_openGate) s_open.Remove(this);
        _options.Parent?._children.Remove(this);
        // macOS closes child windows with their parent; Linux does not. Destroy, as Windows does with owned
        // windows: a child that vetoed would outlive its parent.
        foreach (var child in _children.ToArray()) child.Destroy();
        // As Electron: windows the page opened close with it unless they asked to outlive it.
        foreach (var popup in _popups.ToArray()) popup.Destroy();
        _opener?._popups.Remove(this);
        Closed?.Invoke(this, EventArgs.Empty);
        if (s_open.Count == 0) OrielApp.Current.OnAllWindowsClosed();
    }
}
