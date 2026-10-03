using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;
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

    private cef_browser_t* _browser;
    private bool _ready;
    private string? _pendingUrl;
    private string? _mainFrameId;
    private int? _isolatedContext;

    internal WebContents(string? appPreload)
    {
        // The app's preload gets its own scope, after Oriel's runtime has defined `oriel`.
        _isolatedScript = appPreload is null
            ? s_preloadRuntime
            : $"{s_preloadRuntime}\n;(() => {{\n{appPreload}\n}})();\n";
        _devTools.EventReceived += OnDevToolsEvent;
    }

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

    internal void OnBrowserDestroyed()
    {
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
