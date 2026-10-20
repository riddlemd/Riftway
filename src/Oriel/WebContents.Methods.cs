using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;

namespace Oriel;

/// <summary>Where <see cref="WebContents.InsertCssAsync"/> puts the sheet in the cascade; Electron's <c>cssOrigin</c>.</summary>
public enum CssOrigin
{
    /// <summary>The page's own origin, Electron's default.</summary>
    Author,

    /// <summary>The user's origin: below the page's rules, but its <c>!important</c> rules win. Applied as
    /// <see cref="Author"/> (drift C25).</summary>
    User,
}

/// <summary>Options for <see cref="WebContents.InsertCssAsync"/>.</summary>
public sealed record InsertCssOptions
{
    /// <summary>The sheet's cascade origin. Electron's <c>cssOrigin</c>.</summary>
    public CssOrigin CssOrigin { get; init; } = CssOrigin.Author;
}

/// <summary>Options for <see cref="WebContents.CapturePageAsync"/>; Electron's <c>capturePage</c> options. Both are
/// accepted but have no effect (drift C22).</summary>
public sealed record CapturePageOptions
{
    /// <summary>Keep a hidden page hidden while capturing. Electron's <c>stayHidden</c>.</summary>
    public bool StayHidden { get; init; }

    /// <summary>Keep the system awake while capturing. Electron's <c>stayAwake</c>.</summary>
    public bool StayAwake { get; init; }
}

/// <summary>How far <see cref="WebContents.AdjustSelection"/> moves each end of the selection, in characters;
/// negative is towards the start of the document. Electron's <c>adjustSelection</c> options.</summary>
public sealed record AdjustSelectionOptions
{
    /// <summary>Moves the start. Electron's <c>start</c>.</summary>
    public int Start { get; init; }

    /// <summary>Moves the end. Electron's <c>end</c>.</summary>
    public int End { get; init; }
}

/// <summary>A script for <see cref="WebContents.ExecuteJavaScriptInIsolatedWorldAsync"/>; Electron's <c>WebSource</c>.</summary>
/// <param name="Code">The script.</param>
/// <param name="Url">The URL stack traces name it by. Electron's <c>url</c>.</param>
public sealed record WebSource(string Code, string? Url = null);

/// <summary>The screen type <see cref="DeviceEmulationParameters"/> emulates; Electron's <c>screenPosition</c>.</summary>
public enum EmulatedScreenPosition
{
    /// <summary>A desktop screen.</summary>
    Desktop,

    /// <summary>A mobile screen.</summary>
    Mobile,
}

/// <summary>What <see cref="WebContents.EnableDeviceEmulation"/> emulates; Electron's <c>parameters</c>.</summary>
public sealed record DeviceEmulationParameters
{
    /// <summary>The screen type. Electron's <c>screenPosition</c>.</summary>
    public EmulatedScreenPosition ScreenPosition { get; init; } = EmulatedScreenPosition.Desktop;

    /// <summary>The emulated screen size. Electron's <c>screenSize</c>.</summary>
    public (int Width, int Height) ScreenSize { get; init; }

    /// <summary>Where the view is on the emulated screen; must lie on <see cref="ScreenSize"/> (drift C24).
    /// Electron's <c>viewPosition</c>.</summary>
    public Point? ViewPosition { get; init; }

    /// <summary>The device scale factor; 0 keeps the real one. Electron's <c>deviceScaleFactor</c>.</summary>
    public double DeviceScaleFactor { get; init; }

    /// <summary>The emulated view size; empty keeps the real one. Electron's <c>viewSize</c>.</summary>
    public (int Width, int Height) ViewSize { get; init; }

    /// <summary>The scale of the emulated view inside the available space. Electron's <c>scale</c>.</summary>
    public double Scale { get; init; } = 1;
}

/// <summary>The page's loading state, renderer control, muting, editing commands, capture, isolated worlds,
/// device emulation and injected CSS.</summary>
public sealed unsafe partial class WebContents
{
    // Electron's world for the preload under contextIsolation, which is Oriel's preload world.
    internal const int PreloadWorldId = 999;

