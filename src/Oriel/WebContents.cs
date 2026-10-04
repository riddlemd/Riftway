using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>The page inside a window: Electron's <c>webContents</c>.</summary>
public sealed unsafe class WebContents
{
    private const string IsolatedWorld = "oriel";
    private const string Binding = "__orielIpc";

    private static readonly string s_preloadRuntime = ReadResource("Oriel.Ipc.preload.js");
    private static readonly string s_mainWorldStub = ReadResource("Oriel.Ipc.main-world.js");

    private readonly string _isolatedScript;
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

    internal WebContents(string? appPreload, WebPreferences preferences)
    {
        _preferences = preferences;
        JsDialog = new JsDialogHandler { SuppressAll = preferences.DisableDialogs };
        // Chrome's own shortcut (F12) and the context menu's Inspect open DevTools without OpenDevTools.
        if (!preferences.DevTools)
            Command = new CommandHandler(["IDC_DEV_TOOLS", "IDC_DEV_TOOLS_CONSOLE", "IDC_DEV_TOOLS_DEVICES", "IDC_DEV_TOOLS_INSPECT",
                "IDC_DEV_TOOLS_TOGGLE", "IDC_CONTENT_CONTEXT_INSPECTELEMENT", "IDC_CONTENT_CONTEXT_INSPECTBACKGROUNDPAGE"]);
        // The app's preload gets its own scope, after Oriel's runtime has defined `oriel`.
        _isolatedScript = appPreload is null
            ? s_preloadRuntime
            : $"{s_preloadRuntime}\n;(() => {{\n{appPreload}\n}})();\n";
        _devTools.EventReceived += OnDevToolsEvent;

        Display = new DisplayHandler { TitleChanged = OnTitleChanged };
        ContextMenuRequests = new ContextMenuHandler(OnContextMenu);
        Load = new LoadHandler
        {
            LoadStarted = isMain => { if (isMain) ApplyDefaultZoom(); },
            LoadingStateChanged = loading => Raise(loading ? DidStartLoading : DidStopLoading),
            LoadEnded = (isMain, _) => { if (isMain) Raise(DidFinishLoad); },
            LoadFailed = (isMain, code, text, url) =>
            {
                if (_navigated) DidFailLoad?.Invoke(this, new LoadFailedEventArgs(code, text, url, isMain));
            },
        };
        Request = new RequestHandler(new ResourceRequestHandler { BeforeResourceLoad = OnBeforeResourceLoad })
        {
            Wants = WantsRequest,
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

    /// <summary>Where the page last asked for a context menu, in page DIPs; UI thread only.</summary>
    internal (int X, int Y)? LastContextMenuPoint { get; private set; }

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
            SelectionText = CefString.ReadUserFree(p->get_selection_text(p)),
            TitleText = CefString.ReadUserFree(p->get_title_text(p)),
            IsEditable = p->is_editable(p) != 0,
            SpellcheckEnabled = p->is_spell_check_enabled(p) != 0,
            MisspelledWord = CefString.ReadUserFree(p->get_misspelled_word(p)),
            DictionarySuggestions = words,
            EditFlags = new ContextMenuEditFlags(
                Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_UNDO), Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_REDO),
                Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_CUT), Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_COPY),
                Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_PASTE), Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_DELETE),
                Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_SELECT_ALL), Has(cef_context_menu_edit_state_flags_t.CM_EDITFLAG_CAN_EDIT_RICHLY)),
        };
        LastContextMenuPoint = (args.X, args.Y);
        // Raised after CEF has finished with its own (emptied) menu, so a Popup from the handler is not nested in it.
        CefThread.PostUi(() => ContextMenu?.Invoke(this, args));
    }

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
        CefThread.RunOnUi(() => _devTools.Send("Input.dispatchMouseEvent", w =>
        {
            w.WriteString("type", inputEvent.Type switch { MouseInputEventType.MouseDown => "mousePressed", MouseInputEventType.MouseUp => "mouseReleased", _ => "mouseMoved" });
            w.WriteNumber("x", inputEvent.X);
            w.WriteNumber("y", inputEvent.Y);
            w.WriteNumber("modifiers", modifiers);
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
    public void SetZoomLevel(double level) => WithHost(host => host->set_zoom_level(host, level));

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
            if (host->get_zoom_level(host) == 0) host->set_zoom_level(host, ZoomLevelOf(_preferences.ZoomFactor));
        });
    }

    /// <summary>Runs <paramref name="code"/> in the page's main world and returns its value, awaiting it if it is
    /// a promise. Electron's <c>webContents.executeJavaScript()</c>.</summary>
    /// <returns>The value as JSON; null for <c>undefined</c> or <c>null</c>.</returns>
    /// <exception cref="JavaScriptException">The code threw or its promise rejected.</exception>
    /// <exception cref="InvalidOperationException">The page has closed.</exception>
    public Task<JsonNode?> ExecuteJavaScriptAsync(string code, bool userGesture = false)
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
            _devTools.Send("Runtime.evaluate", w =>
            {
                w.WriteString("expression", code);
                w.WriteBoolean("awaitPromise", true);
                w.WriteBoolean("returnByValue", true);
                w.WriteBoolean("userGesture", userGesture);
            }, (result, error) =>
            {
                registration.Dispose();
                if (error is not null) completion.TrySetException(new JavaScriptException(error));
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

    private T QueryHost<T>(HostQuery<T> query, T whenClosed) => CefThread.InvokeOnUi(() =>
    {
        if (_browser is null) return whenClosed;
        var host = _browser->get_host(_browser);
        try { return query(host); }
        finally { NativeObject.Release(host); }
    });

    private delegate void HostAction(cef_browser_host_t* host);

    private delegate T HostQuery<out T>(cef_browser_host_t* host);

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

    /// <summary>Reloads the page. Electron's <c>webContents.reload()</c>.</summary>
    public void Reload() => CefThread.RunOnUi(() =>
    {
        if (_browser is not null && _navigated) _browser->reload(_browser);
    });

    /// <summary>Reloads the page, bypassing the cache. Electron's <c>webContents.reloadIgnoringCache()</c>.</summary>
    public void ReloadIgnoringCache() => CefThread.RunOnUi(() =>
    {
        if (_browser is not null && _navigated) _browser->reload_ignore_cache(_browser);
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
    public void LoadFile(string path, IReadOnlyDictionary<string, string>? query = null, string? hash = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var builder = new UriBuilder(new Uri(Path.GetFullPath(path)));
        if (query is { Count: > 0 })
            builder.Query = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        if (!string.IsNullOrEmpty(hash)) builder.Fragment = hash;
        LoadUrl(builder.Uri.AbsoluteUri);
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

    /// <summary>Navigates to <paramref name="url"/>. Electron's <c>webContents.loadURL()</c>.</summary>
    /// <param name="url">The URL, with its scheme.</param>
    /// <param name="options">Referrer, user agent, extra headers and body for this navigation.</param>
    /// <exception cref="NotSupportedException"><see cref="LoadUrlOptions.BaseUrlForDataUrl"/>, or an
    /// <see cref="UploadFile"/> with an offset or length.</exception>
    public void LoadUrl(string url, LoadUrlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        var load = PendingLoad.Create(url, options);
        var userAgent = options?.UserAgent;
        CefThread.RunOnUi(() =>
        {
            if (userAgent is not null) ApplyUserAgent(userAgent);
            Navigate(url, load);
        });
    }

    /// <summary>Overrides the user agent for this page: request headers and <c>navigator.userAgent</c>, for
    /// every later navigation. An empty string restores the default. Electron's <c>webContents.setUserAgent()</c>.</summary>
    public void SetUserAgent(string userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        CefThread.RunOnUi(() => ApplyUserAgent(userAgent));
    }

    /// <summary>The page's user agent: the override, else Chromium's default (empty until DevTools reports it,
    /// just after the window is created). Electron's <c>webContents.getUserAgent()</c>.</summary>
    public string GetUserAgent() => CefThread.InvokeOnUi(() => _userAgent is { Length: > 0 } ua ? ua : _defaultUserAgent ?? string.Empty);

    private void ApplyUserAgent(string userAgent)
    {
        _userAgent = userAgent;
        // Emulation's override stays for the DevTools session, across navigations and renderer swaps.
        _devTools.Send("Emulation.setUserAgentOverride", w => w.WriteString("userAgent", userAgent));
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
        cef_string_utf16_t value = default;
        CefString.Set(&value, url);
        frame->load_url(frame, &value);
        CefString.Clear(&value);
        NativeObject.Release(frame);
    }

    // IO thread: only the main-frame navigation a LoadUrl with options started needs the hop.
    private bool WantsRequest(cef_frame_t* frame, cef_request_t* request, bool isNavigation)
    {
        if (!isNavigation || frame is null || frame->is_main(frame) == 0) return false;
        lock (_pendingLoadGate) return _pendingLoad is not null;
    }

    private void OnBeforeResourceLoad(cef_frame_t* frame, cef_request_t* request)
    {
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

    internal void OnBrowserCreated(cef_browser_t* browser)
    {
        NativeObject.AddRef(browser);
        _browser = browser;
        _devTools.Attach(browser);

        _devTools.Send("Page.enable");
        _devTools.Send("Browser.getVersion", onResult: (result, error) =>
        {
            if (error is null) _defaultUserAgent = result.GetProperty("userAgent").GetString();
        });
        if (_userAgent is { } userAgent) ApplyUserAgent(userAgent);
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
        // Scoped to the isolated world: the page's own scripts never see the binding.
        _devTools.Send("Runtime.addBinding", w =>
        {
            w.WriteString("name", Binding);
            w.WriteString("executionContextName", IsolatedWorld);
        });
        // Main world first: its listeners must exist when the isolated preload exposes APIs.
        _devTools.Send("Page.addScriptToEvaluateOnNewDocument", w => w.WriteString("source", s_mainWorldStub));
        _devTools.Send("Page.addScriptToEvaluateOnNewDocument", w =>
        {
            w.WriteString("source", _isolatedScript);
            w.WriteString("worldName", IsolatedWorld);
        });
        _devTools.Send("Page.getFrameTree", onResult: (result, _) =>
        {
            var frame = result.GetProperty("frameTree").GetProperty("frame");
            _mainFrameId = frame.GetProperty("id").GetString();
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
        _devTools.Detach();
        NativeObject.Release(_browser);
        _browser = null;
        _ready = false;
        _isolatedContext = null;
    }

    private void OnDevToolsEvent(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "Runtime.bindingCalled" when parameters.GetProperty("name").GetString() == Binding:
                OnIpcMessage(parameters.GetProperty("executionContextId").GetInt32(), parameters.GetProperty("payload").GetString()!);
                break;

            case "Runtime.executionContextCreated":
                var context = parameters.GetProperty("context");
                if (context.GetProperty("name").GetString() == IsolatedWorld &&
                    context.GetProperty("auxData").GetProperty("frameId").GetString() == _mainFrameId)
                {
                    _isolatedContext = context.GetProperty("id").GetInt32();
                    foreach (var pending in _pendingDeliveries) Deliver(_isolatedContext.Value, pending);
                    _pendingDeliveries.Clear();
                }
                break;

            case "Runtime.executionContextDestroyed" when parameters.GetProperty("executionContextId").GetInt32() == _isolatedContext:
            case "Runtime.executionContextsCleared":
                _isolatedContext = null;
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
                IpcMain.DispatchSend(this, channel, args);
                break;

            case "invoke":
                var id = root.GetProperty("id").GetInt32();
                _ = ReplyAsync(context, id, channel, args);
                break;
        }
    }

    // ContinueWith rather than await: an unsafe class cannot contain async methods.
    private Task ReplyAsync(int context, int id, string channel, JsonElement[] args)
        => IpcMain.DispatchInvokeAsync(this, channel, args).ContinueWith(
            completed =>
            {
                var (ok, value, error) = completed.Result;
                var reply = new JsonObject { ["t"] = "reply", ["id"] = id, ["ok"] = ok, ["value"] = value, ["error"] = error }.ToJsonString();
                CefThread.RunOnUi(() => Deliver(context, reply));
            },
            TaskScheduler.Default);

    // Runs in the isolated world's context, so only Oriel's preload runtime can receive it.
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

/// <summary>Arguments for <see cref="WebContents.DidFailLoad"/>; Electron's <c>did-fail-load</c> parameters.</summary>
/// <param name="ErrorCode">Chromium's net error code (negative).</param>
/// <param name="ErrorDescription">Chromium's description of the error.</param>
/// <param name="ValidatedUrl">The URL that failed.</param>
/// <param name="IsMainFrame">Whether the main frame failed, rather than a subframe.</param>
public sealed record LoadFailedEventArgs(int ErrorCode, string ErrorDescription, string ValidatedUrl, bool IsMainFrame);
