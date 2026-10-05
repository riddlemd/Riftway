using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>The page inside a window: Electron's <c>webContents</c>.</summary>
public sealed unsafe partial class WebContents
{
    private const string IsolatedWorld = "oriel";
    private const string Binding = "__orielIpc";

    private static readonly string s_preloadRuntime = ReadResource("Oriel.Ipc.preload.js");
    private static readonly string s_mainWorldStub = ReadResource("Oriel.Ipc.main-world.js");

    private readonly string? _appPreload;
    // DevTools' id for the registered isolated-world script, so a session preload change can replace it.
    private string? _isolatedScriptId;
    private readonly DevToolsChannel _devTools = new();
    private readonly List<string> _pendingDeliveries = [];
    private readonly CancellationTokenSource _destroyed = new();
    // Written on the UI thread, read and consumed on the IO thread by the request handler.
    private readonly Lock _pendingLoadGate = new();
    private PendingLoad? _pendingLoad;

    private cef_browser_t* _browser;
    private bool _ready;
    private (string Url, PendingLoad? Load)? _queuedLoad;
    private string? _userAgent;
    private string? _defaultUserAgent;
    private string? _mainFrameId;
    private int? _isolatedContext;
    // False until the app's first navigation: events from the initial about:blank are not the app's.
    private bool _navigated;
    private string _title = string.Empty;
    private readonly WebPreferences _preferences;

    internal WebContents(string? appPreload, WebPreferences preferences, Session session)
    {
        Permissions = new PermissionHandler { Prompt = DecidePromptAsync, MediaAccess = DecideMediaAsync };
        Downloads = new DownloadHandler
        {
            Before = (snapshot, before) => session.OnBeforeDownload(this, snapshot, before),
            Updated = session.OnDownloadUpdated,
        };
        _defaultUserAgent = session.UserAgentOrNull ?? OrielApp.CurrentOrNull?.UserAgentFallback;
        _acceptLanguages = session.AcceptLanguages;
        session.Track(this);
        RegisterLifecycle();
        _preferences = preferences;
        Session = session;
        JsDialog = new JsDialogHandler { SuppressAll = preferences.DisableDialogs };
        // Chrome's own shortcut (F12) and the context menu's Inspect open DevTools without OpenDevTools.
        if (!preferences.DevTools)
            Command = new CommandHandler(["IDC_DEV_TOOLS", "IDC_DEV_TOOLS_CONSOLE", "IDC_DEV_TOOLS_DEVICES", "IDC_DEV_TOOLS_INSPECT",
                "IDC_DEV_TOOLS_TOGGLE", "IDC_CONTENT_CONTEXT_INSPECTELEMENT", "IDC_CONTENT_CONTEXT_INSPECTBACKGROUNDPAGE"]);
        _appPreload = appPreload;
        _devTools.EventReceived += OnDevToolsEvent;

        Display = new DisplayHandler
        {
            TitleChanged = OnTitleChanged,
            FaviconUrlsChanged = OnFaviconUrlsChanged,
            FullscreenModeChanged = OnFullscreenModeChanged,
        };
        ContextMenuRequests = new ContextMenuHandler(OnContextMenu);
        Load = new LoadHandler
        {
            LoadStarted = isMain => { if (isMain) ApplyDefaultZoom(); },
            LoadingStateChanged = loading => Raise(loading ? DidStartLoading : DidStopLoading),
            LoadEnded = (isMain, _) =>
            {
                if (_navigated) DidFrameFinishLoad?.Invoke(this, isMain);
                if (!isMain) return;
                if (_navigated && !_bootstrapForgotten)
                {
                    _bootstrapForgotten = true;
                    _devTools.Send("Page.resetNavigationHistory");
                }
                Raise(DidFinishLoad);
                if (_navigated) CompleteLoad(null);
            },
            LoadFailed = (isMain, code, text, url) =>
            {
                // CEF aborts a navigation a webRequest listener cancelled; Electron reports it blocked.
                if (isMain && code == AbortedError && Interlocked.Exchange(ref _blockedNavigation, null) == url)
                    (code, text) = (BlockedByClientError, "ERR_BLOCKED_BY_CLIENT");
                // An aborted load was superseded by a newer one, whose task stays pending; LoadUrl already failed the old one.
                if (isMain && _navigated && code != AbortedError) CompleteLoad(new LoadFailedException(code, text, url));
                if (!_navigated) return;
                var failure = new LoadFailedEventArgs(code, text, url, isMain);
                DidFailProvisionalLoad?.Invoke(this, failure);
                // Electron's did-fail-load leaves out cancelled loads.
                if (code != AbortedError) DidFailLoad?.Invoke(this, failure);
            },
        };
        var webRequest = session.WebRequest;
        Request = new RequestHandler(new ResourceRequestHandler
        {
            BeforeResourceLoad = OnBeforeResourceLoad,
            Decide = request => WatchBlocked(request, webRequest.Decide(this, request)),
            Sent = request => webRequest.Sent(this, request),
            Redirected = (request, response, url) => webRequest.Redirected(this, request, response, url),
            ResponseStarted = (request, response) =>
            {
                RecordNavigationResponse(request, response);
                webRequest.ResponseStarted(this, request, response);
            },
            Completed = (request, response, status) => webRequest.Completed(this, request, response, status),
        })
        {
            Wants = WantsRequest,
            CertificateError = OnCertificateError,
            SelectClientCertificate = OnSelectClientCertificate,
            BeforeBrowse = OnBeforeBrowse,
            Login = OnLogin,
            RenderProcessTerminated = OnRenderProcessTerminated,
            RenderProcessUnresponsive = OnRenderProcessUnresponsive,
            RenderProcessResponsive = OnRenderProcessResponsive,
        };
    }

    /// <summary>The page's title changed; cancel to keep the window's title. Electron's <c>page-title-updated</c>.</summary>
    public event EventHandler<PageTitleUpdatedEventArgs>? PageTitleUpdated;

    /// <summary>The page started loading (the tab spinner starts). Electron's <c>did-start-loading</c>.</summary>
    public event EventHandler? DidStartLoading;

    /// <summary>The page stopped loading. Electron's <c>did-stop-loading</c>.</summary>
    public event EventHandler? DidStopLoading;

    /// <summary>The main frame finished loading. Electron's <c>did-finish-load</c>.</summary>
    public event EventHandler? DidFinishLoad;

    /// <summary>A load failed or was cancelled. Electron's <c>did-fail-load</c>.</summary>
    public event EventHandler<LoadFailedEventArgs>? DidFailLoad;

    internal DisplayHandler Display { get; }

    internal LoadHandler Load { get; }

    internal RequestHandler Request { get; }

    internal JsDialogHandler JsDialog { get; }

    internal CommandHandler? Command { get; }

    internal ContextMenuHandler ContextMenuRequests { get; }

    /// <summary>The user asked for a context menu (right click, the menu key, Shift+F10). Oriel shows none of its
    /// own, as Electron: call <see cref="Menu.Popup"/> from here to show one. Electron's <c>context-menu</c>.</summary>
    public event EventHandler<ContextMenuParams>? ContextMenu;