    // Blink's isolated world ids end below 1 << 29.
    internal const int WorldIdLimit = 1 << 29;

    private const string ScriptFailedMessage = "Script failed to execute, this normally means an error was thrown. Check the renderer console for the error.";
    private const string FrameRemovedMessage = "WebFrame was removed before script could run. This normally means the underlying frame was destroyed";

    // UI thread: a main-frame navigation to another document has started and has not committed.
    private string? _pendingNavigationUrl;
    // UI thread: the main frame's committed document is loading.
    private bool _mainDocumentLoading;
    private bool _crashed;
    private bool _cssEnabled;

    private void RegisterMethods()
    {
        _devTools.EventReceived += OnMethodsDevToolsEvent;
        RenderProcessGone += (_, _) => _crashed = true;
        DidFailProvisionalLoad += (_, e) =>
        {
            if (e.IsMainFrame && e.ValidatedUrl == _pendingNavigationUrl) _pendingNavigationUrl = null;
        };
    }

    private void OnMethodsDevToolsEvent(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "Page.frameStartedNavigating" when IsMainFrameEvent(parameters):
                var type = parameters.TryGetProperty("navigationType", out var kind) ? kind.GetString() : null;
                if (type is not ("sameDocument" or "historySameDocument")) _pendingNavigationUrl = parameters.GetProperty("url").GetString();
                break;

            case "Page.frameStartedLoading" when IsMainFrameEvent(parameters):
                _mainDocumentLoading = true;
                break;

            // Committed: the response is in, as at Chromium's ready-to-commit.
            case "Page.frameNavigated" when parameters.GetProperty("frame").GetProperty("id").GetString() == _mainFrameId:
                _pendingNavigationUrl = null;
                break;

            case "Page.frameStoppedLoading" when IsMainFrameEvent(parameters):
                _mainDocumentLoading = false;
                break;

            // Chromium's crashed state ends when the main frame has a live renderer again.
            case "Inspector.targetReloadedAfterCrash":
                _crashed = false;
                break;
        }
    }

    private bool IsMainFrameEvent(JsonElement parameters)
        => parameters.TryGetProperty("frameId", out var id) && id.GetString() == _mainFrameId;

    /// <summary>Whether the page is loading: its main frame or any iframe. Electron's <c>webContents.isLoading()</c>.</summary>
    public bool IsLoading() => CefThread.InvokeOnUi(() => _browser is not null && _browser->is_loading(_browser) != 0);

    /// <summary>Whether the main frame is loading a new document: from the start of its navigation until the document
    /// has loaded, not counting iframes or in-page navigations; derived from DevTools (drift C19). Electron's
    /// <c>webContents.isLoadingMainFrame()</c>.</summary>
    public bool IsLoadingMainFrame() => CefThread.InvokeOnUi(()
        => _browser is not null && _browser->is_loading(_browser) != 0 && (_pendingNavigationUrl is not null || _mainDocumentLoading));

    /// <summary>Whether a main-frame navigation is waiting for its response, until it commits (drift C19). Electron's
    /// <c>webContents.isWaitingForResponse()</c>.</summary>
    public bool IsWaitingForResponse() => CefThread.InvokeOnUi(()
        => _browser is not null && _browser->is_loading(_browser) != 0 && _pendingNavigationUrl is not null);

    /// <summary>Stops any pending navigation and the page's loading. Electron's <c>webContents.stop()</c>.</summary>
    public void Stop() => CefThread.RunOnUi(() =>
    {
        if (_browser is not null) _browser->stop_load(_browser);
    });

    /// <summary>Whether the page's renderer process has ended, until a reload starts a new one. Electron's
    /// <c>webContents.isCrashed()</c>.</summary>
    public bool IsCrashed() => CefThread.InvokeOnUi(() => _crashed);

    /// <summary>Crashes the page's renderer process: <see cref="RenderProcessGone"/> follows, and a <see cref="Reload"/>
    /// starts a new process. Pages sharing the process crash with it; the reason is <c>crashed</c> on every OS (drift C27). Electron's <c>webContents.forcefullyCrashRenderer()</c>.</summary>
    public void ForcefullyCrashRenderer() => CefThread.RunOnUi(() =>
    {
        // Electron's Linux route: the renderer crashes itself on its IO thread, as DevTools' Page.crash does.
        if (_browser is not null) _devTools.Send("Page.crash");
    });

    /// <summary>Mutes or unmutes the page. Electron's <c>webContents.setAudioMuted()</c>.</summary>
    public void SetAudioMuted(bool muted) => WithHost(host => host->set_audio_muted(host, muted ? 1 : 0));

    /// <summary>Whether the page is muted. Electron's <c>webContents.isAudioMuted()</c> and <c>audioMuted</c>.</summary>
    public bool IsAudioMuted() => QueryHost(host => host->is_audio_muted(host) != 0, false);

    /// <summary>Types <paramref name="text"/> into the focused element, as an input method commits it. Electron's
    /// <c>webContents.insertText()</c>.</summary>
    public Task InsertTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (_browser is null)
            {
                done.TrySetException(new InvalidOperationException("The page has closed."));
                return;
            }
            _devTools.Send("Input.insertText", w => w.WriteString("text", text), (_, error) =>
            {
                if (error is null) done.TrySetResult();
                else done.TrySetException(new InvalidOperationException(error));
            });
        });
        return done.Task;
    }

    /// <summary>Replaces the selection, or the word at the caret, with <paramref name="text"/> in the focused frame.
    /// Electron's <c>webContents.replace()</c>; done by script (drift C20).</summary>
    public void Replace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        RunEditingScript(EditingScripts.Replace, JsonValue.Create(text));
    }

    /// <summary>Collapses the focused frame's selection to its end. Electron's <c>webContents.unselect()</c>; done by
    /// script (drift C20).</summary>
    public void Unselect() => RunEditingScript(EditingScripts.Unselect);

    /// <summary>Scrolls to the top of the focused element's scroller, else the document. Electron's
    /// <c>webContents.scrollToTop()</c>; done by script (drift C20).</summary>
    public void ScrollToTop() => RunEditingScript(EditingScripts.ScrollToEdge, JsonValue.Create(-1));

    /// <summary>Scrolls to the bottom of the focused element's scroller, else the document. Electron's
    /// <c>webContents.scrollToBottom()</c>; done by script (drift C20).</summary>
    public void ScrollToBottom() => RunEditingScript(EditingScripts.ScrollToEdge, JsonValue.Create(1));

    /// <summary>Scrolls the focused frame's selection to the middle of the view; macOS only, elsewhere it does nothing,
    /// as Electron's. Electron's <c>webContents.centerSelection()</c>.</summary>
    public void CenterSelection()
    {
        // Chromium compiles CenterSelection for macOS only.
        if (OperatingSystem.IsMacOS()) RunEditingScript(EditingScripts.CenterSelection);
    }

    /// <summary>Shows the system's Look Up panel for the selected word (macOS); does nothing elsewhere, as Electron's.
    /// Electron's <c>webContents.showDefinitionForSelection()</c>.</summary>
    public void ShowDefinitionForSelection()
    {
        if (!OperatingSystem.IsMacOS()) return;
        CefThread.RunOnUi(() =>
        {
            if (_browser is null || !OperatingSystem.IsMacOS()) return;
            var host = _browser->get_host(_browser);
            try { Oriel.Cef.Platform.MacLookUp.ShowDefinitionForSelection((nint)host->get_window_handle(host)); }
            finally { NativeObject.Release(host); }
        });
    }

    /// <summary>Moves the ends of the focused frame's selection by characters. Electron's
    /// <c>webContents.adjustSelection()</c>; done by script, and <c>end</c> is honoured (drift C20, C21).</summary>
    public void AdjustSelection(AdjustSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RunEditingScript(EditingScripts.AdjustSelection, JsonValue.Create(options.Start), JsonValue.Create(options.End));
    }

    /// <summary>Runs one of <see cref="EditingScripts"/> in the focused frame (the main frame when none is known), in
    /// Oriel's preload world so the page's own globals cannot interfere.</summary>
    internal Task RunEditingScript(string function, params JsonNode?[] args)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expression = $"({function})(...{new JsonArray(args).ToJsonString()})";
        CefThread.RunOnUi(() =>
        {
            if (_browser is null)
            {
                done.TrySetResult();
                return;
            }
            var frame = _focusedFrameId is { } id && _frames.TryGetValue(id, out var focused) && !focused.Detached ? focused
                : _mainFrameId is { } main && _frames.TryGetValue(main, out var top) ? top : null;
            var context = frame?.IsolatedContext ?? frame?.DefaultContext;
            _devTools.Send("Runtime.evaluate", w =>
            {
                w.WriteString("expression", expression);
                if (context is { } c) w.WriteNumber("contextId", c);
            }, (_, _) => done.TrySetResult());
        });
        return done.Task;
    }

    /// <summary>Captures the visible page, or <paramref name="rect"/> of it (DIPs, in the view), at the display's scale
    /// factor: the image's 1x representation holds the device pixels, as Electron's. Electron's
    /// <c>webContents.capturePage()</c>; <paramref name="options"/> has no effect (drift C22).</summary>
    /// <exception cref="InvalidOperationException">The page has closed, or nothing could be captured.</exception>
    public Task<NativeImage> CapturePageAsync(Rectangle? rect = null, CapturePageOptions? options = null)
    {
        var done = new TaskCompletionSource<NativeImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (_browser is null)
            {
                done.TrySetException(new InvalidOperationException("The page has closed."));
                return;
            }
            var zoom = QueryHost(host => Math.Pow(ZoomBase, host->get_zoom_level(host)), 1);
            var main = _mainFrameId is { } id && _frames.TryGetValue(id, out var f) ? f : null;
            _devTools.Send("Runtime.evaluate", w =>
            {
                w.WriteString("expression", "devicePixelRatio");
                if ((main?.IsolatedContext ?? main?.DefaultContext) is { } c) w.WriteNumber("contextId", c);
                w.WriteBoolean("returnByValue", true);
            }, (ratio, ratioError) =>
            {
                var pixelRatio = ratioError is null && ratio.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v)
                    && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 1;
                var scale = pixelRatio / zoom;
                // No clip: DevTools emulates and resizes the view for a clip, which the page would see.
                _devTools.Send("Page.captureScreenshot", w => w.WriteString("format", "png"), (shot, error) =>
                {
                    if (error is not null)
                    {
                        done.TrySetException(new InvalidOperationException(error));
                        return;
                    }
                    var image = NativeImage.CreateFromBuffer(Convert.FromBase64String(shot.GetProperty("data").GetString()!));
                    var (width, height) = image.GetSize();
                    done.TrySetResult(CapturePixels(rect, scale, width, height) is { } crop ? image.Crop(crop) : image);
                });
            });
        });
        return done.Task;
    }

    /// <summary>The device-pixel rectangle of the view's capture that <paramref name="rect"/> (DIPs) covers, clipped to
    /// the image; null for the whole image (no rect, or an empty one, as Electron's).</summary>
    internal static Rectangle? CapturePixels(Rectangle? rect, double scale, int imageWidth, int imageHeight)
    {
        if (rect is not { Width: > 0, Height: > 0 } area) return null;
        var left = (int)Math.Floor(area.X * scale);
        var top = (int)Math.Floor(area.Y * scale);
        var width = (int)Math.Ceiling(area.Width * scale);
        var height = (int)Math.Ceiling(area.Height * scale);
        var right = Math.Min(left + width, imageWidth);
        var bottom = Math.Min(top + height, imageHeight);
        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        return new Rectangle(left, top, Math.Max(right - left, 0), Math.Max(bottom - top, 0));
    }

    /// <summary>Runs <paramref name="scripts"/> in the main frame's world <paramref name="worldId"/>, in order, and returns
    /// the first one's value, awaiting it if it is a promise. World 0 is the page's main world and 999 the preload's,
    /// as in Electron; any other id is a world of its own, kept per document (drift C26). Waits for the main frame to load first.
    /// Electron's <c>webContents.executeJavaScriptInIsolatedWorld()</c>.</summary>
    /// <returns>The value as JSON; null for <c>undefined</c> or <c>null</c>.</returns>
    /// <exception cref="JavaScriptException">The first script threw, or its promise rejected.</exception>
    /// <exception cref="InvalidOperationException">No scripts, or the frame has no script context.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="worldId"/> is negative or beyond Blink's limit.</exception>
    public Task<JsonNode?> ExecuteJavaScriptInIsolatedWorldAsync(int worldId, IReadOnlyList<WebSource> scripts, bool userGesture = false)
        => ExecuteJavaScriptInIsolatedWorldInFrameAsync(worldId, scripts, userGesture, null);

    // A frame's (webFrame's) or, with none, the main frame's.
    internal Task<JsonNode?> ExecuteJavaScriptInIsolatedWorldInFrameAsync(int worldId, IReadOnlyList<WebSource> scripts, bool userGesture, FrameState? frame)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentOutOfRangeException.ThrowIfNegative(worldId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(worldId, WorldIdLimit);
        var sources = scripts.Select(s => WithSourceUrl(s ?? throw new ArgumentException("A script is null.", nameof(scripts)))).ToArray();
        var done = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = _destroyed.Token.Register(() => done.TrySetException(new InvalidOperationException("The page has closed.")));
        _ = done.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        WhenCanExecuteJavaScript(() =>
        {
            if (sources.Length == 0)
            {
                done.TrySetException(new InvalidOperationException(FrameRemovedMessage));
                return;
            }
            WithWorldContext(worldId, frame, (context, error) =>
            {
                if (error is not null) done.TrySetException(new InvalidOperationException(error));
                else RunInContext(context, sources, userGesture, done);
            });
        });
        return done.Task;
    }

    internal static string WithSourceUrl(WebSource source)
        => string.IsNullOrEmpty(source.Url) ? source.Code : $"{source.Code}\n//# sourceURL={source.Url}";

    /// <summary>Where a world id runs: the page's main world, Oriel's preload world, or a world of that name.</summary>
    internal static (bool Main, string? Name) WorldOf(int worldId) => worldId switch
    {
        0 => (true, null),
        PreloadWorldId => (false, IsolatedWorld),
        _ => (false, $"oriel-world-{worldId}"),
    };

    // Electron's executeJavaScript family waits until the main frame has a document and has finished loading it.
    private void WhenCanExecuteJavaScript(Action run) => CefThread.RunOnUi(() =>
    {
        if (_browser is not null && _navigated && GetUrl().Length > 0 && !IsLoadingMainFrame())
        {
            run();
            return;
        }
        EventHandler? once = null;
        once = (_, _) =>
        {
            DidStopLoading -= once;
            run();
        };
        DidStopLoading += once;
    });

    // UI thread.
    private void WithWorldContext(int worldId, FrameState? frame, Action<int?, string?> use)
    {
        // DevTools may not have reported a new document's contexts yet; without a context id it uses the main world.
        var (isMain, name) = WorldOf(worldId);
        if (isMain || (frame?.Id ?? _mainFrameId) is not { } frameId)
        {
            use(isMain ? frame?.DefaultContext : null, isMain ? null : "The page has no main frame yet.");
            return;
        }
        // Worlds are kept per frame by name, so the preload's world answers to its own name.
        _devTools.Send("Page.createIsolatedWorld", w =>
        {
            w.WriteString("frameId", frameId);
            w.WriteString("worldName", name);
        }, (result, error) => use(error is null ? result.GetProperty("executionContextId").GetInt32() : null, error));
    }

    private const string ObjectGroup = "oriel-isolated-world";

    // Each script runs as its own classic script, back to back, as Blink runs Electron's sources; the first one's value
    // is fetched by reference so the later scripts are not held up by its promise.
    private void RunInContext(int? context, string[] sources, bool userGesture, TaskCompletionSource<JsonNode?> done)
    {
        void Evaluate(string source, Action<JsonElement, string?>? onResult) => _devTools.Send("Runtime.evaluate", w =>
        {
            w.WriteString("expression", source);
            if (context is { } id) w.WriteNumber("contextId", id);
            w.WriteString("objectGroup", ObjectGroup);
            w.WriteBoolean("userGesture", userGesture);
        }, onResult);

        void Finish(Action settle)
        {
            settle();
            _devTools.Send("Runtime.releaseObjectGroup", w => w.WriteString("objectGroup", ObjectGroup));
        }

        void Settle(JsonElement result, string? error, bool fromPromise)
        {
            if (error is not null) Finish(() => done.TrySetException(new InvalidOperationException(error)));
            else if (result.TryGetProperty("exceptionDetails", out var details))
                Finish(() => done.TrySetException(new JavaScriptException(fromPromise ? DescribeException(details) : ScriptFailedMessage)));
            else Finish(() => done.TrySetResult(result.GetProperty("result").TryGetProperty("value", out var value) ? JsonNode.Parse(value.GetRawText()) : null));
        }

        Evaluate(sources[0], (first, error) =>
        {
            if (error is not null || first.TryGetProperty("exceptionDetails", out _))
            {
                Settle(first, error, fromPromise: false);
                return;
            }
            var remote = first.GetProperty("result");
            var objectId = remote.TryGetProperty("objectId", out var o) ? o.GetString() : null;
            if (objectId is null)
            {
                Settle(first, null, fromPromise: false);
                return;
            }
            if (remote.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "promise")
                _devTools.Send("Runtime.awaitPromise", w =>
                {
                    w.WriteString("promiseObjectId", objectId);
                    w.WriteBoolean("returnByValue", true);
                }, (settled, awaitError) => Settle(settled, awaitError, fromPromise: true));
            else
                _devTools.Send("Runtime.callFunctionOn", w =>
                {
                    w.WriteString("functionDeclaration", "function () { return this; }");
                    w.WriteString("objectId", objectId);
                    w.WriteBoolean("returnByValue", true);
                }, (byValue, valueError) => Settle(byValue, valueError, fromPromise: false));
        });
        foreach (var source in sources.Skip(1)) Evaluate(source, null);
    }

    /// <summary>Emulates a device: screen type and size, view position and size, scale factor and scale (drift C24). Electron's
    /// <c>webContents.enableDeviceEmulation()</c>.</summary>
    public void EnableDeviceEmulation(DeviceEmulationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var overrides = DeviceMetricsOverride(parameters);
        CefThread.RunOnUi(() => _devTools.Send("Emulation.setDeviceMetricsOverride", w =>
        {
            foreach (var (name, value) in overrides)
            {
                w.WritePropertyName(name);
                value!.WriteTo(w);
            }
        }));
    }

    /// <summary>Ends <see cref="EnableDeviceEmulation"/>. Electron's <c>webContents.disableDeviceEmulation()</c>.</summary>
    public void DisableDeviceEmulation() => CefThread.RunOnUi(() => _devTools.Send("Emulation.clearDeviceMetricsOverride"));

    /// <summary>Electron's <c>blink::DeviceEmulationParams</c> as DevTools' <c>Emulation.setDeviceMetricsOverride</c>.</summary>
    internal static JsonObject DeviceMetricsOverride(DeviceEmulationParameters p)
    {
        var overrides = new JsonObject
        {
            ["width"] = p.ViewSize.Width,
            ["height"] = p.ViewSize.Height,
            ["deviceScaleFactor"] = p.DeviceScaleFactor,
            ["mobile"] = p.ScreenPosition == EmulatedScreenPosition.Mobile,
            ["scale"] = p.Scale,
            ["screenWidth"] = p.ScreenSize.Width,
            ["screenHeight"] = p.ScreenSize.Height,
        };
        if (p.ViewPosition is { } position)
        {
            overrides["positionX"] = position.X;
            overrides["positionY"] = position.Y;
        }
        return overrides;
    }

    /// <summary>Adds <paramref name="css"/> to the main frame's current document, in a sheet the page cannot see in
    /// <c>document.styleSheets</c>; it goes with the document (drift C25). Returns the key for <see cref="RemoveInsertedCssAsync"/>.
    /// Electron's <c>webContents.insertCSS()</c>.</summary>
    /// <exception cref="InvalidOperationException">The page has closed or has no document.</exception>
    public Task<string> InsertCssAsync(string css, InsertCssOptions? options = null) => InsertCssInFrameAsync(css, null);

    // A frame's (webFrame's) or, with none, the main frame's.
    internal Task<string> InsertCssInFrameAsync(string css, FrameState? frame)
    {
        ArgumentNullException.ThrowIfNull(css);
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (_browser is null || (frame?.Id ?? _mainFrameId) is not { } frameId)
            {
                done.TrySetException(new InvalidOperationException("The page has closed."));
                return;
            }
            WithCss(error =>
            {
                if (error is not null)
                {
                    done.TrySetException(new InvalidOperationException(error));
                    return;
                }
                // An inspector sheet: StyleEngine keeps it outside the document's sheet list.
                _devTools.Send("CSS.createStyleSheet", w =>
                {
                    w.WriteString("frameId", frameId);
                    w.WriteBoolean("force", true);
                }, (created, createError) =>
                {
                    if (createError is not null)
                    {
                        done.TrySetException(new InvalidOperationException(createError));
                        return;
                    }
                    var key = created.GetProperty("styleSheetId").GetString()!;
                    _devTools.Send("CSS.setStyleSheetText", w =>
                    {
                        w.WriteString("styleSheetId", key);
                        w.WriteString("text", css);
                    }, (_, setError) =>
                    {
                        if (setError is null) done.TrySetResult(key);
                        else done.TrySetException(new InvalidOperationException(setError));
                    });
                });
            });
        });
        return done.Task;
    }

    /// <summary>Removes CSS that <see cref="InsertCssAsync"/> added; an unknown key is ignored, as Electron's.
    /// Electron's <c>webContents.removeInsertedCSS()</c>.</summary>
    public Task RemoveInsertedCssAsync(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (_browser is null || !_cssEnabled)
            {
                done.TrySetResult();
                return;
            }
            _devTools.Send("CSS.setStyleSheetText", w =>
            {
                w.WriteString("styleSheetId", key);
                w.WriteString("text", "");
            }, (_, _) => done.TrySetResult());
        });
        return done.Task;
    }

    // UI thread: the CSS agent needs the DOM agent, and both stay on for the DevTools session.
    private void WithCss(Action<string?> then)
    {
        if (_cssEnabled)
        {
            then(null);
            return;
        }
        _devTools.Send("DOM.enable");
        _devTools.Send("CSS.enable", onResult: (_, error) =>
        {
            if (error is null) _cssEnabled = true;
            then(error);
        });
    }
}

