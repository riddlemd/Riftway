using System.ComponentModel;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A rectangle in screen device-independent pixels; Electron's <c>Rectangle</c>.</summary>
public readonly record struct Rectangle(int X, int Y, int Width, int Height);

/// <summary>Arguments for <see cref="BaseWindow.AlwaysOnTopChanged"/>.</summary>
public sealed class AlwaysOnTopChangedEventArgs(bool isAlwaysOnTop) : EventArgs
{
    /// <summary>Whether the window now stays above others.</summary>
    public bool IsAlwaysOnTop { get; } = isAlwaysOnTop;
}

/// <summary>Options for a new <see cref="BaseWindow"/>; Electron's <c>BaseWindowConstructorOptions</c>.</summary>
public partial record BaseWindowOptions
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

    /// <summary>Whether the window appears when created; otherwise call <see cref="BaseWindow.Show"/>.</summary>
    public bool Show { get; init; } = true;

    /// <summary>Whether the window has a title bar and border. Electron's <c>frame</c>.</summary>
    public bool Frame { get; init; } = true;

    /// <summary>Whether the user can resize the window.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>Whether the window can be minimized.</summary>
    public bool Minimizable { get; init; } = true;

    /// <summary>Whether the window can be maximized.</summary>
    public bool Maximizable { get; init; } = true;

    /// <summary>Whether the user can close the window; code can still call <see cref="BaseWindow.Close"/>.
    /// Not implemented on Linux in Electron; honoured everywhere here.</summary>
    public bool Closable { get; init; } = true;

    /// <summary>Whether <see cref="Width"/> and <see cref="Height"/> size the page rather than the whole window.</summary>
    public bool UseContentSize { get; init; }

    /// <summary>Centres the window even when <see cref="X"/> and <see cref="Y"/> are set.</summary>
    public bool Center { get; init; }

    /// <summary>The colour behind the page, as <c>#RRGGBB</c> or <c>#AARRGGBB</c>; the alpha shows the desktop through
    /// on a <see cref="Transparent"/> window. Electron's <c>backgroundColor</c>.</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>Makes the window see-through wherever the page and <see cref="BackgroundColor"/> are. Electron's
    /// <c>transparent</c>.</summary>
    /// <remarks>Windows: as in Electron, such a window has no title bar or sizing border, cannot be resized by the user
    /// or maximized from the system menu, and <see cref="BaseWindow.Maximize"/> fills the work area instead.
    /// macOS and Linux: the window stays resizable and maximizable. Linux needs a libcef built with Oriel's CEF patches
    /// (<c>eng/cef</c>) and, as in Electron, a running compositing manager, without which the window is opaque; with
    /// stock CEF the option is ignored there (DRIFT W24).</remarks>
    public bool Transparent { get; init; }

    /// <summary>Linux: asks GTK window managers for the dark variant of the window's frame. Electron's <c>darkTheme</c>.</summary>
    public bool DarkTheme { get; init; }

    /// <summary>A PNG or JPEG for the window and taskbar icon (Windows, Linux). Electron's <c>icon</c> as a path.</summary>
    public string? Icon { get; init; }

    /// <summary>The window and taskbar icon as an image; wins over <see cref="Icon"/>. Electron's <c>icon</c> as a
    /// <c>NativeImage</c>.</summary>
    public NativeImage? IconImage { get; init; }

    /// <summary>Whether the window has no taskbar button (Windows, Linux; macOS has no per-window taskbar button).
    /// Electron's <c>skipTaskbar</c>.</summary>
    public bool SkipTaskbar { get; init; }

    /// <summary>The window's opacity, 0 to 1. Windows only for now: stored but not shown on macOS and Linux.
    /// Electron's <c>opacity</c>.</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>Whether the window has a shadow. Stored only: on Windows, as in Electron, it changes nothing, and macOS and
    /// Linux are not done yet. Electron's <c>hasShadow</c>.</summary>
    public bool HasShadow { get; init; } = true;

    /// <summary>Opens the window in kiosk mode: full screen. Electron's <c>kiosk</c>.</summary>
    public bool Kiosk { get; init; }

    /// <summary>How the title bar looks; anything but <see cref="Oriel.TitleBarStyle.Default"/> hides it on Windows and
    /// Linux. Electron's <c>titleBarStyle</c>.</summary>
    public TitleBarStyle TitleBarStyle { get; init; }

    /// <summary>With a hidden title bar, draws the window's minimize, maximize and close buttons over the page's top right.
    /// Windows only for now. Electron's <c>titleBarOverlay</c>.</summary>
    public TitleBarOverlayOptions? TitleBarOverlay { get; init; }

    /// <summary>What Windows 11 22H2 and later draws behind the window; null leaves the system default. Unless
    /// <see cref="BackgroundColor"/> is set, any value but <see cref="Oriel.BackgroundMaterial.None"/> makes the page
    /// background transparent so the material shows. Windows only: ignored on macOS and Linux. Electron's
    /// <c>backgroundMaterial</c>.</summary>
    public BackgroundMaterial? BackgroundMaterial { get; init; }

    /// <summary>Windows: whether a frameless window keeps <c>WS_THICKFRAME</c> and <c>WS_CAPTION</c>, which give it a
    /// shadow, the minimize and maximize animations and resizing from its edges; false also squares its corners. On a
    /// framed window false removes only <c>WS_THICKFRAME</c>, and the window can no longer be resized. Ignored on macOS
    /// and Linux. Electron's <c>thickFrame</c>.</summary>
    public bool ThickFrame { get; init; } = true;

    /// <summary>Whether a frameless window has rounded corners: Windows 11 only, through DWM, and only with
    /// <see cref="ThickFrame"/>. Not yet applied on macOS and Linux. Electron's <c>roundedCorners</c>.</summary>
    public bool RoundedCorners { get; init; } = true;

    /// <summary>The window's border (and, when framed, caption) colour on Windows 11; null follows the system
    /// setting, which shows the system accent colour only on the active window when "Show accent colour on title bars"
    /// is on. A colour string Electron cannot parse is ignored. Ignored on macOS and Linux. Electron's
    /// <c>accentColor</c>.</summary>
    public AccentColor? AccentColor { get; init; }

    /// <summary>Whether the window stays above other windows.</summary>
    public bool AlwaysOnTop { get; init; }

    /// <summary>Whether the window starts full screen.</summary>
    public bool FullScreen { get; init; }

    /// <summary>On macOS, full screen fills the display without moving to a new Space, so other displays
    /// stay usable (native full screen blacks them out). Applies to <see cref="FullScreen"/>,
    /// <see cref="BaseWindow.SetFullScreen"/>, the title bar's green button and Window ▸ Enter Full Screen.
    /// Ignored elsewhere. Electron's <c>simpleFullscreen</c>.</summary>
    public bool SimpleFullScreen { get; init; }

    /// <summary>Leaves the window out of Mission Control (macOS). Electron's <c>hiddenInMissionControl</c>.</summary>
    public bool HiddenInMissionControl { get; init; }

    /// <summary>The window this one belongs to: it stays above its parent, has no taskbar button of its own,
    /// and closes with it. Electron's <c>parent</c>.</summary>
    public BaseWindow? Parent { get; init; }

    /// <summary>Disables <see cref="Parent"/> while this window is shown; requires a parent. Electron's <c>modal</c>.</summary>
    /// <remarks>On Linux Oriel disables the parent's input itself, as Electron does, whatever the window manager makes
    /// of the modal hint; the pointer crossing into or out of the parent still reaches its page (drift W5).</remarks>
    public bool Modal { get; init; }

    /// <summary>Hides the menu bar until Alt is pressed (Windows, Linux). Electron's <c>autoHideMenuBar</c>.</summary>
    public bool AutoHideMenuBar { get; init; }
}

/// <summary>A top-level window; Electron's <c>BaseWindow</c>.</summary>
/// <remarks>
/// Create after <see cref="OrielApp.Ready"/>. Members may be called from any thread: the constructor and
/// queries wait for the UI thread, commands are queued to it. Events are raised on the UI thread.
/// Electron's event names collide with its method names in C# (<c>focus()</c> / <c>'focus'</c>), so
/// events use .NET's past tense: <see cref="Focused"/>, <see cref="Moved"/>, <see cref="Maximized"/>, ...
/// </remarks>
public unsafe partial class BaseWindow
{
    // Native delegates call back into these objects, so open windows must stay reachable.
    private protected static readonly List<BaseWindow> s_open = [];
    private protected static readonly Lock s_openGate = new();
    private static int s_nextId;

