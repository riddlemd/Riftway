using System.Drawing;
using System.Text.Json;
using Oriel.Cef;

namespace Oriel;

/// <summary>The page's input events and preferred size: Electron's <c>input-event</c> and <c>preferred-size-changed</c>.</summary>
public sealed partial class WebContents
{
    private readonly Lock _inputGate = new();
    private EventHandler<WebInputEvent>? _inputEvent;

    // UI thread.
    private bool _pointerReporting;
    private bool _preferredSizeMode;
    private Size _preferredSize;
    private (double Width, double Height, double PixelRatio)? _measuredPreferredSize;

    /// <summary>An input event was sent to the page. Keys are reported as CEF forwards them, after
    /// <see cref="BeforeInputEvent"/> and only when it did not cancel them; mouse and wheel events as the main frame
    /// receives them, so they arrive a little later (drift C41). Electron's <c>input-event</c>.</summary>
    public event EventHandler<WebInputEvent>? InputEvent
    {
        add
        {
            lock (_inputGate) _inputEvent += value;
            SyncPointerReporting();
        }
        remove
        {
            lock (_inputGate) _inputEvent -= value;
            SyncPointerReporting();
        }
    }

    /// <summary>The page's preferred size changed: the smallest size, in DIPs, that holds its layout without scrolling.
    /// Raised only with <see cref="WebPreferences.EnablePreferredSizeMode"/>, from the end of the first load on, and
    /// only when the size differs from the last one raised (drift C42). Electron's <c>preferred-size-changed</c>.</summary>
    public event EventHandler<Size>? PreferredSizeChanged;

    // The page's main frame reports pointer events only while someone listens: every move crosses DevTools.
    private void SyncPointerReporting() => CefThread.RunOnUi(() =>
    {
        var on = _inputEvent is not null;
        if (on == _pointerReporting) return;
        _pointerReporting = on;
        if (_isolatedContext is { } context) Deliver(context, PointerReportingMessage(on));
    });

    private static string PointerReportingMessage(bool on) => $$"""{"t":"input","on":{{(on ? "true" : "false")}}}""";

    // UI thread: the main frame's preload world is new, so it starts with reporting off.
    private void OnMainIsolatedContext(int context)
    {
        if (_pointerReporting) Deliver(context, PointerReportingMessage(true));
    }

    // UI thread.
    private void OnPagePointerEvent(JsonElement[] args)
    {
        if (_inputEvent is not { } observer) return;
        Raise(observer, PagePointerEvent.Parse(args, GetZoomFactor()), "input-event");
    }

    // UI thread. CEF's keyboard handler never sees a char event DevTools dispatches, though Chromium forwards it to the
    // page as any other, so the one this page sends is reported here.
    private void ReportSentChar(KeyboardInputEvent sent)
    {
        if (_inputEvent is { } observer) Raise(observer, KeyInputMapper.SentChar(sent), "input-event");
    }

    // UI thread: Chromium sends its sizes once the page is in preferred-size mode, which Electron turns on when a load stops.
    // Only a page with EnablePreferredSizeMode measures (its preload is told so).
    private void OnPagePreferredSize(JsonElement[] args)
    {
        _measuredPreferredSize = (args[0].GetDouble(), args[1].GetDouble(), args[2].GetDouble());
        if (_preferredSizeMode) RaisePreferredSize();
    }

    // UI thread.
    private void OnStoppedLoadingForPreferredSize()
    {
        if (_preferredSizeMode) return;
        _preferredSizeMode = true;
        RaisePreferredSize();
    }

    // BrowserWindow::RequestPreferredWidth: GetPreferredSize's width, 0 until preferred-size mode is on.
    internal double PreferredWidth => _preferredSizeMode ? _preferredSize.Width : 0;

    private void RaisePreferredSize()
    {
        if (_measuredPreferredSize is not var (width, height, ratio)) return;
        var size = PreferredSizeMath.ToDips(width, height, ratio, GetZoomFactor());
        // WebContentsImpl reports only a change from the size it last had, which starts empty.
        if (size == _preferredSize) return;
        _preferredSize = size;
        Raise(PreferredSizeChanged, size, "preferred-size-changed");
    }
}

/// <summary>Electron's <c>InputEvent</c> for a pointer event the page's preload world saw.</summary>
internal static class PagePointerEvent
{
    /// <summary><paramref name="args"/>: <c>[type, modifier names]</c> for a wheel, else <c>[type, clientX, clientY,
    /// screenX, screenY, movementX, movementY, button, clickCount]</c>.</summary>
    internal static WebInputEvent Parse(IReadOnlyList<JsonElement> args, double zoomFactor)
    {
        var type = Enum.Parse<InputEventType>(args[0].GetString()!, ignoreCase: true);
        if (type is not (InputEventType.MouseDown or InputEventType.MouseUp or InputEventType.MouseMove or InputEventType.MouseEnter
            or InputEventType.MouseLeave or InputEventType.ContextMenu))
            return new WebInputEvent(type) { Modifiers = InputEventTypes.FromElectronNames(args[1].EnumerateArray().Select(n => n.GetString()!)) };
        // Chromium's widget positions are DIPs; the DOM's client positions are CSS pixels, smaller by the zoom.
        return new WebMouseEvent(type, args[1].GetDouble() * zoomFactor, args[2].GetDouble() * zoomFactor)
        {
            GlobalX = args[3].GetDouble(),
            GlobalY = args[4].GetDouble(),
            MovementX = (int)args[5].GetDouble(),
            MovementY = (int)args[6].GetDouble(),
            Button = Enum.Parse<MouseButton>(args[7].GetString()!, ignoreCase: true),
            ClickCount = args[8].GetInt32(),
        };
    }
}

/// <summary>Chromium's conversion of the preferred size to DIPs (<c>WebViewImpl::UpdatePreferredSize</c>).</summary>
internal static class PreferredSizeMath
{
    /// <summary>CSS pixels to Blink's rounded device pixels, then to DIPs, floored.</summary>
    internal static Size ToDips(double cssWidth, double cssHeight, double devicePixelRatio, double zoomFactor)
    {
        var deviceScale = devicePixelRatio / zoomFactor;
        // The epsilon absorbs the ratio's float error, which would floor an exact 200 to 199.
        int Dips(double css) => (int)Math.Floor(Math.Round(css * devicePixelRatio, MidpointRounding.AwayFromZero) / deviceScale + 1e-6);
        return new Size(Dips(cssWidth), Dips(cssHeight));
    }
}