/// <summary>Scripts for Electron's editing commands that CEF does not expose; each is a function taking its
/// arguments, run in the focused frame. Blink's commands work on the frame's selection, which inside a text field is
/// the field's own.</summary>
internal static class EditingScripts
{
    private const string TextControl = "const field = document.activeElement; const inField = !!field && (field.tagName === 'INPUT' || field.tagName === 'TEXTAREA') && field.selectionStart !== null;";

    // Blink's CollapseSelection: a caret at the selection's end.
    internal const string Unselect = "() => { " + TextControl + @"
        if (inField) { const end = field.selectionEnd; field.setSelectionRange(end, end); return; }
        const s = getSelection();
        if (!s || s.rangeCount === 0) return;
        const r = s.getRangeAt(0);
        s.collapse(r.endContainer, r.endOffset);
    }";

    // Blink's AdjustSelectionByCharacterOffset, with its checks against an empty or negative result.
    internal const string AdjustSelection = "(start, end) => { " + TextControl + @"
        if (inField) {
            const from = field.selectionStart, length = field.selectionEnd - from;
            if (start - end > length || from + start < 0) return;
            field.setSelectionRange(from + start, from + start + length + end - start);
            return;
        }
        const s = getSelection();
        if (!s || s.rangeCount === 0) return;
        const r = s.getRangeAt(0).cloneRange();
        if (start - end > r.toString().length) return;
        const move = (node, offset, by) => {
            s.collapse(node, offset);
            for (let i = 0; i < Math.abs(by); i++) {
                const before = [s.focusNode, s.focusOffset];
                s.modify('move', by < 0 ? 'backward' : 'forward', 'character');
                if (s.focusNode === before[0] && s.focusOffset === before[1]) return null;
            }
            return [s.focusNode, s.focusOffset];
        };
        const a = move(r.startContainer, r.startOffset, start);
        const b = a && move(r.endContainer, r.endOffset, end);
        if (!a || !b) { s.removeAllRanges(); s.addRange(r); return; }
        s.setBaseAndExtent(a[0], a[1], b[0], b[1]);
    }";

    // Blink's Replace: with no selection, the word around the caret is selected first; then the selection is replaced.
    internal const string Replace = "(text) => { " + TextControl + @"
        const wordAt = (value, at) => {
            const words = [...new Intl.Segmenter(undefined, { granularity: 'word' }).segment(value)].filter(w => w.isWordLike);
            const word = words.find(w => w.index <= at && at < w.index + w.segment.length) || words.find(w => w.index + w.segment.length === at);
            return word ? [word.index, word.index + word.segment.length] : null;
        };
        if (inField) {
            if (field.selectionStart === field.selectionEnd) {
                const word = wordAt(field.value, field.selectionStart);
                if (word) field.setSelectionRange(word[0], word[1]);
            }
        } else {
            const s = getSelection();
            if (!s || s.rangeCount === 0) return;
            if (s.isCollapsed && s.anchorNode && s.anchorNode.nodeType === Node.TEXT_NODE) {
                const word = wordAt(s.anchorNode.data, s.anchorOffset);
                if (word) s.setBaseAndExtent(s.anchorNode, word[0], s.anchorNode, word[1]);
            }
        }
        document.execCommand('insertText', false, text);
    }";

    // Blink's ScrollToBeginning/EndOfDocument: the nearest scroller from the focused element that can still move, else
    // the document, in the block direction.
    internal const string ScrollToEdge = @"(direction) => {
        const down = direction > 0;
        const canMove = el => {
            const overflow = getComputedStyle(el).overflowY;
            if (!/^(auto|scroll|overlay)$/.test(overflow) || el.scrollHeight <= el.clientHeight) return false;
            return down ? el.scrollTop + el.clientHeight < el.scrollHeight : el.scrollTop > 0;
        };
        for (let el = document.activeElement; el && el !== document.body && el !== document.documentElement; el = el.parentElement) {
            if (canMove(el)) { el.scrollTo({ top: down ? el.scrollHeight : 0, behavior: 'instant' }); return; }
        }
        const root = document.scrollingElement || document.documentElement;
        window.scrollTo({ left: scrollX, top: down ? root.scrollHeight : 0, behavior: 'instant' });
    }";

    // Blink's CenterSelection: reveal a selected range in the middle of the view.
    internal const string CenterSelection = "() => { " + TextControl + @"
        if (inField) { if (field.selectionStart !== field.selectionEnd) field.scrollIntoView({ block: 'center', inline: 'center' }); return; }
        const s = getSelection();
        if (!s || s.rangeCount === 0 || s.isCollapsed) return;
        const rect = s.getRangeAt(0).getBoundingClientRect();
        window.scrollBy({ left: rect.left + rect.width / 2 - innerWidth / 2, top: rect.top + rect.height / 2 - innerHeight / 2, behavior: 'instant' });
    }";
}