    private protected readonly BaseWindowOptions _options;
    private readonly WindowDelegate _windowDelegate;
    // Windows and Linux only; on macOS the application menu is the menu bar.
    private protected readonly WindowMenuBar? _menuBar;
    private readonly Action<Menu?> _applicationMenuChanged;
    private Menu? _ownMenu;
    private bool _hasOwnMenu;

    private protected cef_window_t* _window;

    // Last observed state, to turn CEF's bounds callbacks into Electron's specific events.
    private Rectangle _lastBounds;
    private bool _wasMaximized;
    private bool _wasMinimized;

    private bool _closable;
    private bool _skipTaskbar;
    private double _opacity;
    private bool _hasShadow;
    // macOS: Electron's is_kiosk_; elsewhere kiosk is full screen itself.
    private bool _kiosk;
    // Electron's layered_: once given an opacity, the window stays layered when click-through ends.
    private bool _layered;
    private Rectangle _normalBounds;
    private TitleBarOverlayView? _titleBarOverlay;
    private IReadOnlyList<DraggableRegion> _pageRegions = [];
    // Whether the app means the window to be shown; CEF activates windows that are not.
    private bool _meantVisible;
    private readonly NativeImage? _icon;
    private bool _thumbarAdded;
    private readonly nint[] _thumbarIcons = new nint[Thumbar.MaxButtons];
    // Electron's callback_map_, by button id; UI thread only.
    private readonly Dictionary<int, Action?> _thumbarClicks = [];
    private IReadOnlyList<ThumbarButton> _lastThumbarButtons = [];
    // Electron's messages_callback_map_: read inside the window procedure, changed from any thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Action<nint, nint>> _messageHooks = new();
    // Electron's accent_color_: null follows the system; UI thread only.
    private WindowsFrame.Accent? _accent;
    // Electron's rounded_corner_: true unless a frameless window turned it off.
    private bool _roundedCorners = true;
    // macOS: the Dock bounce FlashFrame started, to cancel.
    private nint _attentionRequest;
    // Set by Close(): a close from code goes through even when the user may not close.
    private bool _closeFromCode;
    // Set by Destroy(): CEF still asks can_close, which must then neither ask the app nor raise Closing.
    private bool _destroying;
    private protected string? _backgroundColor;
    // Windows: a transparent window loses its frame, resizing and maximizing.
    private readonly bool _transparent;

    // macOS keeps resizing and maximizing for a transparent window, as Electron's NativeWindowMac does.
    private readonly bool _macTransparent;

    // Linux: transparent only with a patched libcef, which can create the X window with an ARGB visual (DRIFT W24).
    private readonly bool _linuxTransparent;
    private bool _resizable;
    // A transparent window's bounds before Maximize filled the work area.
    private Rectangle? _restoreBounds;
    // macOS: the NSWindow whose full-screen toggle routes to simple full screen, or 0.
    private nint _routedNSWindow;
    // Whether the window takes input (every OS), and Linux's X11 cover that blocks it while it does not; UI thread only.
    private readonly WindowEnabledState _enabled = new();
    private nint _inputCover;
    // Electron's GetContentsView: fills the content area, with a BrowserWindow's page below ContentView, both filling it.
    private protected readonly View _contentsView;
    private bool _viewsDetached;
    // Touched only on the UI thread.
    private readonly List<BaseWindow> _children = [];
    // The option until SetParentWindow changes it; UI thread.
    private BaseWindow? _parent;
    // macOS: Electron's NativeWindowMac parent links, which attach this window to its parent as an AppKit child window.
    private readonly MacParentLink? _macLink;

    /// <summary>Creates the window, with an empty <see cref="ContentView"/>, shown unless <see cref="BaseWindowOptions.Show"/>
    /// is false. Electron's <c>new BaseWindow()</c>.</summary>
    /// <exception cref="InvalidOperationException">The application is not ready yet, or the parent is closed.</exception>
    /// <exception cref="ArgumentException"><see cref="BaseWindowOptions.Modal"/> without a parent.</exception>
    public BaseWindow(BaseWindowOptions? options = null) : this(options ?? new BaseWindowOptions(), opensItself: true)
    {
        CefThread.InvokeOnUi(() =>
        {
            // The parent may have closed since the base constructor checked it.
            try { ThrowIfParentClosed(); }
            catch
            {
                ReleaseViews();
                throw;
            }
            OpenWindow();
            return true;
        });
    }

    // A subclass creates the native window once its contents exist (OpenWindow), and raises the app's created event
    // itself: virtual members it overrides are not usable until its constructor has run.
    private protected BaseWindow(BaseWindowOptions options, bool opensItself)
    {
        CheckOptions(options);
        // Before the name and views exist, which a refused window would leave behind; BrowserWindow checks its own, as
        // its popups are not refused.
        if (opensItself && CefThread.InvokeOnUi(() => options.Parent is { _window: null }))
            throw new InvalidOperationException("The parent window is closed.");
        _options = options;
        _parent = _options.Parent;
        if (OperatingSystem.IsMacOS())
            _macLink = new MacParentLink(MacChildWindows.Instance, () => _window is null ? 0 : (nint)_window->get_window_handle(_window), _options.Modal);
        _icon = _options.IconImage ?? (_options.Icon is { } iconPath ? NativeImage.CreateFromBuffer(File.ReadAllBytes(iconPath)) : null);

        ClaimName();
        Id = Interlocked.Increment(ref s_nextId);
        _closable = _options.Closable;
        _skipTaskbar = _options.SkipTaskbar;
        _opacity = WindowPolish.ClampOpacity(_options.Opacity);
        _hasShadow = _options.HasShadow;
        _kiosk = _options.Kiosk && !_restoreDisplayMode;
        _accent = WindowsFrame.FromOption(_options.AccentColor);
        _transparent = _options.Transparent && OperatingSystem.IsWindows();
        _macTransparent = _options.Transparent && OperatingSystem.IsMacOS();
        _linuxTransparent = _options.Transparent && OperatingSystem.IsLinux() && CefRuntime.IsPatched;
        _resizable = _options.Resizable;
        _backgroundColor = WindowTransparency.InitialBackgroundColor(_options.BackgroundColor, _transparent || _macTransparent || _linuxTransparent,
            OperatingSystem.IsWindows() ? _options.BackgroundMaterial : null);
        _contentsView = new View();
        ContentView = new View { FillsParent = true };
        CefThread.InvokeOnUi(() =>
        {
            _contentsView.InsertChild(ContentView, 0);
            return true;
        });
        if (!OperatingSystem.IsMacOS())
        {
            _menuBar = new WindowMenuBar(this) { AutoHide = _options.AutoHideMenuBar };
            _menuBar.SetShown(!_options.AutoHideMenuBar);
        }
        _applicationMenuChanged = menu => Command(_ =>
        {
            if (!_hasOwnMenu) _menuBar?.SetMenu(menu);
        });
        _windowDelegate = new WindowDelegate
        {
            Created = OnWindowCreated,
            Closing = OnClosing,
            Destroyed = OnWindowDestroyed,
            ActivationChanged = OnActivationChanged,
            BoundsChanged = OnBoundsChanged,
            FullscreenTransition = OnFullscreenTransition,
            // Otherwise Chromium offers resize edges and a maximize box, which Electron's transparent windows lack.
            Resizable = CanResize(_options.Resizable) && !_transparent,
            Minimizable = _options.Minimizable,
            Maximizable = _options.Maximizable && !_transparent,
            ThemeChanged = () => Command(ApplyBackgroundColor),
            Frameless = IsFrameless,
            Translucent = WindowTransparency.TranslucentWidget(_options.Transparent, CefRuntime.IsPatched, OperatingSystem.IsWindows()),
            MinimumSize = new cef_size_t { width = _options.MinWidth, height = _options.MinHeight },
            MaximumSize = new cef_size_t { width = _options.MaxWidth, height = _options.MaxHeight },
            Parent = _parent is { } parent ? () => (nint)parent._window : null,
            Modal = _options.Modal,
            Accelerator = commandId => BlocksInput || (!IgnoresMenuShortcuts && (_menuBar?.OnAccelerator(commandId) ?? false)),
            KeyEvent = keyEvent => BlocksInput || (_menuBar?.OnKeyEvent(keyEvent) ?? false),
        };
    }

