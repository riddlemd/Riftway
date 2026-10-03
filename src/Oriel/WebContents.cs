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

    private cef_browser_t* _browser;
    private bool _ready;
    private string? _pendingUrl;
    private string? _mainFrameId;
    private int? _isolatedContext;
    // False until the app's first navigation: events from the initial about:blank are not the app's.
    private bool _navigated;
    private string _title = string.Empty;

    internal WebContents(string? appPreload)
    {
        // The app's preload gets its own scope, after Oriel's runtime has defined `oriel`.
        _isolatedScript = appPreload is null
            ? s_preloadRuntime
            : $"{s_preloadRuntime}\n;(() => {{\n{appPreload}\n}})();\n";
        _devTools.EventReceived += OnDevToolsEvent;

        Display = new DisplayHandler { TitleChanged = OnTitleChanged };
        Load = new LoadHandler
        {
            LoadingStateChanged = loading => Raise(loading ? DidStartLoading : DidStopLoading),
            LoadEnded = (isMain, _) => { if (isMain) Raise(DidFinishLoad); },
            LoadFailed = (isMain, code, text, url) =>
            {
                if (_navigated) DidFailLoad?.Invoke(this, new LoadFailedEventArgs(code, text, url, isMain));
            },
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
    public void LoadUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        CefThread.RunOnUi(() =>
        {
            // Until the preload is registered, a navigation would run the page without it.
            if (!_ready)
            {
                _pendingUrl = url;
                return;
            }

            _navigated = true;
            var frame = _browser->get_main_frame(_browser);
            cef_string_utf16_t value = default;
            CefString.Set(&value, url);
            frame->load_url(frame, &value);
            CefString.Clear(&value);
            NativeObject.Release(frame);
        });
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
        _devTools.Send("Runtime.enable");
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
            _mainFrameId = result.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
            _ready = true;
            if (_pendingUrl is { } url)
            {
                _pendingUrl = null;
                LoadUrl(url);
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

/// <summary>Arguments for <see cref="WebContents.DidFailLoad"/>; Electron's <c>did-fail-load</c> parameters.</summary>
/// <param name="ErrorCode">Chromium's net error code (negative).</param>
/// <param name="ErrorDescription">Chromium's description of the error.</param>
/// <param name="ValidatedUrl">The URL that failed.</param>
/// <param name="IsMainFrame">Whether the main frame failed, rather than a subframe.</param>
public sealed record LoadFailedEventArgs(int ErrorCode, string ErrorDescription, string ValidatedUrl, bool IsMainFrame);