    private void OnContextMenu(cef_frame_t* frame, cef_context_menu_params_t* p)
    {
        var flags = p->get_edit_state_flags(p);
        bool Has(cef_context_menu_edit_state_flags_t flag) => (flags & flag) != 0;
        var suggestions = CefNative.cef_string_list_alloc();
        p->get_dictionary_suggestions(p, suggestions);
        var words = new List<string>();
        for (nuint i = 0; i < CefNative.cef_string_list_size(suggestions); i++)
        {
            cef_string_utf16_t word = default;
            if (CefNative.cef_string_list_value(suggestions, i, &word) != 0) words.Add(CefString.Read(&word));
            CefString.Clear(&word);
        }
        CefNative.cef_string_list_free(suggestions);
        var misspelled = CefString.ReadUserFree(p->get_misspelled_word(p));
        // macOS checks with the system spell checker, whose guesses Chromium fetches only when it builds its own menu.
        if (OperatingSystem.IsMacOS() && words.Count == 0 && misspelled.Length > 0) words = [.. Oriel.Cef.Platform.MacSpelling.Guesses(misspelled)];

        var args = new ContextMenuParams
        {
            X = p->get_xcoord(p),
            Y = p->get_ycoord(p),
            LinkUrl = CefString.ReadUserFree(p->get_link_url(p)),
            SrcUrl = CefString.ReadUserFree(p->get_source_url(p)),
            PageUrl = CefString.ReadUserFree(p->get_page_url(p)),
            FrameUrl = CefString.ReadUserFree(p->get_frame_url(p)),
            FrameCharset = CefString.ReadUserFree(p->get_frame_charset(p)),
            MediaType = (ContextMenuMediaType)Math.Clamp((int)p->get_media_type(p), 0, (int)ContextMenuMediaType.Plugin),
            HasImageContents = p->has_image_contents(p) != 0,
            MediaFlags = ContextMenuMediaFlags.From(p->get_media_state_flags(p)),
            SelectionText = CefString.ReadUserFree(p->get_selection_text(p)),
            TitleText = CefString.ReadUserFree(p->get_title_text(p)),
            IsEditable = p->is_editable(p) != 0,
            SpellcheckEnabled = p->is_spell_check_enabled(p) != 0,
            MisspelledWord = misspelled,
            DictionarySuggestions = words,
            EditFlags = new ContextMenuEditFlags(
                Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_UNDO), Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_REDO),
                Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_CUT), Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_COPY),
                Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_PASTE), Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_DELETE),
                Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_SELECT_ALL), Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_EDIT_RICHLY)),
        };
        // Raised after CEF has finished with its own (emptied) menu, so a Popup from the handler is not nested in it.
        CefThread.PostUi(() => ContextMenu?.Invoke(this, args));
    }

    /// <summary>The session the page's cookies, cache and storage belong to. Electron's <c>webContents.session</c>.</summary>
    public Session Session { get; }

    /// <summary>A mouse button went down in the page; UI thread.</summary>
    internal Action? PageMouseDown { get; set; }

    /// <summary>The page's permission prompts and media requests, decided by its session.</summary>
    internal PermissionHandler Permissions { get; }

    /// <summary>The page's downloads, reported to its session.</summary>
    internal DownloadHandler Downloads { get; }

    /// <summary>The settings this page was created with; a page it opens inherits them.</summary>
    internal WebPreferences Preferences => _preferences;

    private Func<WindowOpenDetails, WindowOpenHandlerResponse>? _windowOpenHandler;

    /// <summary>Decides what happens when the page opens a window (<c>window.open</c>, a <c>target=_blank</c> link,
    /// a shift-click). Without a handler the window opens, as a <see cref="BrowserWindow"/> with this page's web
    /// preferences, and closes with this window. Null restores that. Electron's <c>webContents.setWindowOpenHandler()</c>.</summary>
    /// <remarks>The handler runs on the UI thread while the page waits, so it must decide at once.</remarks>
    public void SetWindowOpenHandler(Func<WindowOpenDetails, WindowOpenHandlerResponse>? handler) => _windowOpenHandler = handler;

    /// <summary>A window this page opened was created. Electron's <c>did-create-window</c>.</summary>
    public event EventHandler<DidCreateWindowEventArgs>? DidCreateWindow;

    internal WindowOpenHandlerResponse DecideWindowOpen(WindowOpenDetails details)
        => _windowOpenHandler is { } handler ? handler(details) ?? WindowOpenHandlerResponse.Deny : WindowOpenHandlerResponse.Allow();

    internal void RaiseDidCreateWindow(DidCreateWindowEventArgs args) => DidCreateWindow?.Invoke(this, args);

    /// <summary>A page opened this one: its first document is real, with no about:blank start to hide.</summary>
    internal void MarkOpenedByPage() => _navigated = _openedByPage = true;

    private bool _openedByPage;

    /// <summary>Opens DevTools in their own window; does nothing when <see cref="WebPreferences.DevTools"/> is
    /// false. Electron's <c>webContents.openDevTools()</c>; docked modes are not supported.</summary>
    public void OpenDevTools() => WithHost(host =>
    {
        if (!_preferences.DevTools) return;
        var windowInfo = cef_window_info_native_t.Empty();
        var settings = new cef_browser_settings_t { size = (nuint)sizeof(cef_browser_settings_t) };
        host->show_dev_tools(host, (cef_window_info_t*)&windowInfo, null, &settings, null);
    });

    /// <summary>Sends a key event to the page as if typed, including to the browser's own shortcuts.
    /// Electron's <c>webContents.sendInputEvent()</c>; mouse and wheel events are not supported yet.</summary>
    /// <remarks><see cref="KeyboardInputEventType.KeyDown"/> presses a key without typing text; send
    /// <see cref="KeyboardInputEventType.Char"/> to type a character, as in Electron.</remarks>
    /// <exception cref="ArgumentException">An unknown key name, or a <c>Char</c> event that is not one character.</exception>
    public void SendInputEvent(KeyboardInputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        var isChar = inputEvent.Type == KeyboardInputEventType.Char;
        if (isChar && inputEvent.KeyCode.Length != 1) throw new ArgumentException("A Char event takes exactly one character.", nameof(inputEvent));
        var key = isChar ? default : KeyMap.Resolve(inputEvent.KeyCode, inputEvent.Modifiers.HasFlag(InputModifiers.Shift));
        var modifiers = KeyMap.DevToolsModifiers(inputEvent.Modifiers);
        CefThread.RunOnUi(() => _devTools.Send("Input.dispatchKeyEvent", w =>
        {
            // rawKeyDown: a press that types nothing; text arrives only through char events.
            w.WriteString("type", inputEvent.Type switch { KeyboardInputEventType.KeyDown => "rawKeyDown", KeyboardInputEventType.KeyUp => "keyUp", _ => "char" });
            w.WriteNumber("modifiers", modifiers);
            if (inputEvent.Modifiers.HasFlag(InputModifiers.IsAutoRepeat)) w.WriteBoolean("autoRepeat", true);
            if (inputEvent.Modifiers.HasFlag(InputModifiers.IsKeypad)) w.WriteNumber("location", 3);
            if (isChar)
            {
                w.WriteString("text", inputEvent.KeyCode);
                return;
            }
            w.WriteString("key", key.KeyValue);
            w.WriteString("code", key.Code);
            w.WriteNumber("windowsVirtualKeyCode", key.VirtualKeyCode);
            w.WriteNumber("nativeVirtualKeyCode", key.VirtualKeyCode);
        }));
    }

    /// <summary>Sends a mouse event to the page as if from the user: a right button press opens a context menu.
    /// Electron's <c>webContents.sendInputEvent()</c> for mouse events; wheel events are not supported yet.</summary>
    public void SendInputEvent(MouseInputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        var modifiers = KeyMap.DevToolsModifiers(inputEvent.Modifiers);
        switch (inputEvent.Type)
        {
            // DevTools has no enter or leave; CEF's own mouse move carries the leave flag.
            case MouseInputEventType.MouseEnter or MouseInputEventType.MouseLeave:
                WithHost(host =>
                {
                    var mouse = new cef_mouse_event_t { x = inputEvent.X, y = inputEvent.Y, modifiers = KeyMap.CefModifiers(inputEvent.Modifiers) };
                    host->send_mouse_move_event(host, &mouse, inputEvent.Type == MouseInputEventType.MouseLeave ? 1 : 0);
                });
                return;
            // A right click is what raises the page's contextmenu, as Electron's contextMenu event does.
            case MouseInputEventType.ContextMenu:
                SendInputEvent(inputEvent with { Type = MouseInputEventType.MouseDown, Button = MouseButton.Right });
                SendInputEvent(inputEvent with { Type = MouseInputEventType.MouseUp, Button = MouseButton.Right });
                return;
        }
        CefThread.RunOnUi(() => _devTools.Send("Input.dispatchMouseEvent", w =>
        {
            w.WriteString("type", inputEvent.Type switch
            {
                MouseInputEventType.MouseDown => "mousePressed",
                MouseInputEventType.MouseUp => "mouseReleased",
                MouseInputEventType.MouseWheel => "mouseWheel",
                _ => "mouseMoved",
            });
            w.WriteNumber("x", inputEvent.X);
            w.WriteNumber("y", inputEvent.Y);
            w.WriteNumber("modifiers", modifiers);
            if (inputEvent.Type == MouseInputEventType.MouseWheel)
            {
                // Chromium's wheel deltas are positive upwards and leftwards; DevTools', like the DOM's, downwards.
                w.WriteNumber("deltaX", -inputEvent.DeltaX);
                w.WriteNumber("deltaY", -inputEvent.DeltaY);
                return;
            }
            if (inputEvent.Type == MouseInputEventType.MouseMove) return;
            w.WriteString("button", inputEvent.Button switch { MouseButton.Middle => "middle", MouseButton.Right => "right", _ => "left" });
            w.WriteNumber("clickCount", inputEvent.ClickCount);
        }));
    }

    /// <summary>Closes DevTools. Electron's <c>webContents.closeDevTools()</c>.</summary>
    public void CloseDevTools() => WithHost(host => host->close_dev_tools(host));

    /// <summary>Whether DevTools are open. Electron's <c>webContents.isDevToolsOpened()</c>.</summary>
    public bool IsDevToolsOpened() => QueryHost(host => host->has_dev_tools(host) != 0, false);

    /// <summary>Opens DevTools if closed, else closes them. Electron's <c>webContents.toggleDevTools()</c>.</summary>
    public void ToggleDevTools()
    {
        if (IsDevToolsOpened()) CloseDevTools();
        else OpenDevTools();
    }

    /// <summary>Zooms the page, 1.0 for 100%; remembered per origin. Electron's <c>webContents.setZoomFactor()</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not greater than 0.</exception>
    public void SetZoomFactor(double factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        SetZoomLevel(ZoomLevelOf(factor));
    }

    /// <summary>The page's zoom, 1.0 for 100%. Electron's <c>webContents.getZoomFactor()</c>.</summary>
    public double GetZoomFactor() => Math.Pow(ZoomBase, GetZoomLevel());

    /// <summary>Zooms the page by level: 0 is 100%, each step is 20% larger or smaller; remembered per origin.
    /// Electron's <c>webContents.setZoomLevel()</c>.</summary>
    public void SetZoomLevel(double level) => WithHost(host =>
    {
        // Chromium keeps zoom per host; one the app set, to 0 included, keeps it.
        if (Uri.TryCreate(GetUrl(), UriKind.Absolute, out var url)) lock (_zoomedHosts) _zoomedHosts.Add(url.Host);
        host->set_zoom_level(host, level);
    });

    private readonly HashSet<string> _zoomedHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The page's zoom level. Electron's <c>webContents.getZoomLevel()</c>.</summary>
    public double GetZoomLevel() => QueryHost(host => host->get_zoom_level(host), 0);

    // Chromium's zoom levels are powers of 1.2.
    private const double ZoomBase = 1.2;

    internal static double ZoomLevelOf(double factor) => Math.Log(factor) / Math.Log(ZoomBase);

    // An origin the page has not zoomed yet starts at the preference's factor.
    private void ApplyDefaultZoom()
    {
        if (!_navigated || _preferences.ZoomFactor == 1.0) return;
        WithHost(host =>
        {
            if (Uri.TryCreate(GetUrl(), UriKind.Absolute, out var url)) lock (_zoomedHosts) if (_zoomedHosts.Contains(url.Host)) return;
            if (host->get_zoom_level(host) == 0) host->set_zoom_level(host, ZoomLevelOf(_preferences.ZoomFactor));
        });
    }

    /// <summary>Runs <paramref name="code"/> in the page's main world and returns its value, awaiting it if it is
    /// a promise. Electron's <c>webContents.executeJavaScript()</c>.</summary>
    /// <returns>The value as JSON; null for <c>undefined</c> or <c>null</c>.</returns>
    /// <exception cref="JavaScriptException">The code threw or its promise rejected.</exception>
    /// <exception cref="InvalidOperationException">The page has closed.</exception>
    public Task<JsonNode?> ExecuteJavaScriptAsync(string code, bool userGesture = false) => EvaluateAsync(code, userGesture, null);

    /// <summary>Evaluates <paramref name="code"/> in <paramref name="frame"/>'s main world, or the main frame's when null.</summary>
    internal Task<JsonNode?> EvaluateAsync(string code, bool userGesture, FrameState? frame)
    {
        ArgumentNullException.ThrowIfNull(code);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = _destroyed.Token.Register(() => completion.TrySetException(new InvalidOperationException("The page has closed.")));
        CefThread.RunOnUi(() =>
        {
            if (_browser is null)
            {
                completion.TrySetException(new InvalidOperationException("The page has closed."));
                return;
            }
            if (frame is { Detached: true } or { DefaultContext: null })
            {
                registration.Dispose();
                completion.TrySetException(new InvalidOperationException("The frame has no script context here: it is detached, not loaded yet, or in another process."));
                return;
            }
            _devTools.Send("Runtime.evaluate", w =>
            {
                w.WriteString("expression", code);
                if (frame?.DefaultContext is { } context) w.WriteNumber("contextId", context);
                w.WriteBoolean("awaitPromise", true);
                w.WriteBoolean("returnByValue", true);
                w.WriteBoolean("userGesture", userGesture);
            }, (result, error) =>
            {
                registration.Dispose();
                // A frame's context can go between the check above and DevTools running the script.
                if (error is not null && frame is not null && error.Contains("Cannot find context", StringComparison.Ordinal))
                    completion.TrySetException(new InvalidOperationException("The frame has no script context here: it is detached, not loaded yet, or in another process."));
                else if (error is not null) completion.TrySetException(new JavaScriptException(error));
                else if (result.TryGetProperty("exceptionDetails", out var details)) completion.TrySetException(new JavaScriptException(DescribeException(details)));
                else completion.TrySetResult(result.GetProperty("result").TryGetProperty("value", out var value) ? JsonNode.Parse(value.GetRawText()) : null);
            });
        });
        return completion.Task;
    }

    private static string DescribeException(JsonElement details)
        => details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description)
            ? description.GetString() ?? "Script error."
            : details.TryGetProperty("text", out var text) ? text.GetString() ?? "Script error." : "Script error.";

    // Host calls are only valid on the UI thread and while the browser exists.
    private void WithHost(HostAction action) => CefThread.RunOnUi(() =>
    {
        if (_browser is null) return;
        var host = _browser->get_host(_browser);
        try { action(host); }
        finally { NativeObject.Release(host); }
    });

    internal T QueryHost<T>(HostQuery<T> query, T whenClosed) => CefThread.InvokeOnUi(() =>
    {
        if (_browser is null) return whenClosed;
        var host = _browser->get_host(_browser);
        try { return query(host); }
        finally { NativeObject.Release(host); }
    });

    private delegate void HostAction(cef_browser_host_t* host);

    internal delegate T HostQuery<out T>(cef_browser_host_t* host);

    internal delegate void BrowserAction(cef_browser_t* browser);

    /// <summary>Runs <paramref name="action"/> on the UI thread while the browser exists.</summary>
    internal void WithBrowser(BrowserAction action) => CefThread.RunOnUi(() =>
    {
        if (_browser is not null) action(_browser);
    });

    /// <summary>Moves the history to the entry <paramref name="target"/> picks from the current index and the entry count;
    /// directly, through DevTools, as Electron's controller does, so it needs none of the page's script.</summary>
    internal void GoToHistoryEntry(Func<int, int, int> target) => CefThread.RunOnUi(() =>
    {
        if (_browser is null) return;
        _devTools.Send("Page.getNavigationHistory", onResult: (result, error) =>
        {
            if (error is not null) return;
            var current = result.GetProperty("currentIndex").GetInt32();
            var entries = result.GetProperty("entries");
            var index = target(current, entries.GetArrayLength());
            if (index < 0 || index >= entries.GetArrayLength() || index == current) return;
            var id = entries[index].GetProperty("id").GetInt32();
            _devTools.Send("Page.navigateToHistoryEntry", w => w.WriteNumber("entryId", id));
        });
    });

    /// <summary>Keeps only the page showing in the back/forward list.</summary>
    internal void ResetHistory() => CefThread.RunOnUi(() =>
    {
        if (_browser is not null) _devTools.Send("Page.resetNavigationHistory");
    });

    /// <summary>The page's back/forward list. Electron's <c>webContents.navigationHistory</c>.</summary>
    public NavigationHistory NavigationHistory => _navigationHistory ??= new NavigationHistory(this);

    private NavigationHistory? _navigationHistory;

    // The about:blank a window starts on would otherwise stay in its history, behind the first real page.
    private bool _bootstrapForgotten;

    /// <summary>The page's title. Electron's <c>webContents.getTitle()</c>.</summary>
    public string GetTitle() => CefThread.InvokeOnUi(() => _title);

    /// <summary>The main frame's URL. Electron's <c>webContents.getURL()</c>.</summary>
    public string GetUrl() => CefThread.InvokeOnUi(() =>
    {
        if (_browser is null || !_navigated) return string.Empty;
        var frame = _browser->get_main_frame(_browser);
        var url = CefString.ReadUserFree(frame->get_url(frame));
        NativeObject.Release(frame);
        return url;
    });

    /// <summary>Downloads <paramref name="url"/> without navigating; the session's <see cref="Session.WillDownload"/> is
    /// raised for it. Electron's <c>webContents.downloadURL()</c>.</summary>
    public void DownloadUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        WithHost(host =>
        {
            cef_string_utf16_t native = default;
            CefString.Set(&native, url);
            host->start_download(host, &native);
            CefString.Clear(&native);
        });
    }

    /// <summary>Replaces the misspelled word at the caret with <paramref name="text"/>. Electron's
    /// <c>webContents.replaceMisspelling()</c>.</summary>
    public void ReplaceMisspelling(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WithHost(host =>
        {
            cef_string_utf16_t native = default;
            CefString.Set(&native, text);
            host->replace_misspelling(host, &native);
            CefString.Clear(&native);
        });
    }

    internal void AddWordToDictionary(string word) => WithHost(host =>
    {
        cef_string_utf16_t native = default;
        CefString.Set(&native, word);
        host->add_word_to_dictionary(host, &native);
        CefString.Clear(&native);
    });

    /// <summary>Reloads the page. Electron's <c>webContents.reload()</c>.</summary>
    public void Reload() => CefThread.RunOnUi(() =>
    {
        if (_browser is null || !_navigated) return;
        _reloadRequested = true;
        _browser->reload(_browser);
    });

    /// <summary>Reloads the page, bypassing the cache. Electron's <c>webContents.reloadIgnoringCache()</c>.</summary>
    public void ReloadIgnoringCache() => CefThread.RunOnUi(() =>
    {
        if (_browser is null || !_navigated) return;
        _reloadRequested = true;
        _browser->reload_ignore_cache(_browser);
    });

    /// <summary>Undoes the last edit in the focused frame. Electron's <c>webContents.undo()</c>.</summary>
    public void Undo() => WithFocusedFrame(f => f->undo(f));

    /// <summary>Redoes the last undone edit in the focused frame. Electron's <c>webContents.redo()</c>.</summary>
    public void Redo() => WithFocusedFrame(f => f->redo(f));

    /// <summary>Cuts the focused frame's selection. Electron's <c>webContents.cut()</c>.</summary>
    public void Cut() => WithFocusedFrame(f => f->cut(f));

    /// <summary>Copies the focused frame's selection. Electron's <c>webContents.copy()</c>.</summary>
    public void Copy() => WithFocusedFrame(f => f->copy(f));

    /// <summary>Pastes into the focused frame. Electron's <c>webContents.paste()</c>.</summary>
    public void Paste() => WithFocusedFrame(f => f->paste(f));

    /// <summary>Pastes as plain text into the focused frame. Electron's <c>webContents.pasteAndMatchStyle()</c>.</summary>
    public void PasteAndMatchStyle() => WithFocusedFrame(f => f->paste_and_match_style(f));

    /// <summary>Deletes the focused frame's selection. Electron's <c>webContents.delete()</c>.</summary>
    public void Delete() => WithFocusedFrame(f => f->del(f));

    /// <summary>Selects everything in the focused frame. Electron's <c>webContents.selectAll()</c>.</summary>
    public void SelectAll() => WithFocusedFrame(f => f->select_all(f));

    private void WithFocusedFrame(FrameAction action) => CefThread.RunOnUi(() =>
    {
        if (_browser is null) return;
        var frame = _browser->get_focused_frame(_browser);
        if (frame is null) return;
        try { action(frame); }
        finally { NativeObject.Release(frame); }
    });

    private delegate void FrameAction(cef_frame_t* frame);

    /// <summary>Loads a local file. Electron's <c>webContents.loadFile()</c>.</summary>
    /// <param name="path">Absolute, or relative to the current directory.</param>
    /// <param name="query">Query parameters, encoded into the URL.</param>
    /// <param name="hash">Fragment, without the leading <c>#</c>.</param>
    /// <param name="search">A raw query string, used as is; it replaces <paramref name="query"/>, as in Electron.</param>
    public Task LoadFile(string path, IReadOnlyDictionary<string, string>? query = null, string? hash = null, string? search = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var builder = new UriBuilder(new Uri(Path.GetFullPath(path)));
        if (search is not null) builder.Query = search.TrimStart('?');
        else if (query is { Count: > 0 })
            builder.Query = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        if (!string.IsNullOrEmpty(hash)) builder.Fragment = hash;
        return LoadUrl(builder.Uri.AbsoluteUri);
    }

    private void Raise(EventHandler? handler)
    {
        if (_navigated) handler?.Invoke(this, EventArgs.Empty);
    }

    private void OnTitleChanged(string title)
    {
        if (!_navigated) return;
        _title = title;
        var args = new PageTitleUpdatedEventArgs(title);
        PageTitleUpdated?.Invoke(this, args);
        if (!args.Cancel) TitleApplied?.Invoke(title);
    }

    /// <summary>The page title, unless a <see cref="PageTitleUpdated"/> handler cancelled it; the window applies it.</summary>
    internal Action<string>? TitleApplied { get; set; }

    /// <summary>Navigates to <paramref name="url"/>; the task completes when the page has loaded and fails with a
    /// <see cref="LoadFailedException"/> if it does not (ERR_ABORTED when a newer load replaces it).
    /// Electron's <c>webContents.loadURL()</c>.</summary>
    /// <param name="url">The URL, with its scheme.</param>
    /// <param name="options">Referrer, user agent, extra headers and body for this navigation.</param>
    /// <exception cref="NotSupportedException"><see cref="LoadUrlOptions.BaseUrlForDataUrl"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An <see cref="UploadFile"/> with a negative offset or length.</exception>
    public Task LoadUrl(string url, LoadUrlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        var load = PendingLoad.Create(url, options);
        var userAgent = options?.UserAgent;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Chromium fails the upload when a file changed since its modificationTime.
        if (options?.PostData?.OfType<UploadFile>().Any(f => f.HasChanged()) == true)
        {
            CefThread.RunOnUi(() => DidFailLoad?.Invoke(this, new LoadFailedEventArgs(UploadFileChangedError, "ERR_UPLOAD_FILE_CHANGED", url, true)));
            done.SetException(new LoadFailedException(UploadFileChangedError, "ERR_UPLOAD_FILE_CHANGED", url));
            return done.Task;
        }
        CefThread.RunOnUi(() =>
        {
            CompleteLoad(new LoadFailedException(AbortedError, "ERR_ABORTED", url));
            _loadDone = done;
            if (userAgent is not null) ApplyUserAgent(userAgent);
            Navigate(url, load);
        });
        return done.Task;
    }

    // net::ERR_ABORTED: a navigation stopped, typically because a newer one replaced it.
    private const int AbortedError = -3;

    // net::ERR_BLOCKED_BY_CLIENT.
    private const int BlockedByClientError = -20;

    // The main-frame URL a webRequest listener last cancelled; set off the UI thread.
    private string? _blockedNavigation;

    private Task<RequestDecision>? WatchBlocked(RequestInfo request, Task<RequestDecision>? decision)
    {
        if (decision is null || request.ResourceType != cef_resource_type_t.RT_MAIN_FRAME) return decision;
        return decision.ContinueWith(t =>
        {
            if (t.Result.Cancel) Volatile.Write(ref _blockedNavigation, request.Url);
            return t.Result;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    // net::ERR_UPLOAD_FILE_CHANGED.
    private const int UploadFileChangedError = -14;

    // The task of the latest LoadUrl, until its page finishes or fails; UI thread only.
    private TaskCompletionSource? _loadDone;

    /// <summary>Whether loading <paramref name="target"/> from <paramref name="current"/> only moves to a fragment,
    /// keeping the document: the same URL apart from a fragment, which the target has.</summary>
    internal static bool IsSameDocument(string current, string target)
        => Uri.TryCreate(current, UriKind.Absolute, out var from) && Uri.TryCreate(target, UriKind.Absolute, out var to)
            && to.Fragment.Length > 0 && from.GetLeftPart(UriPartial.Query) == to.GetLeftPart(UriPartial.Query);

    private void CompleteLoad(LoadFailedException? failure)
    {
        if (_loadDone is not { } done) return;
        _loadDone = null;
        if (failure is null) done.TrySetResult();
        else done.TrySetException(failure);
    }

    /// <summary>Overrides the user agent for this page: request headers and <c>navigator.userAgent</c>, for
    /// every later navigation. An empty string restores the default. Electron's <c>webContents.setUserAgent()</c>.</summary>
    public void SetUserAgent(string userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        CefThread.RunOnUi(() => ApplyUserAgent(userAgent));
    }

    /// <summary>The page's user agent: the override, else the app's fallback when the page was created.
    /// Electron's <c>webContents.getUserAgent()</c>.</summary>
    public string GetUserAgent() => CefThread.InvokeOnUi(() => _userAgent is { Length: > 0 } ua ? ua : _defaultUserAgent ?? string.Empty);

    private void ApplyUserAgent(string userAgent)
    {
        _userAgent = userAgent;
        SendUserAgent();
    }

    private string? _acceptLanguages;

    /// <summary>UI thread: the page's session changed its user agent; the page's own override still wins.</summary>
    // Oriel's runtime defines `oriel` first; then the session's preloads and the app's, each in its own scope, as Electron runs them.
    private string IsolatedScript()
    {
        var script = new System.Text.StringBuilder(s_preloadRuntime);
        foreach (var preload in Session.GetPreloadScripts())
        {
            string? source;
            try { source = File.ReadAllText(preload.FilePath); }
            catch (IOException) { source = null; }
            catch (UnauthorizedAccessException) { source = null; }
            script.Append(source is null
                ? $"\n;console.error({JsonValue.Create("Unable to load preload script: " + preload.FilePath).ToJsonString()});\n"
                : $"\n;(() => {{\n{source}\n}})();\n");
        }
        if (_appPreload is not null) script.Append($"\n;(() => {{\n{_appPreload}\n}})();\n");
        return script.ToString();
    }

    private void AddIsolatedScript() => _devTools.Send("Page.addScriptToEvaluateOnNewDocument", w =>
    {
        w.WriteString("source", IsolatedScript());
        w.WriteString("worldName", IsolatedWorld);
    }, onResult: (result, _) => _isolatedScriptId = result.TryGetProperty("identifier", out var id) ? id.GetString() : null);

    /// <summary>UI thread: the session's preloads changed; the page's next load runs the new set.</summary>
    internal void OnSessionPreloadsChanged()
    {
        if (_browser is null) return;
        if (_isolatedScriptId is { } previous) _devTools.Send("Page.removeScriptToEvaluateOnNewDocument", w => w.WriteString("identifier", previous));
        AddIsolatedScript();
    }

    internal void OnSessionUserAgent(string userAgent, string? acceptLanguages)
    {
        _defaultUserAgent = userAgent;
        _acceptLanguages = acceptLanguages;
        SendUserAgent();
    }

    // The page's override, else its session's, else the app's fallback; CEF started with the fallback.
    private void SendUserAgent()
    {
        var userAgent = _userAgent is { Length: > 0 } own ? own : _defaultUserAgent ?? "";
        var differs = userAgent != OrielApp.ProcessUserAgent || _acceptLanguages is not null;
        // Emulation's override stays for the DevTools session, across navigations and renderer swaps; an empty one clears it.
        _devTools.Send("Emulation.setUserAgentOverride", w =>
        {
            w.WriteString("userAgent", differs ? userAgent : "");
            if (_acceptLanguages is { } languages) w.WriteString("acceptLanguage", languages);
        });
    }

    private void Navigate(string url, PendingLoad? load)
    {
        // Until the preload is registered, a navigation would run the page without it.
        if (!_ready)
        {
            _queuedLoad = (url, load);
            return;
        }

        // Set even when null: a plain load must not inherit an earlier load's options.
        lock (_pendingLoadGate) _pendingLoad = load;
        _navigated = true;
        var frame = _browser->get_main_frame(_browser);
        // Only the fragment changes: the page stays, and no load end follows.
        if (IsSameDocument(CefString.ReadUserFree(frame->get_url(frame)), url) && _loadDone is { } current)
            CefThread.PostUi(() => { if (_loadDone == current) CompleteLoad(null); });
        cef_string_utf16_t value = default;
        CefString.Set(&value, url);
        frame->load_url(frame, &value);
        CefString.Clear(&value);
        NativeObject.Release(frame);
    }

    // IO thread: only the main-frame navigation a LoadUrl with options started needs the hop.
    private bool WantsRequest(cef_frame_t* frame, cef_request_t* request, bool isNavigation)
        // Navigations always come here, for loadURL options and the status did-navigate reports.
        => isNavigation || Session.WebRequest.HasListeners;

    private void OnBeforeResourceLoad(cef_frame_t* frame, cef_request_t* request)
    {
        // With webRequest listeners every request comes here; loadURL options are for the main frame's navigation only.
        if (frame is null || frame->is_main(frame) == 0 || request->get_resource_type(request) != cef_resource_type_t.RT_MAIN_FRAME) return;
        var url = CefString.ReadUserFree(request->get_url(request));
        PendingLoad? load;
        lock (_pendingLoadGate)
        {
            if (_pendingLoad is not { } pending || !pending.Matches(url)) return;
            load = pending;
            _pendingLoad = null;
        }
        load.ApplyTo(request);
    }

    /// <summary>Sends a message to the page's <c>ipcRenderer.on(channel)</c> listeners. Electron's <c>webContents.send()</c>.</summary>
    /// <remarks>Messages sent before the page's preload has run are delivered once it has.</remarks>
    public void Send(string channel, params JsonNode?[] args)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var message = new JsonObject { ["t"] = "msg", ["ch"] = channel, ["args"] = new JsonArray(args) }.ToJsonString();
        CefThread.RunOnUi(() =>
        {
            if (_isolatedContext is { } context) Deliver(context, message);
            else _pendingDeliveries.Add(message);
        });
    }

    internal cef_browser_t* Browser => _browser;

    internal bool HasBrowser => _browser is not null;

    internal void OnBrowserCreated(cef_browser_t* browser)
    {
        NativeObject.AddRef(browser);
        _browser = browser;
        _devTools.Attach(browser);

        _devTools.Send("Page.enable");
        // Per-frame DOMContentLoaded for dom-ready.
        _devTools.Send("Page.setLifecycleEventsEnabled", w => w.WriteBoolean("enabled", true));
        // A session's user agent, or a fallback changed after start-up, applies from the first page, as Electron's.
        if (_userAgent is not null || _defaultUserAgent != OrielApp.ProcessUserAgent || _acceptLanguages is not null) SendUserAgent();
        _devTools.Send("Runtime.enable");
        // Chrome-style browsers ignore the font sizes in the browser settings; this sets them for this page only.
        if (_preferences.DefaultFontSize is not null || _preferences.DefaultMonospaceFontSize is not null)
        {
            _devTools.Send("Page.setFontSizes", w =>
            {
                w.WriteStartObject("fontSizes");
                if (_preferences.DefaultFontSize is { } standard) w.WriteNumber("standard", standard);
                if (_preferences.DefaultMonospaceFontSize is { } fixedSize) w.WriteNumber("fixed", fixedSize);
                w.WriteEndObject();
            });
        }
        // The browser settings have no math family; DevTools sets it for this page.
        if (_preferences.DefaultFontFamily?.Math is { } math)
            _devTools.Send("Page.setFontFamilies", w =>
            {
                w.WriteStartObject("fontFamilies");
                w.WriteString("math", math);
                w.WriteEndObject();
            });
        // Scoped to the isolated world: the page's own scripts never see the binding.
        _devTools.Send("Runtime.addBinding", w =>
        {
            w.WriteString("name", Binding);
            w.WriteString("executionContextName", IsolatedWorld);
        });
        // Main world first: its listeners must exist when the isolated preload exposes APIs.
        _devTools.Send("Page.addScriptToEvaluateOnNewDocument", w => w.WriteString("source", s_mainWorldStub));
        AddIsolatedScript();
        _devTools.Send("Page.getFrameTree", onResult: (result, _) =>
        {
            var frame = result.GetProperty("frameTree").GetProperty("frame");
            _mainFrameId = frame.GetProperty("id").GetString();
            AddFrameTree(result.GetProperty("frameTree"));
            _ready = true;
            // A page-opened window starts on its real page, which can commit before the scripts above are registered
            // and would then run without Oriel's runtime or the preload. Reloading keeps window.opener.
            if (_openedByPage && frame.GetProperty("url").GetString() is { Length: > 0 } url && url != "about:blank")
                _devTools.Send("Page.reload");
            if (_queuedLoad is { } queued)
            {
                _queuedLoad = null;
                Navigate(queued.Url, queued.Load);
            }
        });
    }

    /// <summary>Cancelled when the page's browser is destroyed.</summary>
    internal CancellationToken DestroyedToken => _destroyed.Token;

    internal void OnBrowserDestroyed()
    {
        _destroyed.Cancel();
        UnregisterLifecycle();
        _devTools.Detach();
        NativeObject.Release(_browser);
        _browser = null;
        _ready = false;
        _isolatedContext = null;
        foreach (var frame in _frames.Values) frame.Detached = true;
        _frames.Clear();
        _contextFrames.Clear();
    }

    private void OnDevToolsEvent(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "Runtime.bindingCalled" when parameters.GetProperty("name").GetString() == Binding:
                OnIpcMessage(parameters.GetProperty("executionContextId").GetInt32(), parameters.GetProperty("payload").GetString()!);
                break;

            case "Page.frameAttached":
                var attached = parameters.GetProperty("frameId").GetString()!;
                if (!_frames.ContainsKey(attached))
                    _frames[attached] = new FrameState { Id = attached, ParentId = parameters.GetProperty("parentFrameId").GetString() };
                break;

            case "Page.lifecycleEvent" when parameters.GetProperty("name").GetString() == "DOMContentLoaded":
                var readyId = parameters.GetProperty("frameId").GetString()!;
                if (_frames.TryGetValue(readyId, out var ready)) ready.Wrapper?.RaiseDomReady();
                if (readyId == _mainFrameId && _navigated) DomReady?.Invoke(this, EventArgs.Empty);
                break;

            case "Page.frameNavigated":
                DetachDescendants(parameters.GetProperty("frame").GetProperty("id").GetString()!);
                UpdateFrame(parameters.GetProperty("frame"));
                OnFrameCommitted(parameters.GetProperty("frame"));
                break;

            case "Page.navigatedWithinDocument":
                OnNavigatedWithinDocument(parameters);
                break;

            // A swap moves the frame to another process, where it still exists; only a removal ends it.
            case "Page.frameDetached" when parameters.TryGetProperty("reason", out var reason) && reason.GetString() == "remove":
                if (_frames.Remove(parameters.GetProperty("frameId").GetString()!, out var detached)) detached.Detached = true;
                break;

            case "Runtime.executionContextCreated":
                var context = parameters.GetProperty("context");
                TrackContext(context);
                if (context.GetProperty("name").GetString() == IsolatedWorld &&
                    context.GetProperty("auxData").GetProperty("frameId").GetString() == _mainFrameId)
                {
                    _isolatedContext = context.GetProperty("id").GetInt32();
                    foreach (var pending in _pendingDeliveries) Deliver(_isolatedContext.Value, pending);
                    _pendingDeliveries.Clear();
                }
                break;

            case "Runtime.executionContextDestroyed":
                var gone = parameters.GetProperty("executionContextId").GetInt32();
                if (gone == _isolatedContext) _isolatedContext = null;
                PortLink.ContextGone(this, gone);
                if (_contextFrames.Remove(gone, out var owner) && _frames.TryGetValue(owner, out var ownerFrame))
                {
                    if (ownerFrame.DefaultContext == gone) ownerFrame.DefaultContext = null;
                    if (ownerFrame.IsolatedContext == gone) ownerFrame.IsolatedContext = null;
                }
                break;

            case "Runtime.executionContextsCleared":
                foreach (var cleared in _contextFrames.Keys) PortLink.ContextGone(this, cleared);
                _isolatedContext = null;
                _contextFrames.Clear();
                foreach (var frame in _frames.Values) frame.DefaultContext = frame.IsolatedContext = null;
                break;
        }
    }

    private void OnIpcMessage(int context, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var channel = root.GetProperty("ch").GetString()!;
        var args = root.GetProperty("args").EnumerateArray().Select(a => a.Clone()).ToArray();

        switch (root.GetProperty("t").GetString())
        {
            case "send":
                IpcMain.DispatchSend(this, FrameForContext(context), channel, args);
                break;

            case "post" when _contextFrames.TryGetValue(context, out var postFrameId) && _frames.TryGetValue(postFrameId, out var postFrame):
                var made = PortIds(root).Select(id => PortLink.FromPage(this, postFrame, context, id)).ToList();
                IpcMain.DispatchSend(this, WrapperOf(postFrame), channel, args, made);
                break;

            case "port-msg" when PortLink.ForRemote(root.GetProperty("id").GetString()!) is { } link:
                var portId = root.GetProperty("id").GetString()!;
                if (link.RemoteEnd(portId) is { } origin)
                    link.FromPageEnd(portId, root.GetProperty("data"), [.. PortIds(root).Select(id => (id, context))], origin);
                break;

            case "port-close" when PortLink.ForRemote(root.GetProperty("id").GetString()!) is { } closing:
                closing.Close(root.GetProperty("id").GetString()!);
                break;

            case "invoke":
                var id = root.GetProperty("id").GetInt32();
                _ = ReplyAsync(context, id, channel, args);
                break;

            case "mousedown":
                PageMouseDown?.Invoke();
                break;

            case "focus":
                if (_contextFrames.TryGetValue(context, out var frameId)) _focusedFrameId = frameId;
                break;
        }
    }

    // ContinueWith rather than await: an unsafe class cannot contain async methods.
    private Task ReplyAsync(int context, int id, string channel, JsonElement[] args)
        => IpcMain.DispatchInvokeAsync(this, FrameForContext(context), channel, args).ContinueWith(
            completed =>
            {
                var (ok, value, error) = completed.Result;
                var reply = new JsonObject { ["t"] = "reply", ["id"] = id, ["ok"] = ok, ["value"] = value, ["error"] = error }.ToJsonString();
                CefThread.RunOnUi(() => Deliver(context, reply));
            },
            TaskScheduler.Default);

    // The page's frames by DevTools frame id, which (unlike CEF's frame identifiers) is what DevTools events and
    // execution contexts name; UI thread only.
    private readonly Dictionary<string, FrameState> _frames = [];
    private readonly Dictionary<int, string> _contextFrames = [];
    private string? _focusedFrameId;

    /// <summary>The frame that has focus in the page, or null. Electron's <c>webContents.focusedFrame</c>.</summary>
    /// <remarks>A cross-site iframe in another process is not seen taking focus (drift F1).</remarks>
    public WebFrameMain? FocusedFrame => CefThread.InvokeOnUi(()
        => _focusedFrameId is { } id && _frames.TryGetValue(id, out var frame) ? WrapperOf(frame) : null);

    /// <summary>The main frame's document has been parsed: its DOM is ready, though images may still load.
    /// Electron's <c>dom-ready</c>.</summary>
    public event EventHandler? DomReady;

    /// <summary>The top frame of the page. Electron's <c>webContents.mainFrame</c>; null until the page exists.</summary>
    public WebFrameMain? MainFrame => CefThread.InvokeOnUi(() => _mainFrameId is { } id && _frames.TryGetValue(id, out var frame) ? WrapperOf(frame) : null);

    internal WebFrameMain WrapperOf(FrameState frame) => frame.Wrapper ??= new WebFrameMain(this, frame);

    internal WebFrameMain? ParentOf(FrameState frame)
        => CefThread.InvokeOnUi(() => frame.ParentId is { } parent && _frames.TryGetValue(parent, out var p) ? WrapperOf(p) : null);

    internal IReadOnlyList<WebFrameMain> ChildrenOf(FrameState frame)
        => CefThread.InvokeOnUi(() => (IReadOnlyList<WebFrameMain>)[.. _frames.Values.Where(f => f.ParentId == frame.Id && !f.Detached).Select(WrapperOf)]);

    internal T ReadFrame<T>(Func<T> read) => CefThread.InvokeOnUi(read);

    /// <summary>Sends to the preload of one frame; ignored while it has none (not loaded, or in another process).</summary>
    internal void SendToFrame(FrameState frame, string message) => CefThread.RunOnUi(() =>
    {
        if (frame.IsolatedContext is { } context) Deliver(context, message);
    });

    private WebFrameMain? FrameForContext(int context)
        => _contextFrames.TryGetValue(context, out var id) && _frames.TryGetValue(id, out var frame) ? WrapperOf(frame) : null;

    // A frame's new document starts without the old one's iframes; DevTools reports no detach for them.
    private void DetachDescendants(string frameId)
    {
        foreach (var child in _frames.Values.Where(f => f.ParentId == frameId).ToList())
        {
            DetachDescendants(child.Id);
            child.Detached = true;
            _frames.Remove(child.Id);
        }
    }

    private void AddFrameTree(JsonElement node)
    {
        UpdateFrame(node.GetProperty("frame"));
        if (node.TryGetProperty("childFrames", out var children))
            foreach (var child in children.EnumerateArray()) AddFrameTree(child);
    }

    private void UpdateFrame(JsonElement frame)
    {
        var id = frame.GetProperty("id").GetString()!;
        if (!_frames.TryGetValue(id, out var state)) _frames[id] = state = new FrameState { Id = id };
        state.ParentId = frame.TryGetProperty("parentId", out var parent) ? parent.GetString() : null;
        state.Url = frame.GetProperty("url").GetString() + (frame.TryGetProperty("urlFragment", out var fragment) ? fragment.GetString() : "");
        state.Name = frame.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
        state.Origin = frame.TryGetProperty("securityOrigin", out var origin) ? origin.GetString() ?? string.Empty : string.Empty;
    }

    private void TrackContext(JsonElement context)
    {
        if (!context.TryGetProperty("auxData", out var aux) || !aux.TryGetProperty("frameId", out var frameId)) return;
        var id = frameId.GetString()!;
        var contextId = context.GetProperty("id").GetInt32();
        _contextFrames[contextId] = id;
        if (!_frames.TryGetValue(id, out var frame)) _frames[id] = frame = new FrameState { Id = id };
        if (aux.TryGetProperty("isDefault", out var isDefault) && isDefault.GetBoolean()) frame.DefaultContext = contextId;
        else if (context.GetProperty("name").GetString() == IsolatedWorld) frame.IsolatedContext = contextId;
    }

    // Runs in the isolated world's context, so only Oriel's preload runtime can receive it.
    private static IEnumerable<string> PortIds(JsonElement root)
        => root.TryGetProperty("ports", out var ports) ? ports.EnumerateArray().Select(p => p.GetString()!) : [];

    /// <summary>Sends <paramref name="message"/> to the main frame with ports moved into it. Electron's
    /// <c>webContents.postMessage()</c>.</summary>
    /// <exception cref="InvalidOperationException">A port was already moved.</exception>
    public void PostMessage(string channel, JsonNode? message, IReadOnlyList<MessagePortMain>? transfer = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var ports = PortLink.CheckTransfer(transfer, null);
        CefThread.RunOnUi(() =>
        {
            if (_mainFrameId is { } id && _frames.TryGetValue(id, out var frame)) PostToFrame(frame, channel, message, ports);
            else foreach (var port in ports) port.Close();
        });
    }

    // UI thread.
    internal void PostToFrame(FrameState frame, string channel, JsonNode? message, IReadOnlyList<MessagePortMain> ports)
    {
        var ids = ports.Select(p => PortLink.MoveToPage(p, this, frame)).ToList();
        var envelope = new JsonObject { ["t"] = "msg", ["ch"] = channel, ["args"] = new JsonArray(message?.DeepClone()), ["ports"] = new JsonArray([.. ids.Select(i => (JsonNode?)i)]) };
        DeliverTo(frame, envelope.ToJsonString(), null);
    }

    // UI thread: a port message for the page's end; a document that has gone takes the port with it.
    internal void DeliverPort(RemoteEnd end, JsonObject message)
    {
        if (end.Context is { } known && end.Frame.IsolatedContext != known)
        {
            PortLink.ForRemote(end.Id)?.Close(end);
            return;
        }
        DeliverTo(end.Frame, message.ToJsonString(), end);
    }

    // The main frame's messages wait for its preload; another frame without one gets nothing.
    private void DeliverTo(FrameState frame, string message, RemoteEnd? end)
    {
        if (frame.IsolatedContext is { } context)
        {
            if (end is not null) end.Context ??= context;
            Deliver(context, message);
        }
        else if (frame.Id == _mainFrameId) _pendingDeliveries.Add(message);
    }

    private void Deliver(int context, string message)
        => _devTools.Send("Runtime.evaluate", w =>
        {
            w.WriteString("expression", $"__orielDeliver({message})");
            w.WriteNumber("contextId", context);
        });

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Arguments for <see cref="WebContents.PageTitleUpdated"/>; cancel to keep the window's title.</summary>
public sealed class PageTitleUpdatedEventArgs(string title) : CancelEventArgs
{
    /// <summary>The page's new title.</summary>
    public string Title { get; } = title;
}

/// <summary>Script run by <see cref="WebContents.ExecuteJavaScriptAsync"/> threw, or its promise rejected.</summary>
public sealed class JavaScriptException(string message) : Exception(message);

/// <summary>A page failed to load; what <see cref="WebContents.LoadUrl"/>'s task fails with. Electron rejects
/// <c>loadURL</c>'s promise with these details.</summary>
/// <param name="errorCode">Chromium's net error code (negative), e.g. -6 for a missing file.</param>
/// <param name="errorDescription">Chromium's description of the error.</param>
/// <param name="validatedUrl">The URL that failed.</param>
public sealed class LoadFailedException(int errorCode, string errorDescription, string validatedUrl)
    : Exception($"{errorDescription} ({errorCode}) loading '{validatedUrl}'")
{
    /// <summary>Chromium's net error code (negative).</summary>
    public int ErrorCode { get; } = errorCode;

    /// <summary>Chromium's description of the error.</summary>
    public string ErrorDescription { get; } = errorDescription;

    /// <summary>The URL that failed.</summary>
    public string ValidatedUrl { get; } = validatedUrl;
}

/// <summary>Arguments for <see cref="WebContents.DidFailLoad"/>; Electron's <c>did-fail-load</c> parameters.</summary>
/// <param name="ErrorCode">Chromium's net error code (negative).</param>
/// <param name="ErrorDescription">Chromium's description of the error.</param>
/// <param name="ValidatedUrl">The URL that failed.</param>
/// <param name="IsMainFrame">Whether the main frame failed, rather than a subframe.</param>
public sealed record LoadFailedEventArgs(int ErrorCode, string ErrorDescription, string ValidatedUrl, bool IsMainFrame);