    // Before the window claims its name, so a rejected window leaves nothing behind.
    private protected static void CheckOptions(BaseWindowOptions options)
    {
        if (!OrielApp.Current.IsReady) throw new InvalidOperationException("Create windows after OrielApp.Ready.");
        if (options.Modal && options.Parent is null) throw new ArgumentException("A modal window needs a Parent.", nameof(options));
    }

    // UI thread, before the native window exists.
    private protected void ThrowIfParentClosed()
    {
        if (_options.Parent is not { _window: null }) return;
        ReleaseName();
        throw new InvalidOperationException("The parent window is closed.");
    }

    // UI thread: no window will own the views of a window refused after they exist.
    private void ReleaseViews()
    {
        _contentsView.RemoveChild(ContentView);
        _contentsView.ReleaseNative();
        ContentView.ReleaseNative();
    }

    private protected void OpenWindow()
    {
        lock (s_openGate) s_open.Add(this);
        Menu.ApplicationMenuChanged += _applicationMenuChanged;
        _parent?._children.Add(this);
        // NativeWindowMac's constructor: a non-modal parent is linked, and attached when the window is shown.
        _macLink?.SetParent(_parent?._macLink, attach: false);
        NativeObject.AddRef(_windowDelegate.Native);
        _window = CefNative.cef_window_create_top_level(_windowDelegate.Native);
    }

    /// <summary>The view filling the window's content area; add <see cref="WebContentsView"/>s and other views to it. A new
    /// window's has no children: a <see cref="BrowserWindow"/>'s page is not one of them but lies below it, filling the
    /// content area, as in Electron. Electron's <c>win.contentView</c>.</summary>
    /// <remarks>As in Electron, the views in it outlive the window: their pages stay open until closed or the app quits.</remarks>
    public View ContentView { get; private set; }

    /// <summary>Makes <paramref name="view"/> the window's <see cref="ContentView"/>, filling the content area; a
    /// <see cref="BrowserWindow"/>'s page stays below it. The previous content view leaves the window with its children,
    /// whose pages live on. A view with a parent leaves it first, as with <see cref="View.AddChildView"/>; another window's
    /// content view moves here, while that window's <see cref="ContentView"/> still returns it, as in Electron. Ignored
    /// once the window has closed. Electron's <c>win.setContentView()</c>.</summary>
    public void SetContentView(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        CefThread.InvokeOnUi(() =>
        {
            // As other commands, nothing on a closed window, whose views have left it.
            if (_viewsDetached) return true;
            if (ContentView.Parent == _contentsView)
            {
                _contentsView.RemoveChild(ContentView);
                ContentView.StopFilling();
            }
            view.Parent?.RemoveChild(view);
            ContentView = view;
            view.FillsParent = true;
            _contentsView.InsertChild(view, _contentsView.Children.Count);
            return true;
        });
    }

    // The contents view holds only the page (if any) below ContentView, both filling it, natively in the same order. For tests.
    internal bool ContentsViewMatches() => CefThread.InvokeOnUi(() => _contentsView.NativeChildrenMatch() && _contentsView.Children is { Count: 1 or 2 } children
        && children[^1] == ContentView && children.All(c => c.FillsParent) && (children.Count == 1 || this is BrowserWindow));

    /// <summary>A number unique among this process's windows. Electron's <c>win.id</c>.</summary>
    public int Id { get; }

    /// <summary>All open windows. Electron's <c>BaseWindow.getAllWindows()</c>.</summary>
    public static IReadOnlyList<BaseWindow> GetAllWindows()
    {
        lock (s_openGate) return [.. s_open];
    }

    /// <summary>The focused window, or null. Electron's <c>BaseWindow.getFocusedWindow()</c>.</summary>
    public static BaseWindow? GetFocusedWindow() => GetAllWindows().FirstOrDefault(w => w.IsFocused);

    /// <summary>The open window with <paramref name="id"/>, or null. Electron's <c>BaseWindow.fromId()</c>.</summary>
    public static BaseWindow? FromId(int id) => GetAllWindows().FirstOrDefault(w => w.Id == id);

    /// <summary>The window this one belongs to, or null. Electron's <c>win.getParentWindow()</c>.</summary>
    public BaseWindow? GetParentWindow() => CefThread.InvokeOnUi(() => _parent);

    /// <summary>Makes <paramref name="parent"/> this window's parent, or makes it a top-level window again: it stays above
    /// the parent and closes with it. Windows: a visible window is re-shown in its state and brought to the top. Linux:
    /// <c>WM_TRANSIENT_FOR</c>, as Electron on X11. macOS: an AppKit child window of the parent
    /// (<c>addChildWindow:ordered:NSWindowAbove</c>), attached now if this window is visible, otherwise when it is next
    /// shown. As in Electron, nothing stops a cycle. Electron's <c>win.setParentWindow()</c>.</summary>
    /// <exception cref="InvalidOperationException">The window is modal, whose parent is fixed, or the parent is closed.</exception>
    public void SetParentWindow(BaseWindow? parent)
    {
        if (_options.Modal) throw new InvalidOperationException("Can not be called for modal window");
        CefThread.InvokeOnUi(() =>
        {
            if (parent is { _window: null }) throw new InvalidOperationException("The parent window is closed.");
            _parent?._children.Remove(this);
            _parent = parent;
            parent?._children.Add(this);
            if (OperatingSystem.IsWindows() && _window is not null) WindowsWindowStyle.SetOwner(Hwnd(_window), parent is null ? 0 : Hwnd(parent._window));
            if (OperatingSystem.IsLinux() && _window is not null && !X11Window.SetTransientFor(Hwnd(_window), parent is null ? 0 : Hwnd(parent._window)))
                Console.Error.WriteLine("[Oriel] No X display for SetParentWindow.");
            if (OperatingSystem.IsMacOS())
            {
                // NativeWindowMac::SetParentWindow attaches only a window that is visible by its IsVisible.
                var attach = _window is not null && MacWindow.IsVisible((nint)_window->get_window_handle(_window));
                _macLink?.SetParent(parent?._macLink, attach);
            }
            return true;
        });
    }

    /// <summary>The open windows that belong to this one. Electron's <c>win.getChildWindows()</c>.</summary>
    public IReadOnlyList<BaseWindow> GetChildWindows() => CefThread.InvokeOnUi(() => (IReadOnlyList<BaseWindow>)[.. _children]);

    /// <summary>Whether this window disables its parent while shown. Electron's <c>win.isModal()</c>.</summary>
    public bool IsModal => _options.Modal;

    /// <summary>Lets the window take mouse, keyboard and touch input, or stops it; it stays disabled while a modal
    /// child is shown. Windows: <c>EnableWindow</c>. Linux: an X11 cover over the window, and its keys dropped. Stored
    /// only on macOS for now. Electron's <c>win.setEnabled()</c>.</summary>
    /// <remarks>The window manager can still activate a disabled window, as in Electron. On Linux the pointer crossing
    /// into or out of a disabled window still reaches its page (drift W5).</remarks>
    public void SetEnabled(bool enable) => CefThread.RunOnUi(() => SetEnabledOnUi(enable));

    // UI thread.
    internal void SetEnabledOnUi(bool enable)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows ignores the call while the window does not exist.
            if (_window is null || enable == _enabled.Requested) return;
            _enabled.SetEnabled(enable);
            ApplyWindowsEnabled(_window);
            return;
        }
        if (_enabled.SetEnabled(enable)) ApplyLinuxEnabled();
    }

    /// <summary>Whether the window takes input: false after <see cref="SetEnabled"/>(false) or while a modal child is
    /// shown. Windows: <c>IsWindowEnabled</c>. Always true on macOS for now. Electron's <c>win.isEnabled()</c>.</summary>
    public bool IsEnabled => OperatingSystem.IsWindows()
        ? IsWindowsEnabled
        : !OperatingSystem.IsLinux() || CefThread.InvokeOnUi(() => _enabled.Enabled);

    // Linux only: Chromium disables a modal's parent itself on Windows, which ApplyWindowsEnabled reads back.
    private void ChangeChildModals(bool increment)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (increment ? _enabled.IncrementChildModals() : _enabled.DecrementChildModals()) ApplyLinuxEnabled();
    }

    // Keys go to the X focus window, past the stock-CEF input cover, so the keyboard handlers drop them while disabled.
    private protected bool BlocksInput => OperatingSystem.IsLinux() && _enabled.DropsKeys;

    // UI thread. A view's page has a keyboard handler of its own, so it asks the window its view tree hangs from.
    internal static bool BlocksInputAround(View view)
    {
        var root = view;
        while (root.Parent is { } parent) root = parent;
        return GetAllWindows().FirstOrDefault(w => w._contentsView == root) is { BlocksInput: true };
    }

    private void ApplyLinuxEnabled()
    {
        if (!OperatingSystem.IsLinux() || _window is null) return;
        // Patch 0002 blocks keys too, so no key filter is added: Electron relies on DisableEventListening alone.
        _enabled.BlockedInViews = LinuxCefPatches.SetWindowInputEnabled(_window, _enabled.Enabled);
        if (_enabled.BlockedInViews) return;
        if (!_enabled.Enabled && _inputCover == 0) _inputCover = X11Window.BlockInput(Hwnd(_window));
        else if (_enabled.Enabled && _inputCover != 0)
        {
            X11Window.UnblockInput(_inputCover);
            _inputCover = 0;
        }
    }

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

    /// <summary>Raised while the user drags an edge of the window, before it resizes; cancel to keep the size.
    /// Windows only for now. Electron's <c>'will-resize'</c>.</summary>
    /// <remarks>Raised on the UI thread from inside the drag: handlers must return quickly.</remarks>
    public event EventHandler<WillResizeEventArgs>? WillResize;

    /// <summary>Raised while the user drags the window, before it moves; cancel to keep it in place.
    /// Windows only for now. Electron's <c>'will-move'</c>.</summary>
    /// <remarks>Raised on the UI thread from inside the drag: handlers must return quickly.</remarks>
    public event EventHandler<WillMoveEventArgs>? WillMove;

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

    /// <summary><see cref="SetAlwaysOnTop"/> turned always-on-top on or off; not raised when the state is unchanged,
    /// nor when another program changes the window's z-order, as Electron. Electron's <c>always-on-top-changed</c>.</summary>
    public event EventHandler<AlwaysOnTopChangedEventArgs>? AlwaysOnTopChanged;

    /// <summary>Whether the window has closed. Electron's <c>win.isDestroyed()</c>.</summary>
    public bool IsDestroyed => Query(_ => false, true);

    /// <summary>Shows and focuses the window. Electron's <c>win.show()</c>.</summary>
    public void Show() => Command(w =>
    {
        var wasVisible = w->@base.@base.is_visible(&w->@base.@base) != 0;
        _meantVisible = true;
        if (!wasVisible && _options.Modal) _options.Parent?.ChangeChildModals(increment: true);
        FlushPendingDisplayMode(w);
        _macLink?.Show();
        ShowThroughCef(w, activate: _focusable ?? _options.Focusable);
        ApplySkipTaskbar(w);
        if (!wasVisible) Shown?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>Hides the window. Electron's <c>win.hide()</c>.</summary>
    public void Hide() => Command(w =>
    {
        var wasVisible = w->@base.@base.is_visible(&w->@base.@base) != 0;
        _meantVisible = false;
        // Electron re-enables the parent on every hide of a modal, shown or not.
        if (_options.Modal) _options.Parent?.ChangeChildModals(increment: false);
        _macLink?.Hide();
        w->hide(w);
        if (wasVisible) Hidden?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>Whether the window is shown. Electron's <c>win.isVisible()</c>.</summary>
    public bool IsVisible => Query(w => w->@base.@base.is_visible(&w->@base.@base) != 0, false);

    /// <summary>Brings the window to the front and gives it focus. Electron's <c>win.focus()</c>.</summary>
    public void Focus() => CefThread.RunOnUi(() =>
    {
        // Windows disables a modal's parent, but SetForegroundWindow still activates a disabled window. Linux's Electron
        // activates the parent itself and leaves the modal to the window manager.
        var target = this;
        while (!OperatingSystem.IsLinux() && target._children.FirstOrDefault(c => c._options.Modal && c._window is not null && c._window->@base.@base.is_visible(&c._window->@base.@base) != 0) is { } modal)
            target = modal;
        if (target._window is not null) target._window->activate(target._window);
    });

    /// <summary>Removes focus from the window. Electron's <c>win.blur()</c>.</summary>
    public void Blur() => Command(w => w->deactivate(w));

    /// <summary>Whether the window has focus. Electron's <c>win.isFocused()</c>.</summary>
    public bool IsFocused => Query(w => w->is_active(w) != 0, false);

    /// <summary>Maximizes the window. Electron's <c>win.maximize()</c>.</summary>
    /// <remarks>A <see cref="BaseWindowOptions.Transparent"/> window fills its display's work area instead, as in
    /// Electron.</remarks>
    public void Maximize() => Command(MaximizeOn);

    private void MaximizeOn(cef_window_t* w)
    {
        if (!_transparent)
        {
            w->maximize(w);
            return;
        }
        if (IsMaximizedOn(w)) return;
        _restoreBounds = ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base));
        ApplyOuterBounds(w, WorkAreaOf(w), animate: false);
        // Electron's emulated maximize notifies itself; WM_SIZE only says the window was resized.
        RaiseEmulated(Maximized);
    }

    /// <summary>Leaves the maximized state. Electron's <c>win.unmaximize()</c>.</summary>
    public void Unmaximize() => Command(w =>
    {
        if (IsMaximizedOn(w) && RestoreOn(w)) RaiseEmulated(Unmaximized);
    });

    /// <summary>Whether the window is maximized. Electron's <c>win.isMaximized()</c>.</summary>
    public bool IsMaximized => Query(IsMaximizedOn, false);

    private bool IsMaximizedOn(cef_window_t* w)
        => w->is_maximized(w) != 0 || (_transparent && WindowTransparency.FillsWorkArea(
            ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base)), WorkAreaOf(w), w->is_minimized(w) != 0));

    // A transparent window leaves its emulated maximize by going back to the bounds it had; true when it did.
    private bool RestoreOn(cef_window_t* w)
    {
        if (_transparent && _restoreBounds is { } bounds && w->is_maximized(w) == 0 && IsMaximizedOn(w))
        {
            ApplyOuterBounds(w, bounds, animate: false);
            return true;
        }
        w->restore(w);
        return false;
    }

    // Where the poll raises the state events (no WM_SIZE route), it sees the emulated change itself.
    private void RaiseEmulated(EventHandler? handler)
    {
        if (_sizeState is not null) handler?.Invoke(this, EventArgs.Empty);
    }

    private static Rectangle WorkAreaOf(cef_window_t* w)
    {
        var bounds = w->@base.@base.get_bounds_in_screen(&w->@base.@base);
        var display = CefNative.cef_display_get_matching_bounds(&bounds, 0);
        if (display is null) return default;
        var area = ToRectangle(display->get_work_area(display));
        NativeObject.Release(display);
        return area;
    }

    /// <summary>Minimizes the window. Electron's <c>win.minimize()</c>.</summary>
    public void Minimize() => Command(w => w->minimize(w));

    /// <summary>Restores the window from minimized. Electron's <c>win.restore()</c>.</summary>
    public void Restore() => Command(w =>
    {
        if (RestoreOn(w)) RaiseEmulated(Restored);
    });

    /// <summary>Whether the window is minimized. Electron's <c>win.isMinimized()</c>.</summary>
    public bool IsMinimized => Query(w => w->is_minimized(w) != 0, false);

    /// <summary>Enters or leaves full screen. Electron's <c>win.setFullScreen()</c>.</summary>
    /// <remarks>On macOS this is native full screen unless <see cref="BaseWindowOptions.SimpleFullScreen"/>
    /// is set: native moves the window to its own Space and the other displays go black. For a window on
    /// one display while the others stay usable (an audience screen, a kiosk beside a control window),
    /// set that option or call <see cref="SetSimpleFullScreen"/>.</remarks>
    public void SetFullScreen(bool flag)
    {
        if (UsesSimpleFullScreen) SetSimpleFullScreen(flag);
        else Command(w => SetFullScreenOn(w, flag));
    }

    // Set during a Windows SetFullScreen whose event has already gone out; the transition's end then raises none.
    private bool _fullScreenAnnounced;

    private void SetFullScreenOn(cef_window_t* w, bool flag)
    {
        _sizeState?.SetFullScreen(flag);
        var announced = false;
        if (OperatingSystem.IsWindows())
        {
            // Electron's NativeWindowViews::SetFullScreen notifies first, before the resize, and on every call.
            announced = (w->is_fullscreen(w) != 0) != flag;
            (flag ? EnteredFullScreen : LeftFullScreen)?.Invoke(this, EventArgs.Empty);
            if (_window is null) return;
        }
        // Off macOS the transition ends inside set_fullscreen, so the flag covers exactly this call, even one a handler
        // made above, and cannot outlive a call that changed nothing.
        _fullScreenAnnounced = announced;
        w->set_fullscreen(w, flag ? 1 : 0);
        _fullScreenAnnounced = false;
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
            Command(w => SetFullScreenOn(w, flag));
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
    /// window's level, as Electron. On Windows any level keeps the window on top, and the levels below <c>pop-up-menu</c>
    /// keep it under the taskbar; Linux ignores them. An unknown level throws nowhere, as Electron: macOS takes it as
    /// <c>normal</c>, so the window leaves the top unless <paramref name="relativeLevel"/> moves it; Windows keeps the
    /// window on top and above the taskbar. A null level is <c>floating</c>, as Electron's for a non-string one.</remarks>
    public void SetAlwaysOnTop(bool flag, string level = "floating", int relativeLevel = 0)
    {
        level ??= "floating";
        var macLevel = MacWindowLevel(level);
        Command(w =>
        {
            var onTop = StaysOnTop(flag, macLevel + relativeLevel, OperatingSystem.IsMacOS());
            // The widget's z-order level, which Electron compares against too; z-order changes made elsewhere raise nothing.
            var wasOnTop = w->is_always_on_top(w) != 0;
            w->set_always_on_top(w, onTop ? 1 : 0);
            if (onTop && OperatingSystem.IsWindows()) WindowsWindow.EnsureTopmost((nint)w->get_window_handle(w));
            if (OperatingSystem.IsMacOS()) MacWindow.SetLevel((nint)w->get_window_handle(w), onTop ? macLevel + relativeLevel : 0);
            ApplyAlwaysOnTopLevel(w, onTop, level);
            if (wasOnTop != onTop) AlwaysOnTopChanged?.Invoke(this, new AlwaysOnTopChangedEventArgs(onTop));
        });
    }

    /// <summary>Whether <see cref="SetAlwaysOnTop"/> leaves the window on top: on macOS only if the resulting level is
    /// not the normal one, so <c>normal</c> with no <c>relativeLevel</c> is off; elsewhere <paramref name="flag"/> alone, as Electron.</summary>
    internal static bool StaysOnTop(bool flag, long macLevel, bool mac) => flag && (!mac || macLevel != 0);

    /// <summary>Electron's level names as AppKit window levels (NSWindowLevel); any other name is NSNormalWindowLevel.</summary>
    internal static long MacWindowLevel(string level) => level switch
    {
        "normal" => 0,
        "floating" or "torn-off-menu" => 3,
        "modal-panel" => 8,
        "main-menu" => 24,
        "status" => 25,
        "pop-up-menu" => 101,
        "screen-saver" => 1000,
        _ => 0,
    };

    /// <summary>Whether the window stays above others. Electron's <c>win.isAlwaysOnTop()</c>.</summary>
    public bool IsAlwaysOnTop => Query(w => w->is_always_on_top(w) != 0, false);

    /// <summary>Shows the window without focusing it. Windows only for now: macOS and Linux show and focus it.
    /// Electron's <c>win.showInactive()</c>.</summary>
    public void ShowInactive() => Command(w =>
    {
        var wasVisible = w->@base.@base.is_visible(&w->@base.@base) != 0;
        _meantVisible = true;
        FlushPendingDisplayMode(w);
        ShowThroughCef(w, activate: false);
        ApplySkipTaskbar(w);
        if (!wasVisible) Shown?.Invoke(this, EventArgs.Empty);
    });

    // Through CEF, so Views knows the window is shown; without activation the position changes it makes are kept from
    // activating it (Electron's ShowInactive, and every show of a window that cannot activate).
    private void ShowThroughCef(cef_window_t* w, bool activate)
    {
        if (activate || !OperatingSystem.IsWindows() || !WindowsSubclass.WithoutActivation(Hwnd(w), () => w->show(w))) w->show(w);
        // Handing the foreground back raised the previous window; an inactive show still comes out in front.
        else WindowsWindowStyle.BringToTopInactive(Hwnd(w));
    }

    /// <summary>Sets the window's opacity, clamped to 0–1. Windows only for now: stored but not shown on macOS and
    /// Linux. Electron's <c>win.setOpacity()</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The opacity is NaN.</exception>
    public void SetOpacity(double opacity)
    {
        var clamped = WindowPolish.ClampOpacity(opacity);
        Command(w =>
        {
            _opacity = clamped;
            if (OperatingSystem.IsWindows()) ApplyOpacity(w);
        });
    }

    /// <summary>The window's opacity, as set. Electron's <c>win.getOpacity()</c>.</summary>
    public double GetOpacity() => Query(_ => _opacity, 1.0);

    /// <summary>Gives the window a shadow or takes it away. Stored only: on Windows, as in Electron, it changes nothing,
    /// and macOS and Linux are not done yet. Electron's <c>win.setHasShadow()</c>.</summary>
    public void SetHasShadow(bool hasShadow) => Command(_ => _hasShadow = hasShadow);

    /// <summary>Whether the window has a shadow, as set. Electron's <c>win.hasShadow()</c>.</summary>
    public bool GetHasShadow() => Query(_ => _hasShadow, true);

    /// <summary>Lets mouse input pass through the window to whatever is below it, or stops that; with
    /// <see cref="IgnoreMouseEventsOptions.Forward"/> the page still gets mouse moves. Windows only for now.
    /// Electron's <c>win.setIgnoreMouseEvents()</c>.</summary>
    public void SetIgnoreMouseEvents(bool ignore, IgnoreMouseEventsOptions? options = null) => Command(w =>
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = Hwnd(w);
        WindowsWindowStyle.SetClickThrough(hwnd, ignore, keepLayered: _layered);
        // Forwarding only applies while ignoring; taking the mouse back always ends it.
        if (!WindowsSubclass.ForwardMouseMoves(hwnd, ignore && options?.Forward == true))
            Console.Error.WriteLine("[Oriel] Mouse moves cannot be forwarded to the page.");
    });

    /// <summary>Keeps the window out of screenshots and screen recordings, or lets it back in. Windows only for now.
    /// Electron's <c>win.setContentProtection()</c>.</summary>
    public void SetContentProtection(bool enable) => Command(w =>
    {
        if (OperatingSystem.IsWindows() && !WindowsWindowStyle.SetExcludedFromCapture(Hwnd(w), enable))
            Console.Error.WriteLine("[Oriel] Windows did not take the content protection.");
    });

    /// <summary>Whether the window is kept out of captures, read back from Windows; false elsewhere.
    /// Electron's <c>win.isContentProtected()</c>.</summary>
    public bool IsContentProtected() => Query(w => OperatingSystem.IsWindows() && WindowsWindowStyle.IsExcludedFromCapture(Hwnd(w)), false);

    /// <summary>Enters or leaves kiosk mode: full screen. Electron's <c>win.setKiosk()</c>.</summary>
    public void SetKiosk(bool flag)
    {
        Command(_ => _kiosk = flag);
        SetFullScreen(flag);
    }

    /// <summary>Whether the window is in kiosk mode: on Windows and Linux, whether it is full screen, however it got
    /// there, as Electron answers. Electron's <c>win.isKiosk()</c>.</summary>
    public bool IsKiosk() => OperatingSystem.IsMacOS() ? Query(_ => _kiosk, false) : IsFullScreen;

    /// <summary>The bounds the window has, or returns to, when it is neither maximized, minimized nor full screen.
    /// Electron's <c>win.getNormalBounds()</c>.</summary>
    public Rectangle GetNormalBounds() => Query(w =>
    {
        // A minimized window sits off every display, so the scale comes from the restored bounds, the monitor
        // MonitorFromWindow gives Electron's ScreenToDIPRect for an iconic window.
        if (OperatingSystem.IsWindows() && WindowsWindowStyle.GetNormalBounds(Hwnd(w)) is var (x, y, width, height))
            return Screen.ScreenToDipRect(null, new Rectangle(x, y, width, height));
        return IsNormal(w) ? ToRectangle(w->@base.@base.get_bounds_in_screen(&w->@base.@base)) : _normalBounds;
    }, default);

    private static bool IsNormal(cef_window_t* w) => w->is_maximized(w) == 0 && w->is_minimized(w) == 0 && w->is_fullscreen(w) == 0;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ApplyWindowsStyles(cef_window_t* w)
    {
        if (_opacity < 1) ApplyOpacity(w);
        var hwnd = Hwnd(w);
        WindowsSubclass.RouteSizing(hwnd, (wmsz, r) => Cancelled(WillResize, new WillResizeEventArgs(PixelsToDips(r), WindowPolish.EdgeFromWmsz(wmsz))));
        WindowsSubclass.RouteMoving(hwnd, r => Cancelled(WillMove, new WillMoveEventArgs(PixelsToDips(r))));
        WindowsSubclass.RouteMessages(hwnd, OnWindowMessage);
        WindowsSubclass.RouteThumbButtonClicks(hwnd, OnThumbarClick);
        WindowsSubclass.RouteTaskbarCreated(hwnd, () =>
        {
            if (_window is not null) RestoreThumbarButtons(_window);
        });
    }

    // Electron's frameless block in the NativeWindowViews constructor, and FlipWindowStyle for a framed window.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ApplyFrameStyles(cef_window_t* w)
    {
        var hwnd = Hwnd(w);
        if (IsFrameless)
        {
            WindowsWindowStyle.SetStyle(hwnd, WindowsFrame.FramelessStyle(_resizable, _options.Minimizable, _options.Maximizable, _options.ThickFrame));
            _roundedCorners = WindowsFrame.RoundedCorners(_options.ThickFrame, _options.RoundedCorners);
            if (WindowsWindow.IsWindows11) WindowsWindow.SetRoundedCorners(hwnd, _roundedCorners);
        }
        else if (!_options.ThickFrame) WindowsWindowStyle.FlipStyle(hwnd, false, WindowsFrame.WsThickFrame);
    }

    // Electron's OnWindowMessage after PreHandleMSG's NotifyWindowMessage; the colour messages stand in for
    // AccentColorObserver's registry watch.
    private void OnWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (message is 0x0320 /* WM_DWMCOLORIZATIONCOLORCHANGED */ or 0x001A /* WM_SETTINGCHANGE */)
            Command(w => UpdateAccentColor(w, w->is_active(w) != 0));
        if (_messageHooks.TryGetValue(message, out var callback)) callback(wParam, lParam);
    }

    // Electron's UpdateWindowAccentColor.
    private void UpdateAccentColor(cef_window_t* w, bool active)
    {
        if (!OperatingSystem.IsWindows() || !WindowsWindow.IsWindows11) return;
        var system = _accent is { Argb: not null } ? null : WindowsWindow.SystemAccentColor();
        var color = WindowsFrame.BorderColor(_accent, active, _accent is null && WindowsWindow.AccentColorOnTitleBars(), system);
        if (!WindowsWindow.SetBorderAndCaptionColor(Hwnd(w), color, caption: !IsFrameless))
            Console.Error.WriteLine("[Oriel] DWM did not take the accent colour.");
    }

    private bool Cancelled<T>(EventHandler<T>? handler, T args) where T : CancelEventArgs
    {
        if (handler is null) return false;
        handler(this, args);
        return args.Cancel;
    }

    private Rectangle PixelsToDips((int Left, int Top, int Right, int Bottom) r)
        => Screen.ScreenToDipRect(this, new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ApplyOpacity(cef_window_t* w)
    {
        _layered = true;
        if (!WindowsWindowStyle.SetAlpha(Hwnd(w), WindowPolish.ToAlpha(_opacity)))
            Console.Error.WriteLine("[Oriel] Windows did not take the opacity.");
    }

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
        // A window with a sheet attached does not close, even once the sheet ends; Electron ends them first.
        if (OperatingSystem.IsMacOS()) MacWindow.EndAttachedSheets((nint)w->get_window_handle(w));
        w->close(w);
    });

    /// <summary>Closes the window at once: no <see cref="Closing"/>, no beforeunload. Electron's <c>win.destroy()</c>.</summary>
    public void Destroy() => Command(w =>
    {
        _destroying = true;
        // Also for the children OnWindowDestroyed destroys: a window with a sheet attached does not close.
        if (OperatingSystem.IsMacOS()) MacWindow.EndAttachedSheets((nint)w->get_window_handle(w));
        DestroyContents();
        w->close(w);
    });

    /// <summary>Lets the user resize the window, or stops. Electron's <c>win.setResizable()</c>.</summary>
    public void SetResizable(bool flag) => Command(_ =>
    {
        _resizable = flag;
        _windowDelegate.Resizable = CanResize(flag) && !_transparent;
    });

    // Electron's CanResize: on Windows a framed window without a thick frame cannot be resized.
    private bool CanResize(bool resizable) => OperatingSystem.IsWindows() ? WindowsFrame.CanResize(!IsFrameless, resizable, _options.ThickFrame) : resizable;

    /// <summary>Whether the user can resize the window. Electron's <c>win.isResizable()</c>.</summary>
    public bool IsResizable => Query(_ => WindowTransparency.ReportsResizable(_options.Frame, _transparent, CanResize(_resizable)), false);

    /// <summary>Lets the window be minimized, or stops. Electron's <c>win.setMinimizable()</c>.</summary>
    public void SetMinimizable(bool flag) => Command(_ =>
    {
        _windowDelegate.Minimizable = flag;
        UpdateCaptionButtons();
    });

    /// <summary>Whether the window can be minimized. Electron's <c>win.isMinimizable()</c>.</summary>
    public bool IsMinimizable => Query(_ => _windowDelegate.Minimizable, false);

    /// <summary>Lets the window be maximized, or stops. Electron's <c>win.setMaximizable()</c>.</summary>
    public void SetMaximizable(bool flag) => Command(_ =>
    {
        _windowDelegate.Maximizable = flag && !_transparent;
        UpdateCaptionButtons();
    });

    /// <summary>Whether the window can be maximized. Electron's <c>win.isMaximizable()</c>.</summary>
    public bool IsMaximizable => Query(_ => _windowDelegate.Maximizable, false);

    /// <summary>Lets the user close the window, or stops; code can still close it. Electron's <c>win.setClosable()</c>.</summary>
    public void SetClosable(bool flag) => Command(_ =>
    {
        _closable = flag;
        UpdateCaptionButtons();
    });

    // Electron's overlay disables the buttons the window does not allow.
    private void UpdateCaptionButtons() => _titleBarOverlay?.SetEnabled(_windowDelegate.Minimizable, _windowDelegate.Maximizable, _closable);

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
        ParseColor(color);
        Command(w => SetBackgroundColorOn(w, color));
    }

    private void SetBackgroundColorOn(cef_window_t* w, string color)
    {
        _backgroundColor = color;
        ApplyBackgroundColor(w);
        ApplyPageBackground();
    }

    // CEF clears a view's background whenever its theme applies (CefViewView::OnThemeChanged), which includes the view
    // joining its window, so on_theme_changed calls this again.
    private protected void ApplyBackgroundColor(cef_window_t* w)
    {
        var transparentPage = PageTransparent;
        if (WindowTransparency.WindowFill(_backgroundColor, transparentPage, clearWindow: _linuxTransparent) is not { } fill
            || WindowTransparency.PageBackgroundColor(_backgroundColor, transparentPage) is not { } page) return;
        w->@base.@base.set_background_color(&w->@base.@base, ParseColor(fill));
        var under = WindowTransparency.BrowserViewColor(ParseColor(page));
        ApplyPageViewBackground(under);
        // The contents view sits between the window and the page; a themed panel colour there would hide a transparent window.
        _contentsView.SetBackgroundArgb(under);
    }

    /// <summary>The colour behind the page, as set. Electron's <c>win.getBackgroundColor()</c>.</summary>
    public string? GetBackgroundColor() => Query(_ => _backgroundColor, null);

    /// <summary>Calls <paramref name="callback"/> with the raw <c>wParam</c> and <c>lParam</c> whenever the window
    /// receives <paramref name="message"/>, synchronously on the UI thread inside the window procedure, before Chromium
    /// handles the message, which then goes on as usual. One callback per message: hooking it again replaces the
    /// callback. Always true. Windows only: the callback never runs on macOS and Linux.
    /// Electron's <c>win.hookWindowMessage()</c>.</summary>
    /// <remarks>An exception from the callback is written to standard error and does not stop the message.</remarks>
    public bool HookWindowMessage(uint message, Action<nint, nint> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _messageHooks[message] = callback;
        return true;
    }

    /// <summary>Whether <paramref name="message"/> is hooked. Electron's <c>win.isWindowMessageHooked()</c>.</summary>
    public bool IsWindowMessageHooked(uint message) => _messageHooks.ContainsKey(message);

    /// <summary>Removes the callback for <paramref name="message"/>, if any. Electron's <c>win.unhookWindowMessage()</c>.</summary>
    public void UnhookWindowMessage(uint message) => _messageHooks.TryRemove(message, out _);

    /// <summary>Removes every message callback. Electron's <c>win.unhookAllWindowMessages()</c>.</summary>
    public void UnhookAllWindowMessages() => _messageHooks.Clear();

    /// <summary>Sets the window's accent colour (border, and caption when framed) on Windows 11: a colour, the system
    /// accent colour (<see langword="true"/>), none (<see langword="false"/>), or null to follow the system setting
    /// again. A colour string Electron cannot parse changes nothing. Ignored on macOS and Linux.
    /// Electron's <c>win.setAccentColor()</c>.</summary>
    public void SetAccentColor(AccentColor? accentColor) => Command(w =>
    {
        if (accentColor is { Color: { } color } && !CssColor.TryParseElectron(color, out _)) return;
        _accent = WindowsFrame.FromOption(accentColor);
        UpdateAccentColor(w, w->is_active(w) != 0);
    });

    /// <summary>The window's accent colour as <c>#RRGGBB</c>: the colour set, else the system accent colour; or
    /// <see cref="AccentColor.None"/> when turned off or the system has none. Windows only: <see cref="AccentColor.None"/>
    /// on macOS and Linux. Electron's <c>win.getAccentColor()</c>, whose <c>false</c> is <see cref="AccentColor.None"/>.</summary>
    public AccentColor GetAccentColor() => Query(_ =>
    {
        if (!OperatingSystem.IsWindows()) return AccentColor.None;
        var hex = WindowsFrame.Report(_accent, _accent is { Argb: not null } ? null : WindowsWindow.SystemAccentColor());
        return hex is null ? AccentColor.None : AccentColor.FromColor(hex);
    }, AccentColor.None);

    /// <summary>Sets what Windows 11 22H2 and later draws behind the window. Windows only: does nothing on macOS and
    /// Linux. Electron's <c>win.setBackgroundMaterial()</c>.</summary>
    /// <remarks>As in Electron, a material makes the background colour transparent so it shows behind the page, and
    /// <see cref="BackgroundMaterial.None"/> makes it white; <see cref="BackgroundMaterial.Auto"/> leaves it.</remarks>
    public void SetBackgroundMaterial(BackgroundMaterial material) => Command(w =>
    {
        if (!OperatingSystem.IsWindows()) return;
        ApplyBackgroundMaterial(w, material);
        if (WindowTransparency.MaterialBackgroundColor(material) is { } color) SetBackgroundColorOn(w, color);
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ApplyBackgroundMaterial(cef_window_t* w, BackgroundMaterial material)
    {
        var hwnd = Hwnd(w);
        if (!WindowsWindow.SetBackdrop(hwnd, BackgroundMaterials.ToBackdrop(material)))
        {
            Console.Error.WriteLine("[Oriel] DWM did not take the background material (it needs Windows 11 22H2).");
            return;
        }
        WindowsTranslucency.ActivateBackdrop(hwnd, _options.Frame);
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

    // UI thread: the page CEF's dialogs run through, the window's own or else the first open one among the views it
    // shows; a content view moved to another window is that window's.
    internal WebContents? DialogPage()
    {
        if (_window is null) return null;
        if (OwnPage is { } own) return own.Browser is null ? null : own;
        return HostedPages().FirstOrDefault(p => p.Browser is not null);
    }

    /// <summary>The pages in the window's views, its own page first: what it shows now, not what its
    /// <see cref="ContentView"/> held once.</summary>
    internal IEnumerable<WebContents> HostedPages() => PagesIn(_contentsView);

    /// <summary>The open window showing <paramref name="page"/>, as its own page or in any of its views, or null.</summary>
    internal static BaseWindow? HostOf(WebContents page) => GetAllWindows().FirstOrDefault(w => w.HostedPages().Contains(page));

    private static IEnumerable<WebContents> PagesIn(View view)
    {
        if (view is WebContentsView { WebContents: var page }) yield return page;
        foreach (var child in view.Children)
            foreach (var nested in PagesIn(child)) yield return nested;
    }

    internal static void FocusAll()
    {
        foreach (var window in GetAllWindows()) window.Focus();
    }

    // WindowTest's handle on CEF patch 0002 (DRIFT W5); false on stock CEF.
    internal bool SetLinuxInputEnabled(bool enabled) => Query(w => LinuxCefPatches.SetWindowInputEnabled(w, enabled), false);

    // Window calls are only valid on the UI thread and before the window is destroyed.
    private protected void Command(WindowAction action) => CefThread.RunOnUi(() =>
    {
        if (_window is not null) action(_window);
    });

    private protected T Query<T>(WindowQuery<T> query, T whenClosed)
        => CefThread.InvokeOnUi(() => _window is null ? whenClosed : query(_window));

    private protected delegate void WindowAction(cef_window_t* window);

    private protected delegate T WindowQuery<out T>(cef_window_t* window);

    internal static Rectangle ToRectangle(cef_rect_t rect) => new(rect.x, rect.y, rect.width, rect.height);

    private void OnWindowCreated(cef_window_t* window)
    {
        _meantVisible = _options.Show;
        // Electron's widget for a focusable: false window cannot activate either; CEF activates it on creation.
        if (OperatingSystem.IsWindows()) WindowsSubclass.RefuseActivationWhileHidden(Hwnd(window), () => !_meantVisible || !(_focusable ?? _options.Focusable));
        RouteSizeEvents(window);
        if (_menuBar is not null)
        {
            _menuBar.Attach(window, _contentsView.NativeView);
            _menuBar.SetMenu(_hasOwnMenu ? _ownMenu : Menu.GetApplicationMenu());
        }
        else
        {
            NativeObject.AddRef(_contentsView.NativeView);
            window->@base.add_child_view(&window->@base, _contentsView.NativeView);
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
        ApplyBackgroundColor(window);
        if (_macTransparent && OperatingSystem.IsMacOS())
            // Electron hides the traffic lights of a transparent window with the normal title bar style, the only one here.
            MacWindow.MakeTransparent((nint)window->get_window_handle(window), hideButtons: true);
        if (OperatingSystem.IsWindows())
        {
            if (_transparent) MakeSeeThrough(window);
            if (_options.BackgroundMaterial is { } material) ApplyBackgroundMaterial(window, material);
        }
        if (_icon is not null) SetIconOn(window, _icon);
        // Electron sets the variant only when asked, and only at creation.
        if (_options.DarkTheme && OperatingSystem.IsLinux() && !X11Window.SetGtkThemeVariant(Hwnd(window), dark: true))
            Console.Error.WriteLine("[Oriel] No X display for DarkTheme.");

        if (UsesSimpleFullScreen && OperatingSystem.IsMacOS())
            _routedNSWindow = MacWindow.RouteFullScreenToggle((nint)window->get_window_handle(window), () => SetSimpleFullScreen(!IsSimpleFullScreen));
        if (_options.AlwaysOnTop) window->set_always_on_top(window, 1);
        if (OperatingSystem.IsWindows()) ApplyWindowsStyles(window);
        // Late, still before the first show: CEF recomputes the styles while the window is set up.
        if (OperatingSystem.IsWindows()) ApplyFrameStyles(window);
        if (TitleBar.HasOverlay(_options.TitleBarStyle, _options.TitleBarOverlay, OperatingSystem.IsMacOS())) AttachTitleBarOverlay(window, _options.TitleBarOverlay!);
        AttachWindowsState(window);
        // After the size and position options, before full screen and the show, as Electron's InitFromOptions.
        RestoreWindowState(window);
        // OnBoundsChanged ignores bounds set before _window exists, and a window maximized on its first show is never normal.
        _normalBounds = OuterBoundsOn(window);
        SaveWindowState(window);
        ApplyLinuxEnabled();
        if (_options.Show)
        {
            if (_options.Modal) _options.Parent?.ChangeChildModals(increment: true);
            FlushPendingDisplayMode(window);
            _macLink?.Show();
            ShowThroughCef(window, activate: _options.Focusable);
            ApplySkipTaskbar(window);
        }
        if (_options.HiddenInMissionControl && OperatingSystem.IsMacOS())
            MacWindow.SetCollectionBehavior((nint)window->get_window_handle(window), MacWindow.Transient, true);
        if ((_options.FullScreen && !_restoreDisplayMode) || _kiosk)
        {
            if (UsesSimpleFullScreen && OperatingSystem.IsMacOS()) MacWindow.SetSimpleFullScreen((nint)window->get_window_handle(window), true);
            else window->set_fullscreen(window, 1);
        }

        // The show above activates the window before _window is set, so OnActivationChanged could not apply it.
        if (OperatingSystem.IsWindows() && window->is_active(window) != 0) UpdateAccentColor(window, true);

        _lastBounds = ToRectangle(window->@base.@base.get_bounds_in_screen(&window->@base.@base));
        // Also where WM_SIZE raises the state events: the poll re-subclasses Chromium's page windows for the overlay's
        // HTMAXBUTTON, and they come and go.
        CefThread.PostUiDelayed(PollState, s_statePoll);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void MakeSeeThrough(cef_window_t* w)
    {
        var hwnd = Hwnd(w);
        if (!WindowsTranslucency.MakeSeeThrough(hwnd)) Console.Error.WriteLine("[Oriel] The window could not be made transparent.");
        // Chromium gives its windows Mica (DesktopWindowTreeHostWin::Init), which would fill what should be see-through.
        if (_options.BackgroundMaterial is null) WindowsWindow.SetBackdrop(hwnd, WindowsBackdrop.None);
    }

    private bool OnClosing(cef_window_t* window)
    {
        // CEF asks again once the contents are gone, by when the close was approved; a Destroy is never put to the app.
        if (CloseApproved || _destroying)
        {
            ApproveClose(window);
            return true;
        }

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

        var canClose = ContinueClose();
        if (canClose) ApproveClose(window);
        return canClose;
    }

    private void ApproveClose(cef_window_t* window)
    {
        // Electron saves from OnWindowClosed; by on_window_destroyed CEF reports empty bounds.
        FlushWindowState(window);
        DetachViews();
    }

    // As Electron, views added to the window outlive it: tearing the window down would destroy their pages, and a page
    // whose view has merely left the window lives on. The content view keeps its children, as Electron's does.
    private protected void DetachViews()
    {
        _viewsDetached = true;
        if (ContentView.Parent != _contentsView) return;
        _contentsView.RemoveChild(ContentView);
        ContentView.StopFilling();
    }

    private void OnActivationChanged(bool active)
    {
        if (OperatingSystem.IsWindows() && _window is not null) UpdateAccentColor(_window, active);
        if (!active) _menuBar?.OnWindowBlurred();
        if (!active) OnWindowDeactivated();
        (active ? Focused : Blurred)?.Invoke(this, EventArgs.Empty);
        OnActivationChangedCore(active);
    }

    // CEF reports no minimize/restore of its own, and on macOS minimizing changes no bounds, so the
    // state is also polled while the window is open.
    private static readonly TimeSpan s_statePoll = TimeSpan.FromMilliseconds(250);

    private void PollState()
    {
        if (_window is null) return;
        if (OperatingSystem.IsWindows()) PassMaximizeHitTests(_window);
        RaiseStateChanges();
        CefThread.PostUiDelayed(PollState, s_statePoll);
    }

    private void OnBoundsChanged(cef_rect_t rect)
    {
        var bounds = ToRectangle(rect);
        var previous = _lastBounds;
        _lastBounds = bounds;
        if (_window is not null && IsNormal(_window)) _normalBounds = bounds;
        if (bounds.X != previous.X || bounds.Y != previous.Y) Moved?.Invoke(this, EventArgs.Empty);
        if (bounds.Width != previous.Width || bounds.Height != previous.Height)
        {
            if (_titleBarOverlay is not null && _window is not null) ApplyDraggableRegions(_window);
            Resized?.Invoke(this, EventArgs.Empty);
        }
        RaiseStateChanges();
    }

    private void RaiseStateChanges()
    {
        if (_window is null) return;
        // The overlay's maximize/restore button follows the state even where WM_SIZE raises the events.
        if (_sizeState is not null)
        {
            _titleBarOverlay?.SetMaximized(IsMaximizedOn(_window));
            return;
        }
        var maximized = IsMaximizedOn(_window);
        var minimized = _window->is_minimized(_window) != 0;
        if (maximized != _wasMaximized) (maximized ? Maximized : Unmaximized)?.Invoke(this, EventArgs.Empty);
        if (minimized != _wasMinimized) (minimized ? Minimized : Restored)?.Invoke(this, EventArgs.Empty);
        _wasMaximized = maximized;
        _titleBarOverlay?.SetMaximized(maximized);
        _wasMinimized = minimized;
    }

    private void OnFullscreenTransition(bool completed)
    {
        _fullscreenTransitioning = !completed;
        if (!completed || _window is null) return;
        var fullscreen = _window->is_fullscreen(_window) != 0;
        if (fullscreen) _sizeState?.SetFullScreen(true);
        // Electron's SetFullScreen: corners that round square up in full screen, framed windows included.
        if (OperatingSystem.IsWindows() && _roundedCorners && WindowsWindow.IsWindows11) WindowsWindow.SetRoundedCorners(Hwnd(_window), !fullscreen);
        _titleBarOverlay?.SetVisible(!fullscreen);
        if (_fullScreenAnnounced) _fullScreenAnnounced = false;
        else (fullscreen ? EnteredFullScreen : LeftFullScreen)?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowDestroyed(cef_window_t* window)
    {
        if (_routedNSWindow != 0 && OperatingSystem.IsMacOS()) MacWindow.UnrouteFullScreenToggle(_routedNSWindow);
        Menu.ApplicationMenuChanged -= _applicationMenuChanged;
        _menuBar?.Detach();
        _titleBarOverlay?.Detach();
        if (OperatingSystem.IsWindows())
            foreach (var icon in _thumbarIcons) WindowsTaskbar.DestroyIconHandle(icon);
        OnNativeWindowDestroyed();
        NativeObject.Release(_window);
        _window = null;

        lock (s_openGate) s_open.Remove(this);
        ReleaseName();
        _parent?._children.Remove(this);
        _macLink?.Forget();
        // The cover went with the X window.
        _inputCover = 0;
        if (_options is { Modal: true, Parent: { } parent } && OperatingSystem.IsLinux())
        {
            parent.ChangeChildModals(increment: false);
            // Electron focuses the parent once its modal closes (NativeWindowViews::OnWidgetDestroying).
            if (parent._window is not null) parent._window->activate(parent._window);
        }
        // macOS closes child windows with their parent; Linux does not. Destroy, as Windows does with owned
        // windows: a child that vetoed would outlive its parent.
        foreach (var child in _children.ToArray()) child.Destroy();
        DetachFromOpener();
        Closed?.Invoke(this, EventArgs.Empty);
        if (s_open.Count == 0) OrielApp.Current.OnAllWindowsClosed();
    }

    // The base constructor runs before the subclass has its contents, so it only stores delegates that read these later.

    /// <summary>The window's own page, if it has one.</summary>
    private protected virtual WebContents? OwnPage => null;

    /// <summary>Whether the page has the keyboard and the menu bar's accelerators stand aside.</summary>
    private protected virtual bool IgnoresMenuShortcuts => false;

    /// <summary>Whether the page's own background is transparent when the window gives it no colour.</summary>
    private protected virtual bool PageTransparent => false;

    /// <summary>UI thread: the colour under the page's view, as <see cref="ApplyBackgroundColor"/> sets it.</summary>
    private protected virtual void ApplyPageViewBackground(uint argb) { }

    /// <summary>UI thread: the window's background colour changed, for the page to follow.</summary>
    private protected virtual void ApplyPageBackground() { }

    /// <summary>UI thread: <see cref="Destroy"/> is closing the window; tear the contents down without asking them.</summary>
    private protected virtual void DestroyContents() { }

    /// <summary>UI thread: whether CEF is asking again after the contents already let the window close.</summary>
    private protected virtual bool CloseApproved => false;

    /// <summary>UI thread: <see cref="Closing"/> was not cancelled; false while the contents decide (beforeunload).</summary>
    private protected virtual bool ContinueClose() => true;

    /// <summary>UI thread: the window lost activation, before <see cref="Blurred"/>.</summary>
    private protected virtual void OnWindowDeactivated() { }

    /// <summary>UI thread: after <see cref="Focused"/> or <see cref="Blurred"/>.</summary>
    private protected virtual void OnActivationChangedCore(bool active) { }

    /// <summary>UI thread: the native window is gone; release the contents' native views before it is released.</summary>
    private protected virtual void OnNativeWindowDestroyed() { }

    /// <summary>UI thread: the window closed, just before <see cref="Closed"/>.</summary>
    private protected virtual void DetachFromOpener() { }
}
